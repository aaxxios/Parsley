using System;
using System.Collections.Generic;

namespace Parsley.LanguageServer
{
    enum TokenKind
    {
        Identifier,
        Directive,     // @name
        Literal,       // "text"
        Pattern,       // 'regex'
        Number,
        Action,        // => { C# }, the arrow through the closing brace
        Punctuation,
        AttributeOpen, // <
        AttributeClose // >
    }

    /// <summary>
    /// What an identifier is, from where it appears
    /// </summary>
    enum IdentifierRole
    {
        None,
        Definition,     // the name a rule defines
        Reference,      // a rule used in an expression
        AttributeName,  // <name ...>
        AttributeValue, // <... = name>
    }

    sealed class Token
    {
        public Token(TokenKind kind, int start, int length, string text) { Kind = kind; Start = start; Length = length; Text = text; }
        public TokenKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public string Text { get; }
        public IdentifierRole Role { get; set; }
        /// <summary>
        /// For a definition: the rule's extent, from its name through its ";"
        /// </summary>
        public int RuleEnd { get; set; } = -1;
        /// <summary>
        /// For a definition: where the rule's "=" is, or -1
        /// </summary>
        public int EqualsAt { get; set; } = -1;
        public override string ToString() => $"{Kind} {Role} \"{Text}\" @{Start}";
    }

    /// <summary>
    /// A tolerant XBNF tokenizer for editor features. Unlike XbnfReader it never stops at an error, so
    /// navigation and completion keep working while a rule is half written. It knows which identifiers
    /// define rules, which reference them and which are attributes, and where @import paths are.
    /// </summary>
    sealed class XbnfScan
    {
        public List<Token> Tokens { get; } = new List<Token>();
        /// <summary>
        /// @import paths as written, with the literal token they're in
        /// </summary>
        public List<(string Path, Token At)> Imports { get; } = new List<(string, Token)>();
        /// <summary>
        /// Spans of comments, literals, patterns and actions, where completion shouldn't offer anything
        /// </summary>
        public List<(int Start, int End)> Opaque { get; } = new List<(int, int)>();

        public IEnumerable<Token> Definitions
        {
            get { foreach (var t in Tokens) if (t.Role == IdentifierRole.Definition) yield return t; }
        }

        public static XbnfScan Scan(string text)
        {
            var scan = new XbnfScan();
            scan._Scan(text);
            scan._Classify();
            return scan;
        }

        static bool _IdStart(char c) => char.IsLetter(c) || c == '_';
        static bool _IdPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        void _Scan(string s)
        {
            var i = 0;
            var n = s.Length;
            var inAttributes = false;
            while (i < n)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { ++i; continue; }
                var start = i;
                if (c == '/' && i + 1 < n && s[i + 1] == '/')
                {
                    while (i < n && s[i] != '\n') ++i;
                    Opaque.Add((start, i));
                    continue;
                }
                if (c == '/' && i + 1 < n && s[i + 1] == '*')
                {
                    var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? n : close + 2;
                    Opaque.Add((start, i));
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    i = _SkipQuoted(s, i, c);
                    Tokens.Add(new Token(c == '"' ? TokenKind.Literal : TokenKind.Pattern, start, i - start, s.Substring(start, i - start)));
                    // the quotes themselves are where you'd type a new one, so only the inside is opaque
                    Opaque.Add((start + 1, Math.Max(start + 1, i - (i > start + 1 && s[i - 1] == c ? 1 : 0))));
                    continue;
                }
                if (c == '=' && i + 1 < n && s[i + 1] == '>')
                {
                    // a semantic action: the arrow and the C# block up to its matching brace
                    i += 2;
                    while (i < n && char.IsWhiteSpace(s[i])) ++i;
                    if (i < n && s[i] == '{') i = _SkipCSharpBlock(s, i);
                    Tokens.Add(new Token(TokenKind.Action, start, i - start, s.Substring(start, i - start)));
                    Opaque.Add((start + 2, i));
                    continue;
                }
                if (c == '@' && i + 1 < n && _IdStart(s[i + 1]))
                {
                    ++i;
                    while (i < n && _IdPart(s[i])) ++i;
                    Tokens.Add(new Token(TokenKind.Directive, start, i - start, s.Substring(start, i - start)));
                    continue;
                }
                if (_IdStart(c))
                {
                    while (i < n && _IdPart(s[i])) ++i;
                    var t = new Token(TokenKind.Identifier, start, i - start, s.Substring(start, i - start));
                    Tokens.Add(t);
                    continue;
                }
                if (char.IsDigit(c) || (c == '-' && inAttributes && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    ++i;
                    while (i < n && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) ++i;
                    Tokens.Add(new Token(TokenKind.Number, start, i - start, s.Substring(start, i - start)));
                    continue;
                }
                if (c == '<') { inAttributes = true; Tokens.Add(new Token(TokenKind.AttributeOpen, i, 1, "<")); ++i; continue; }
                if (c == '>') { inAttributes = false; Tokens.Add(new Token(TokenKind.AttributeClose, i, 1, ">")); ++i; continue; }
                if (c == ';') inAttributes = false; // a missing > shouldn't swallow the rest of the file
                Tokens.Add(new Token(TokenKind.Punctuation, i, 1, c.ToString()));
                ++i;
            }
        }

        static int _SkipQuoted(string s, int i, char quote)
        {
            ++i;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\') { i += 2; continue; }
                if (c == quote) return i + 1;
                if (c == '\n') return i; // unterminated: stop at the line's end
                ++i;
            }
            return s.Length;
        }

        // a C# block, by brace depth, skipping C# strings, chars and comments
        static int _SkipCSharpBlock(string s, int i)
        {
            var depth = 0;
            var n = s.Length;
            while (i < n)
            {
                var c = s[i];
                if (c == '/' && i + 1 < n && s[i + 1] == '/') { while (i < n && s[i] != '\n') ++i; continue; }
                if (c == '/' && i + 1 < n && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? n : e + 2; continue; }
                if (c == '@' && i + 1 < n && s[i + 1] == '"')
                {
                    // verbatim string: "" is a quote
                    i += 2;
                    while (i < n) { if (s[i] == '"') { if (i + 1 < n && s[i + 1] == '"') { i += 2; continue; } ++i; break; } ++i; }
                    continue;
                }
                if (c == '"' || c == '\'') { i = _SkipQuoted(s, i, c); continue; }
                if (c == '{') ++depth;
                else if (c == '}' && --depth == 0) return i + 1;
                ++i;
            }
            return n;
        }

        // walks the tokens as statements: "@directive ...;" or "Name<attrs>= expression [=> action];"
        void _Classify()
        {
            var atStatementStart = true;
            Token? currentRule = null;
            var inAttributes = false;
            var expectAttributeName = false;
            Token? directive = null;
            for (var k = 0; k < Tokens.Count; ++k)
            {
                var t = Tokens[k];
                switch (t.Kind)
                {
                    case TokenKind.Directive:
                        directive = t;
                        atStatementStart = false;
                        continue;
                    case TokenKind.AttributeOpen:
                        inAttributes = true;
                        expectAttributeName = true;
                        continue;
                    case TokenKind.AttributeClose:
                        inAttributes = false;
                        continue;
                    case TokenKind.Literal:
                        if (null != directive && directive.Text == "@import" && t.Length >= 2)
                            Imports.Add((t.Text.Substring(1, t.Text.Length - (t.Text.EndsWith("\"") && t.Length > 1 ? 2 : 1)), t));
                        continue;
                    case TokenKind.Punctuation:
                        if (inAttributes)
                        {
                            if (t.Text == ",") expectAttributeName = true;
                            else if (t.Text == "=") expectAttributeName = false;
                            continue;
                        }
                        if (t.Text == ";")
                        {
                            if (null != currentRule) currentRule.RuleEnd = t.End;
                            currentRule = null;
                            directive = null;
                            atStatementStart = true;
                        }
                        else if (t.Text == "=" && null != currentRule && currentRule.EqualsAt < 0)
                            currentRule.EqualsAt = t.Start;
                        continue;
                    case TokenKind.Identifier:
                        if (inAttributes)
                        {
                            t.Role = expectAttributeName ? IdentifierRole.AttributeName : IdentifierRole.AttributeValue;
                            expectAttributeName = false;
                            continue;
                        }
                        if (null != directive) continue;
                        if (atStatementStart)
                        {
                            // a name at the start of a statement defines a rule
                            t.Role = IdentifierRole.Definition;
                            currentRule = t;
                            atStatementStart = false;
                            continue;
                        }
                        if (null != currentRule && currentRule.EqualsAt >= 0)
                        {
                            t.Role = IdentifierRole.Reference;
                            continue;
                        }
                        // a name where a definition's "=" was expected: likely a missing ";" above, so
                        // treat it as the start of a new rule rather than lose everything after it
                        if (null != currentRule && currentRule.EqualsAt < 0 && _NextIs(k, "=", "<"))
                        {
                            currentRule.RuleEnd = t.Start;
                            t.Role = IdentifierRole.Definition;
                            currentRule = t;
                        }
                        continue;
                    default:
                        continue;
                }
            }
            if (null != currentRule && currentRule.RuleEnd < 0) currentRule.RuleEnd = Tokens.Count > 0 ? Tokens[Tokens.Count - 1].End : currentRule.End;
        }

        bool _NextIs(int k, params string[] texts)
        {
            if (k + 1 >= Tokens.Count) return false;
            var next = Tokens[k + 1].Text;
            foreach (var t in texts) if (next == t) return true;
            return false;
        }

        /// <summary>
        /// The token whose span holds the offset, or ends at it
        /// </summary>
        public Token? TokenAt(int offset)
        {
            foreach (var t in Tokens)
            {
                if (t.Start <= offset && offset <= t.End) return t;
                if (t.Start > offset) break;
            }
            return null;
        }

        public bool IsOpaque(int offset)
        {
            foreach (var (s, e) in Opaque)
                if (s <= offset && offset < e) return true;
            return false;
        }
    }
}
