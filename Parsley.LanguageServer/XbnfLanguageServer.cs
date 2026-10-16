using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// The XBNF language server: syntax diagnostics as you type, the full grammar analysis when a file opens and when
    /// you save, a glyph
    /// at each decision that needs more than one token of lookahead, and go to definition, find references,
    /// completion and hover for rule names
    /// </summary>
    sealed class XbnfLanguageServer
    {
        const int LiveDelayMs = 250;

        readonly JsonRpc _rpc;
        readonly Documents _docs = new Documents();
        // diagnostics from the last full analysis of each file, shown until it's next edited
        readonly ConcurrentDictionary<string, JsonArray> _analysis = new ConcurrentDictionary<string, JsonArray>(Documents.PathComparer);
        // the latest pending work for each file, so an edit cancels an analysis or live check that's out of date
        readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new ConcurrentDictionary<string, CancellationTokenSource>(Documents.PathComparer);
        // the decisions needing more than one token that the last full analysis of each file found
        readonly ConcurrentDictionary<string, List<DecisionHint>> _hints = new ConcurrentDictionary<string, List<DecisionHint>>(Documents.PathComparer);
        // what the client said it can do: show inlay hints, and be asked to fetch them again after a save
        bool _inlayHints;
        bool _inlayHintRefresh;
        // a client that draws the glyphs itself, from parsley/decisionHints, and wants parsley/decisionHintsChanged
        // when they change: the Visual Studio extension, since Visual Studio's language client doesn't show inlay hints
        bool _customHints;
        int _requestId;
        bool _shutdown;

        public XbnfLanguageServer(JsonRpc rpc) { _rpc = rpc; }

        public async Task<int> RunAsync()
        {
            while (true)
            {
                JsonObject? msg;
                try
                {
                    msg = await _rpc.ReadAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException || ex is System.Text.Json.JsonException || ex is FormatException)
                {
                    Console.Error.WriteLine("xbnf-lsp: bad message: " + ex.Message);
                    continue;
                }
                if (null == msg) return _shutdown ? 0 : 1;
                var method = (string?)msg["method"];
                var id = msg["id"];
                var p = msg["params"] as JsonObject;
                if (null == method) continue; // a response to something we never send
                if (method == "exit") return _shutdown ? 0 : 1;
                try
                {
                    var result = _Handle(method, p, out var handled);
                    if (null != id)
                    {
                        if (handled) await _rpc.RespondAsync(id, result);
                        else await _rpc.RespondErrorAsync(id, -32601, $"Method not found: {method}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"xbnf-lsp: {method} failed: {ex}");
                    if (null != id) await _rpc.RespondErrorAsync(id, -32603, ex.Message);
                }
            }
        }

        JsonNode? _Handle(string method, JsonObject? p, out bool handled)
        {
            handled = true;
            switch (method)
            {
                case "initialize": return _Initialize(p);
                case "initialized": return null;
                case "shutdown": _shutdown = true; return null;
                case "textDocument/didOpen": _DidOpen(p!); return null;
                case "textDocument/didChange": _DidChange(p!); return null;
                case "textDocument/didSave": _DidSave(p!); return null;
                case "textDocument/didClose": _DidClose(p!); return null;
                case "textDocument/definition": return _Definition(p!);
                case "textDocument/references": return _References(p!);
                case "textDocument/completion": return _Completion(p!);
                case "textDocument/hover": return _Hover(p!);
                case "textDocument/documentHighlight": return _Highlights(p!);
                case "textDocument/inlayHint": return _InlayHints(p!);
                case "parsley/decisionHints": return _CustomHints(p!);
                default:
                    // other notifications ($/cancelRequest, settings changes and so on) need nothing
                    handled = method.StartsWith("$/", StringComparison.Ordinal) || method.StartsWith("workspace/", StringComparison.Ordinal);
                    return null;
            }
        }

        JsonObject _Initialize(JsonObject? p)
        {
            var caps = p?["capabilities"];
            _inlayHints = null != caps?["textDocument"]?["inlayHint"];
            _inlayHintRefresh = (bool?)caps?["workspace"]?["inlayHint"]?["refreshSupport"] ?? false;
            _customHints = p?["initializationOptions"] is JsonObject o && ((bool?)o["decisionHints"] ?? false);
            return _Capabilities();
        }

        static JsonObject _Capabilities() => new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                // the whole text on each change: grammars are small, and the reader parses from scratch anyway
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1, ["save"] = new JsonObject { ["includeText"] = false } },
                ["definitionProvider"] = true,
                ["referencesProvider"] = true,
                ["hoverProvider"] = true,
                ["documentHighlightProvider"] = true,
                ["inlayHintProvider"] = true,
                ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("<", ",", "@", "=", " ", "|", "(", "[", "{") },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "Parsley XBNF", ["version"] = typeof(XbnfLanguageServer).Assembly.GetName().Version?.ToString() ?? "1.0" },
        };

        #region Documents
        static string _Path(JsonObject p) => Documents.ToPath((string)p["textDocument"]!["uri"]!);

        void _DidOpen(JsonObject p)
        {
            var td = (JsonObject)p["textDocument"]!;
            var path = _Path(p);
            _docs.Open(path, (string?)td["text"] ?? "", (int?)td["version"] ?? 0);
            _analysis.TryRemove(path, out _);
            // the full analysis straight away, so the glyphs and the analysis's messages are there without a save
            _Schedule(path, full: true, delay: 0);
        }

        void _DidChange(JsonObject p)
        {
            var path = _Path(p);
            var changes = p["contentChanges"] as JsonArray;
            // full sync: the last change holds the whole text
            var text = (string?)changes?.LastOrDefault()?["text"];
            if (null == text) return;
            _docs.Open(path, text, (int?)p["textDocument"]!["version"] ?? 0);
            // the analysis's positions no longer line up with the text
            _analysis.TryRemove(path, out _);
            _Schedule(path, full: false, delay: LiveDelayMs);
        }

        void _DidSave(JsonObject p) => _Schedule(_Path(p), full: true, delay: 0);

        void _DidClose(JsonObject p)
        {
            var path = _Path(p);
            _docs.Close(path);
            _analysis.TryRemove(path, out _);
            _hints.TryRemove(path, out _);
            if (_pending.TryRemove(path, out var cts)) cts.Cancel();
            _ = _rpc.NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = Documents.ToUri(path), ["diagnostics"] = new JsonArray() });
        }
        #endregion

        #region Diagnostics
        // runs a check in the background, replacing any that's pending for the file
        void _Schedule(string path, bool full, int delay)
        {
            var cts = new CancellationTokenSource();
            _pending.AddOrUpdate(path, cts, (_, prev) => { prev.Cancel(); return cts; });
            var ct = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > 0) await Task.Delay(delay, ct);
                    var doc = _docs.GetOpen(path);
                    if (null == doc) return;
                    var diags = _Check(doc, full, ct);
                    if (ct.IsCancellationRequested || _docs.GetOpen(path)?.Version != doc.Version) return;
                    await _rpc.NotifyAsync("textDocument/publishDiagnostics", new JsonObject
                    {
                        ["uri"] = Documents.ToUri(path),
                        ["version"] = doc.Version,
                        ["diagnostics"] = diags,
                    });
                    // the analysis has new hints; an edit makes the client ask anyway, but a save doesn't
                    if (full && _inlayHints && _inlayHintRefresh)
                        await _rpc.WriteAsync(new JsonObject { ["id"] = "parsley-" + Interlocked.Increment(ref _requestId), ["method"] = "workspace/inlayHint/refresh" });
                    if (full && _customHints)
                        await _rpc.NotifyAsync("parsley/decisionHintsChanged", new JsonObject { ["uri"] = Documents.ToUri(path) });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine($"xbnf-lsp: checking {path} failed: {ex}"); }
            }, ct);
        }

        JsonArray _Check(Document doc, bool full, CancellationToken ct)
        {
            var result = new JsonArray();
            var seen = new HashSet<string>();
            void add(GrammarMessage m)
            {
                var d = _ToDiagnostic(doc, m);
                if (seen.Add(d.ToJsonString())) result.Add(d);
            }
            var messages = XbnfParser.TryParse(doc.Text, doc.Path, _docs.ReadForImport, out var grammar);
            foreach (var m in messages) add(m);
            ct.ThrowIfCancellationRequested();
            if (!full && null != grammar && !messages.Any(m => m.Level == ErrorLevel.Error))
            {
                // the analysis's first, cheap step: undefined names, rules defined twice and the like
                var g = grammar.Clone();
                g.ResolveLiterals();
                foreach (var m in g.Validate()) add(m);
            }
            if (full)
            {
                var analysis = new JsonArray();
                if (null != grammar && !messages.Any(m => m.Level == ErrorLevel.Error))
                {
                    // the analysis can take a while on a big grammar; a newer edit cancels this one, though the
                    // analysis itself runs to the end before that's noticed
                    var a = GrammarAnalysis.Analyze(grammar);
                    ct.ThrowIfCancellationRequested();
                    // and the symbol constants the synthesizer would have to rename, which only it decides
                    var naming = a.HasErrors ? Array.Empty<GrammarMessage>() : CSharpSynthesizer.CheckNames(a);
                    foreach (var m in a.Messages.Concat(naming))
                    {
                        var d = _ToDiagnostic(doc, m);
                        if (seen.Add(d.ToJsonString())) { result.Add(d); analysis.Add(d.DeepClone()); }
                    }
                    var hints = DecisionHint.From(a, doc);
                    _hints[doc.Path] = hints;
                    // a client that can't show inlay hints gets them as hint diagnostics, which editors draw
                    // faintly, if at all, and keep out of the problem list. ALL(*) decisions have a message already.
                    if (!_inlayHints && !_customHints)
                        foreach (var h in hints.Where(h => !h.IsAllStar))
                        {
                            var d = _HintDiagnostic(doc, h);
                            if (null != d && seen.Add(d.ToJsonString())) { result.Add(d); analysis.Add(d.DeepClone()); }
                        }
                }
                else _hints.TryRemove(doc.Path, out _);
                _analysis[doc.Path] = analysis;
            }
            else if (_analysis.TryGetValue(doc.Path, out var kept))
            {
                foreach (var d in kept)
                    if (seen.Add(d!.ToJsonString())) result.Add(d.DeepClone());
            }
            return result;
        }

        JsonObject _ToDiagnostic(Document doc, GrammarMessage m)
        {
            var severity = m.Level switch { ErrorLevel.Error => 1, ErrorLevel.Warning => 2, _ => 3 };
            var message = m.Message;
            int start;
            int end;
            if (null != m.Filename && !Documents.PathComparer.Equals(Path.GetFullPath(m.Filename), doc.Path))
            {
                // a problem in an @imported file: on the @import, naming the file
                message = $"{Path.GetFileName(m.Filename)}({m.Line},{m.Column}): {m.Message}";
                var imp = doc.Scan.Imports.Select(i => i.At).FirstOrDefault(t =>
                {
                    try { return Documents.PathComparer.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(doc.Path) ?? "", t.Text.Trim('"'))), Path.GetFullPath(m.Filename)); }
                    catch (ArgumentException) { return false; }
                }) ?? doc.Scan.Imports.Select(i => i.At).FirstOrDefault();
                start = imp?.Start ?? 0;
                end = imp?.End ?? 0;
            }
            else if (m.Line > 0 || m.Position >= 0)
            {
                start = doc.ToOffset(m);
                // the word or token there, so it's easy to see
                var t = doc.Scan.TokenAt(start);
                end = null != t && t.Start == start ? t.End : Math.Min(doc.Text.Length, start + 1);
            }
            else
            {
                start = 0;
                end = 0;
            }
            return new JsonObject
            {
                ["range"] = doc.Range(start, end),
                ["severity"] = severity,
                ["source"] = "Parsley",
                ["message"] = message,
            };
        }
        #endregion

        #region Decision hints
        JsonNode? _InlayHints(JsonObject p)
        {
            var path = _Path(p);
            var doc = _docs.GetOpen(path);
            var arr = new JsonArray();
            if (null == doc || !_hints.TryGetValue(path, out var hints)) return arr;
            var range = p["range"] as JsonObject;
            var from = null == range ? 0 : doc.ToOffset((int)range["start"]!["line"]!, (int)range["start"]!["character"]!);
            var to = null == range ? doc.Text.Length : doc.ToOffset((int)range["end"]!["line"]!, (int)range["end"]!["character"]!);
            foreach (var h in hints)
            {
                var offset = h.OffsetIn(doc);
                if (offset >= from && offset <= to) arr.Add(h.ToInlayHint(doc, offset));
            }
            return arr;
        }

        // the hints where they are now, for a client that draws them itself: zero-based line and UTF-16 character,
        // the glyph, whether it's ALL(*), k, and the tooltip as plain text
        JsonNode? _CustomHints(JsonObject p)
        {
            var path = _Path(p);
            var doc = _docs.GetOpen(path);
            var arr = new JsonArray();
            if (null == doc || !_hints.TryGetValue(path, out var hints)) return arr;
            foreach (var h in hints)
            {
                var offset = h.OffsetIn(doc);
                if (offset < 0) continue;
                var (line, character) = doc.ToPosition(offset);
                arr.Add(new JsonObject
                {
                    ["line"] = line,
                    ["character"] = character,
                    ["label"] = h.Label,
                    ["allStar"] = h.IsAllStar,
                    ["depth"] = h.Depth,
                    ["tooltip"] = _PlainText(h.Tooltip, paragraphs: true),
                });
            }
            return arr;
        }

        // the hints at the token that starts at the offset
        IEnumerable<DecisionHint> _HintsAt(Document doc, int offset)
        {
            if (!_hints.TryGetValue(doc.Path, out var hints)) return Enumerable.Empty<DecisionHint>();
            return hints.Where(h => h.OffsetIn(doc) == offset);
        }

        static JsonObject? _HintDiagnostic(Document doc, DecisionHint h)
        {
            var start = h.OffsetIn(doc);
            if (start < 0) return null;
            var t = doc.Scan.TokenAt(start);
            var end = null != t && t.Start == start ? t.End : Math.Min(doc.Text.Length, start + 1);
            return new JsonObject
            {
                ["range"] = doc.Range(start, end),
                ["severity"] = 4,
                ["source"] = "Parsley",
                ["message"] = _PlainText(h.Tooltip),
            };
        }

        // a tooltip's markdown as plain text: no bold, italics or code marks, and an escaped * kept
        static string _PlainText(string markdown, bool paragraphs = false) => markdown
            .Replace("\\*", "\u0001").Replace("**", "").Replace("*", "").Replace("`", "").Replace("\u0001", "*")
            .Replace("\n\n", paragraphs ? "\n" : " ");
        #endregion

        #region Navigation
        (Document Doc, Token? Token) _At(JsonObject p)
        {
            var path = _Path(p);
            var doc = _docs.Get(path) ?? new Document(path, "", 0);
            var pos = (JsonObject)p["position"]!;
            var offset = doc.ToOffset((int)pos["line"]!, (int)pos["character"]!);
            var t = doc.Scan.TokenAt(offset);
            // at the end of one token and the start of the next, prefer the identifier, or the literal or pattern
            if (null != t && t.Kind != TokenKind.Identifier && t.End == offset)
            {
                var after = doc.Scan.TokenAt(offset + 1);
                if (null != after && after.Start == offset && (after.Kind == TokenKind.Identifier || (!_IsQuoted(t) && _IsQuoted(after)))) t = after;
            }
            return (doc, t);
        }

        static bool _IsRuleName(Token? t) => null != t && t.Kind == TokenKind.Identifier && (t.Role == IdentifierRole.Definition || t.Role == IdentifierRole.Reference);
        static bool _IsQuoted(Token? t) => null != t && (t.Kind == TokenKind.Literal || t.Kind == TokenKind.Pattern);

        // the rule a token names: a rule name as written, or for a "literal" or 'pattern' in a rule, the terminal
        // declared with the same text, which is what the reader resolves it to. Null for anything else, including a
        // literal with no declaration, since that's an implicit terminal with nowhere to go.
        string? _SymbolName(Document doc, Token? t)
        {
            if (_IsRuleName(t)) return t!.Text;
            if (!_IsQuoted(t)) return null;
            return doc.LiteralTargets(_docs.ReadForImport).TryGetValue(t!.Start, out var name) ? name : null;
        }

        // a "literal" or 'pattern' in a rule that has no declaration of its own
        bool _IsImplicitLiteral(Document doc, Token? t) =>
            _IsQuoted(t) && doc.LiteralTargets(_docs.ReadForImport).TryGetValue(t!.Start, out var name) && null == name;

        // definitions of a name across the grammar and what it imports; the first wins, as in the reader
        List<(Document Doc, Token Token)> _FindDefinitions(Document doc, string name)
        {
            var result = new List<(Document, Token)>();
            foreach (var d in _docs.WithImports(doc))
                foreach (var t in d.Scan.Definitions)
                    if (t.Text == name) result.Add((d, t));
            return result;
        }

        static JsonObject _Location(Document d, int start, int end) => new JsonObject { ["uri"] = Documents.ToUri(d.Path), ["range"] = d.Range(start, end) };

        JsonNode? _Definition(JsonObject p)
        {
            var (doc, t) = _At(p);
            var name = _SymbolName(doc, t);
            if (null == name) return null;
            var defs = _FindDefinitions(doc, name);
            if (0 == defs.Count) return null;
            var arr = new JsonArray();
            foreach (var (d, def) in defs) arr.Add(_Location(d, def.Start, def.End));
            return arr;
        }

        JsonNode? _References(JsonObject p)
        {
            var (doc, t) = _At(p);
            var name = _SymbolName(doc, t);
            if (null == name) return null;
            var includeDeclaration = (bool?)p["context"]?["includeDeclaration"] ?? true;
            var arr = new JsonArray();
            // the rule's name wherever it's written, and the literals and patterns that resolve to it
            foreach (var d in _docs.Related(doc))
                foreach (var tok in d.Scan.Tokens)
                    if ((includeDeclaration || tok.Role != IdentifierRole.Definition) && name == _SymbolName(d, tok))
                        arr.Add(_Location(d, tok.Start, tok.End));
            return arr;
        }

        JsonNode? _Highlights(JsonObject p)
        {
            var (doc, t) = _At(p);
            var name = _SymbolName(doc, t);
            if (null == name) return null;
            var arr = new JsonArray();
            foreach (var tok in doc.Scan.Tokens)
                if (name == _SymbolName(doc, tok))
                    arr.Add(new JsonObject { ["range"] = doc.Range(tok.Start, tok.End), ["kind"] = tok.Role == IdentifierRole.Definition ? 3 : 2 });
            return arr;
        }
        #endregion

        #region Hover
        JsonNode? _Hover(JsonObject p)
        {
            var (doc, t) = _At(p);
            if (null == t) return null;
            string? markdown = null;
            var name = _SymbolName(doc, t);
            if (null != name)
            {
                var defs = _FindDefinitions(doc, name);
                if (0 < defs.Count)
                {
                    markdown = _RuleHover(defs[0].Doc, defs[0].Token, doc);
                    var kind = _RuleKind(defs[0].Doc, name);
                    if (null != kind) markdown += "\n\n" + kind;
                }
                else markdown = $"`{name}` isn't defined";
            }
            else if (_IsImplicitLiteral(doc, t))
                markdown = "*An implicit terminal: nothing declares this text, so it's a terminal of its own. Declare it, as in* `name= " + t.Text + ";`*, to name it or give it attributes.*";
            else if (t.Kind == TokenKind.Identifier && t.Role == IdentifierRole.AttributeName && XbnfKnowledge.Attributes.TryGetValue(t.Text, out var attr))
                markdown = $"**{t.Text}** — {attr}";
            else if (t.Kind == TokenKind.Directive && XbnfKnowledge.Directives.TryGetValue(t.Text.Substring(1), out var dir))
                markdown = $"**{t.Text}** — {dir}";
            // a decision that starts here and needs more than one token: what the glyph says, for editors that
            // don't show it. One that shows the glyph shows this as the glyph's tooltip already.
            if (!_inlayHints && !_customHints)
                foreach (var h in _HintsAt(doc, t.Start))
                    markdown = (null == markdown ? "" : markdown + "\n\n---\n\n") + h.Tooltip;
            if (null == markdown) return null;
            return new JsonObject
            {
                ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown },
                ["range"] = doc.Range(t.Start, t.End),
            };
        }

        // what the reader made of a rule: a terminal or not, and what kind. Only a lone literal or pattern is a
        // terminal unless the rule says otherwise, so this is worth seeing
        string? _RuleKind(Document d, string name)
        {
            var g = d.ReadGrammar(_docs.ReadForImport);
            if (null == g) return null;
            var t = g.GetTerminal(name);
            if (null != t)
            {
                if (t.IsIsland) return $"*An island, parsed by {t.Entry}*";
                if (t.IsContextual) return $"*A contextual terminal, read from {t.ContextualBase} when {t.When} says so*";
                if (null != t.Lexer) return $"*A terminal from the {t.Lexer} lexer*";
                return t.IsHidden ? "*A hidden terminal*" : "*A terminal*";
            }
            var p = g.GetProduction(name);
            if (null == p) return null;
            var what = p.Attributes.IsSet("shared") ? "A shared non-terminal" : name == g.StartSymbol ? "The start symbol, a non-terminal" : "A non-terminal";
            return $"*{what}*";
        }

        // the rule as written, with its action shortened, and the comment lines right above it
        static string _RuleHover(Document d, Token def, Document from)
        {
            var text = d.Text;
            var end = def.RuleEnd > def.Start ? def.RuleEnd : def.End;
            var sb = new StringBuilder();
            var pos = def.Start;
            foreach (var tok in d.Scan.Tokens)
            {
                if (tok.Start < def.Start || tok.Start >= end) continue;
                if (tok.Kind == TokenKind.Action)
                {
                    sb.Append(text, pos, tok.Start - pos).Append("=> { … }");
                    pos = tok.End;
                }
            }
            if (pos < end) sb.Append(text, pos, end - pos);
            var rule = sb.ToString().Trim();
            const int max = 1200;
            if (rule.Length > max) rule = rule.Substring(0, max) + " …";

            var comments = new List<string>();
            var (line, _) = d.ToPosition(def.Start);
            var lines = text.Split('\n');
            for (var l = line - 1; l >= 0; --l)
            {
                var s = lines[l].Trim();
                if (!s.StartsWith("//", StringComparison.Ordinal)) break;
                comments.Insert(0, s.Substring(2).Trim());
            }

            var md = new StringBuilder();
            md.Append("```xbnf\n").Append(rule).Append("\n```");
            if (0 < comments.Count) md.Append("\n\n").Append(string.Join("  \n", comments));
            if (!Documents.PathComparer.Equals(d.Path, from.Path))
                md.Append("\n\n*From ").Append(Path.GetFileName(d.Path)).Append("*");
            return md.ToString();
        }
        #endregion

        #region Completion
        JsonNode? _Completion(JsonObject p)
        {
            var path = _Path(p);
            var doc = _docs.Get(path);
            if (null == doc) return null;
            var pos = (JsonObject)p["position"]!;
            var offset = doc.ToOffset((int)pos["line"]!, (int)pos["character"]!);
            if (doc.Scan.IsOpaque(offset)) return new JsonArray();

            var items = new JsonArray();
            var context = _CompletionContext(doc, offset, out var attribute);
            switch (context)
            {
                case Context.Directive:
                    foreach (var (name, docText) in XbnfKnowledge.Directives)
                        items.Add(_Item(name, 14, docText, insert: name));
                    break;
                case Context.AttributeName:
                    foreach (var (name, docText) in XbnfKnowledge.Attributes)
                        items.Add(_Item(name, 10, docText));
                    break;
                case Context.AttributeValue:
                    if (null != attribute && XbnfKnowledge.AttributeValues.TryGetValue(attribute, out var values))
                        foreach (var v in values) items.Add(_Item(v, 12, null));
                    break;
                case Context.RuleName:
                    var seen = new HashSet<string>();
                    foreach (var d in _docs.WithImports(doc))
                        foreach (var t in d.Scan.Definitions)
                            if (seen.Add(t.Text))
                                items.Add(_Item(t.Text, char.IsUpper(t.Text[0]) ? 7 : 21, null, detail: _FirstLine(d, t)));
                    break;
            }
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }

        enum Context { None, Directive, AttributeName, AttributeValue, RuleName }

        // what's being typed at the offset, from the tokens before it
        static Context _CompletionContext(Document doc, int offset, out string? attribute)
        {
            attribute = null;
            var tokens = doc.Scan.Tokens;
            // the last token that starts before the offset; one that's being typed (the offset is inside or
            // at its end) is replaced by the completion, so the context comes from the one before it
            var k = -1;
            for (var i = 0; i < tokens.Count && tokens[i].Start < offset; ++i) k = i;
            var current = k >= 0 && tokens[k].End >= offset && (tokens[k].Kind == TokenKind.Identifier || tokens[k].Kind == TokenKind.Directive) ? tokens[k] : null;
            if (null != current && current.Kind == TokenKind.Directive) return Context.Directive;
            if (offset > 0 && doc.Text[offset - 1] == '@') return Context.Directive;
            var prev = null != current ? k - 1 : k;

            // inside < >?
            for (var i = prev; i >= 0; --i)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.AttributeClose || (t.Kind == TokenKind.Punctuation && t.Text == ";")) break;
                if (t.Kind == TokenKind.AttributeOpen)
                {
                    var before = tokens[prev];
                    if (before.Kind == TokenKind.Punctuation && before.Text == "=" && prev > 0 && tokens[prev - 1].Kind == TokenKind.Identifier)
                    {
                        attribute = tokens[prev - 1].Text;
                        return Context.AttributeValue;
                    }
                    return before.Kind == TokenKind.AttributeOpen || (before.Kind == TokenKind.Punctuation && before.Text == ",")
                        ? Context.AttributeName : Context.None;
                }
            }

            // after a rule's "=", before its ";": an expression, where rule names go
            for (var i = prev; i >= 0; --i)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.Punctuation && t.Text == ";") break;
                if (t.Kind == TokenKind.Directive) return Context.None;
                if (t.Kind == TokenKind.Action) return Context.None;
                if (t.Kind == TokenKind.Punctuation && t.Text == "=") return Context.RuleName;
            }
            return Context.None;
        }

        static JsonObject _Item(string label, int kind, string? documentation, string? insert = null, string? detail = null)
        {
            var item = new JsonObject { ["label"] = label, ["kind"] = kind };
            if (null != insert) item["insertText"] = insert;
            if (null != detail) item["detail"] = detail;
            if (null != documentation) item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
            return item;
        }

        static string _FirstLine(Document d, Token def)
        {
            var end = def.RuleEnd > def.Start ? def.RuleEnd : def.End;
            var text = d.Text.Substring(def.Start, end - def.Start);
            var nl = text.IndexOf('\n');
            var line = (nl < 0 ? text : text.Substring(0, nl)).Trim();
            return line.Length > 80 ? line.Substring(0, 80) + "…" : line;
        }
        #endregion
    }
}
