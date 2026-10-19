#nullable enable
using System;
using System.Collections.Generic;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// A tolerant scan of the C# in a semantic action, for navigation: the names it uses, told apart as locals the
    /// action declares, members after a dot, EvaluateXxx calls and other names, which may be the grammar's symbol
    /// constants. It isn't a C# parser. It finds declarations from the shapes they have (a type, then a name, then
    /// =, ;, ',', ), in or a parameter list; lambda parameters; foreach, from and pattern variables) and gives each
    /// the scope of the braces it's in, which is enough for code the size of an action.
    /// </summary>
    static class CSharpScan
    {
        enum Kind { Identifier, Keyword, Punctuation, Other }

        sealed class Lex
        {
            public Lex(Kind kind, int start, int end, string text, bool verbatim = false)
            {
                Kind = kind; Start = start; End = end; Text = text; Verbatim = verbatim;
            }
            public Kind Kind { get; }
            public int Start { get; }
            public int End { get; }
            // an identifier's name, without the @ of a verbatim identifier
            public string Text { get; }
            public bool Verbatim { get; }
            // the braces around it: where the innermost { is and where its } ends
            public int ScopeStart { get; set; }
            public int ScopeEnd { get; set; }
            // a { that opens a switch expression's arms
            public bool OpensSwitchArms { get; set; }
            // inside a switch expression's arms, outside any parentheses or brackets of its own
            public bool InSwitchArms { get; set; }
            public bool IsDeclaration { get; set; }
            public bool IsMember { get; set; }
            public Token? Token { get; set; }
            public override string ToString() => $"{Kind} {Text} @{Start}";
        }

        static readonly HashSet<string> s_keywords = new HashSet<string>(
            ("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double " +
            "else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is " +
            "lock long namespace new null object operator out override params private protected public readonly ref return sbyte " +
            "sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe " +
            "ushort using virtual void volatile while").Split(' '));
        // keywords that name types, and contextual ones that stand for a type where a declaration starts
        static readonly HashSet<string> s_typeKeywords = new HashSet<string>(
            "bool byte char decimal double float int long object sbyte short string uint ulong ushort var dynamic nint nuint".Split(' '));
        // contextual keywords that start or continue an expression, so a name after one isn't being declared
        static readonly HashSet<string> s_expressionWords = new HashSet<string>(
            "await yield nameof when and or not select where orderby group by on equals ascending descending with".Split(' '));
        // the two-character operators worth keeping whole; < and > stay single, for generic type arguments
        static readonly HashSet<string> s_pairs = new HashSet<string> { "=>", "?.", "::", "->", "??", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=" };

        static bool _IdStart(char c) => char.IsLetter(c) || c == '_';
        static bool _IdPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// The names in the C# between from and to, as tokens in the document's offsets, each with its role:
        /// <see cref="IdentifierRole.CodeLocal"/> (with its declaration), <see cref="IdentifierRole.CodeMember"/>,
        /// <see cref="IdentifierRole.CodeEvaluate"/> or <see cref="IdentifierRole.CodeName"/>
        /// </summary>
        public static List<Token> Scan(string s, int from, int to, Token container)
        {
            var lexes = new List<Lex>();
            var lexer = new _Lexer(s, to, lexes);
            lexer.Run(from);
            _Scopes(lexes, from, to);
            _FindDeclarations(lexes);
            return _Resolve(lexes, s, container);
        }

        #region Lexing
        sealed class _Lexer
        {
            readonly string _s;
            readonly int _end;
            readonly List<Lex> _out;
            public _Lexer(string s, int end, List<Lex> output) { _s = s; _end = end; _out = output; }

            public void Run(int i) => _Code(i, inHole: false);

            // code up to the end, or in an interpolation hole, up to its } (returned past it)
            int _Code(int i, bool inHole)
            {
                var s = _s;
                var depth = 0; // ( [ { inside a hole
                while (i < _end)
                {
                    var c = s[i];
                    if (char.IsWhiteSpace(c)) { ++i; continue; }
                    if (c == '/' && i + 1 < _end && s[i + 1] == '/') { while (i < _end && s[i] != '\n') ++i; continue; }
                    if (c == '/' && i + 1 < _end && s[i + 1] == '*')
                    {
                        var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = close < 0 || close + 2 > _end ? _end : close + 2;
                        continue;
                    }
                    if (inHole)
                    {
                        if (c == '(' || c == '[' || c == '{') ++depth;
                        else if (c == ')' || c == ']') { if (depth > 0) --depth; }
                        else if (c == '}')
                        {
                            if (0 == depth) return i + 1;
                            --depth;
                        }
                        else if (c == ':' && 0 == depth && !(i + 1 < _end && s[i + 1] == ':'))
                        {
                            // a format: {x:N2}, up to the hole's end
                            while (i < _end && s[i] != '}') ++i;
                            return Math.Min(_end, i + 1);
                        }
                    }
                    // strings, with any $ and @ before them
                    var q = i;
                    var dollars = 0;
                    var verbatim = false;
                    while (q < _end && (s[q] == '$' || s[q] == '@'))
                    {
                        if (s[q] == '$') ++dollars; else verbatim = true;
                        ++q;
                    }
                    if (q < _end && s[q] == '"')
                    {
                        i = _String(q, dollars, verbatim);
                        continue;
                    }
                    if (c == '\'') { i = _Char(i); continue; }
                    if (c == '@' && i + 1 < _end && _IdStart(s[i + 1]))
                    {
                        var st = i++;
                        while (i < _end && _IdPart(s[i])) ++i;
                        _out.Add(new Lex(Kind.Identifier, st, i, s.Substring(st + 1, i - st - 1), verbatim: true));
                        continue;
                    }
                    if (_IdStart(c))
                    {
                        var st = i;
                        while (i < _end && _IdPart(s[i])) ++i;
                        var text = s.Substring(st, i - st);
                        _out.Add(new Lex(s_keywords.Contains(text) ? Kind.Keyword : Kind.Identifier, st, i, text));
                        continue;
                    }
                    if (char.IsDigit(c))
                    {
                        var st = i;
                        while (i < _end && (_IdPart(s[i]) || (s[i] == '.' && i + 1 < _end && char.IsDigit(s[i + 1])))) ++i;
                        _out.Add(new Lex(Kind.Other, st, i, s.Substring(st, i - st)));
                        continue;
                    }
                    if (i + 1 < _end && s_pairs.Contains(s.Substring(i, 2)))
                    {
                        _out.Add(new Lex(Kind.Punctuation, i, i + 2, s.Substring(i, 2)));
                        i += 2;
                        continue;
                    }
                    _out.Add(new Lex(Kind.Punctuation, i, i + 1, c.ToString()));
                    ++i;
                }
                return i;
            }

            // a string starting at its first quote: regular, verbatim, interpolated, or raw
            int _String(int i, int dollars, bool verbatim)
            {
                var s = _s;
                var quotes = 0;
                while (i + quotes < _end && s[i + quotes] == '"') ++quotes;
                if (quotes >= 3 && !verbatim)
                {
                    // a raw string: up to the same run of quotes. Its holes, if it has any, aren't looked into.
                    var close = s.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                    return close < 0 || close + quotes > _end ? _end : close + quotes;
                }
                if (quotes == 2 && !verbatim) return i + 2; // ""
                ++i;
                while (i < _end)
                {
                    var c = s[i];
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < _end && s[i + 1] == '"') { i += 2; continue; }
                            return i + 1;
                        }
                    }
                    else
                    {
                        if (c == '\\') { i += 2; continue; }
                        if (c == '"') return i + 1;
                        if (c == '\n') return i;
                    }
                    if (dollars > 0 && c == '{')
                    {
                        if (i + 1 < _end && s[i + 1] == '{') { i += 2; continue; }
                        i = _Code(i + 1, inHole: true);
                        continue;
                    }
                    if (dollars > 0 && c == '}' && i + 1 < _end && s[i + 1] == '}') { i += 2; continue; }
                    ++i;
                }
                return _end;
            }

            int _Char(int i)
            {
                ++i;
                while (i < _end)
                {
                    var c = _s[i];
                    if (c == '\\') { i += 2; continue; }
                    if (c == '\'') return i + 1;
                    if (c == '\n') return i;
                    ++i;
                }
                return _end;
            }
        }
        #endregion

        #region Declarations
        static bool _Is(Lex? l, string text) => null != l && l.Kind != Kind.Identifier && l.Text == text;
        static Lex? _At(List<Lex> lexes, int k) => k >= 0 && k < lexes.Count ? lexes[k] : null;

        // gives each lex the span of the braces it's in, and marks the braces that hold a switch expression's arms
        static void _Scopes(List<Lex> lexes, int from, int to)
        {
            var stack = new Stack<(int Start, int Index, bool Arms, int Parens)>();
            stack.Push((from, -1, false, 0));
            var ends = new Dictionary<int, int>(); // a { lex's index: where its } ends
            var parens = 0;
            for (var k = 0; k < lexes.Count; ++k)
            {
                var l = lexes[k];
                if (_Is(l, "{"))
                {
                    var arms = _Is(_At(lexes, k - 1), "switch");
                    l.OpensSwitchArms = arms;
                    stack.Push((l.Start, k, arms, parens));
                    parens = 0;
                }
                else if (_Is(l, "}") && stack.Count > 1)
                {
                    var top = stack.Pop();
                    ends[top.Index] = l.End;
                    parens = top.Parens;
                }
                else if (_Is(l, "(") || _Is(l, "[")) ++parens;
                else if ((_Is(l, ")") || _Is(l, "]")) && parens > 0) --parens;
                var inner = stack.Peek();
                l.ScopeStart = inner.Start;
                l.InSwitchArms = inner.Arms && 0 == parens;
            }
            // each lex's scope ends where its braces close, or at the end of the code if they never do
            var openAt = new Dictionary<int, int>();
            for (var k = 0; k < lexes.Count; ++k)
                if (_Is(lexes[k], "{")) openAt[lexes[k].Start] = k;
            foreach (var l in lexes)
                l.ScopeEnd = openAt.TryGetValue(l.ScopeStart, out var k) && ends.TryGetValue(k, out var e) ? e : to;
        }

        // whether the lex at k ends a type: a name or a type keyword, a generic's closing >, a [], or a ? after a type
        static bool _EndsType(List<Lex> lexes, int k)
        {
            var l = _At(lexes, k);
            if (null == l) return false;
            if (l.Kind == Kind.Identifier) return l.Verbatim || !s_expressionWords.Contains(l.Text);
            if (l.Kind == Kind.Keyword) return s_typeKeywords.Contains(l.Text);
            if (_Is(l, "]")) return _Is(_At(lexes, k - 1), "[") && _EndsType(lexes, k - 2);
            if (_Is(l, "?")) return _EndsType(lexes, k - 1) && !_Is(_At(lexes, k - 1), "?");
            if (_Is(l, ">"))
            {
                // back to the matching <, over nothing but what type arguments are made of
                var depth = 0;
                for (var j = k; j >= 0; --j)
                {
                    var t = lexes[j];
                    if (_Is(t, ">")) ++depth;
                    else if (_Is(t, "<"))
                    {
                        if (0 == --depth) return null != _At(lexes, j - 1) && lexes[j - 1].Kind == Kind.Identifier;
                    }
                    else if (!(t.Kind == Kind.Identifier || (t.Kind == Kind.Keyword && s_typeKeywords.Contains(t.Text)) ||
                        _Is(t, ",") || _Is(t, ".") || _Is(t, "?") || _Is(t, "[") || _Is(t, "]") || _Is(t, "::")))
                        return false;
                }
            }
            return false;
        }

        static void _FindDeclarations(List<Lex> lexes)
        {
            for (var k = 0; k < lexes.Count; ++k)
            {
                var l = lexes[k];
                if (l.Kind != Kind.Identifier) continue;
                var prev = _At(lexes, k - 1);
                var next = _At(lexes, k + 1);
                if (_Is(prev, ".") || _Is(prev, "?.") || _Is(prev, "->") || _Is(prev, "::"))
                {
                    l.IsMember = true;
                    continue;
                }
                if (!l.Verbatim && s_expressionWords.Contains(l.Text)) continue;
                var before = _At(lexes, k - 2);
                // in a switch expression's arms, name => is a constant pattern, not a lambda
                if (_Is(next, "=>"))
                {
                    if (l.InSwitchArms) { l.IsDeclaration = _EndsType(lexes, k - 1) && !_Is(prev, ","); continue; }
                    l.IsDeclaration = true;
                    continue;
                }
                // from x in xs, let x =, join x in, into x
                if (null != prev && prev.Kind == Kind.Identifier && !prev.Verbatim && (prev.Text == "from" || prev.Text == "let" || prev.Text == "join" || prev.Text == "into"))
                {
                    l.IsDeclaration = true;
                    continue;
                }
                if (!_EndsType(lexes, k - 1)) continue;
                // is Type x and case Type x declare whatever follows
                if (null != before && (_Is(before, "is") || _Is(before, "case") || (before.Kind == Kind.Identifier && before.Text == "not")))
                {
                    l.IsDeclaration = true;
                    continue;
                }
                if (null == next) continue;
                var n = next.Text;
                if (next.Kind == Kind.Identifier) continue;
                if (n == "=" || n == ";" || n == "," || n == ")" || n == "in" || (n == "(" && !_Is(prev, "?")))
                {
                    // a name after a name is a declaration unless the first is a statement's keyword, as in return x;
                    // which _EndsType already ruled out, or the second is called as a generic method's argument list
                    l.IsDeclaration = true;
                }
            }
            // (a, b) => and (int a, int b) =>: the names before each , and the )
            for (var k = 0; k < lexes.Count; ++k)
            {
                if (!_Is(lexes[k], "=>") || !_Is(_At(lexes, k - 1), ")") || lexes[k].InSwitchArms) continue;
                var depth = 0;
                var open = -1;
                for (var j = k - 1; j >= 0; --j)
                {
                    if (_Is(lexes[j], ")")) ++depth;
                    else if (_Is(lexes[j], "(") && 0 == --depth) { open = j; break; }
                }
                if (open < 0) continue;
                var simple = true;
                for (var j = open + 1; j < k - 1 && simple; ++j)
                {
                    var t = lexes[j];
                    simple = t.Kind == Kind.Identifier || t.Kind == Kind.Keyword && (s_typeKeywords.Contains(t.Text) || t.Text == "ref" || t.Text == "out" || t.Text == "in")
                        || _Is(t, ",") || _Is(t, ".") || _Is(t, "<") || _Is(t, ">") || _Is(t, "[") || _Is(t, "]") || _Is(t, "?");
                }
                if (!simple) continue;
                for (var j = open + 1; j < k - 1; ++j)
                    if (lexes[j].Kind == Kind.Identifier && (_Is(lexes[j + 1], ",") || _Is(lexes[j + 1], ")")))
                        lexes[j].IsDeclaration = true;
            }
        }
        #endregion

        static List<Token> _Resolve(List<Lex> lexes, string s, Token container)
        {
            var result = new List<Token>();
            var declarations = new List<Lex>();
            foreach (var l in lexes)
            {
                if (l.Kind != Kind.Identifier) continue;
                var t = new Token(TokenKind.Identifier, l.Start, l.End - l.Start, s.Substring(l.Start, l.End - l.Start))
                {
                    Name = l.Text,
                    NameStart = l.Start + (l.Verbatim ? 1 : 0),
                    Container = container,
                };
                l.Token = t;
                if (l.IsMember) t.Role = IdentifierRole.CodeMember;
                else if (l.IsDeclaration)
                {
                    t.Role = IdentifierRole.CodeLocal;
                    t.Declaration = t;
                    declarations.Add(l);
                }
                result.Add(t);
            }
            foreach (var l in lexes)
            {
                var t = l.Token;
                if (null == t || t.Role != IdentifierRole.None) continue;
                // the innermost declaration of the name whose braces hold this use
                Lex? found = null;
                foreach (var d in declarations)
                    if (d.Text == l.Text && d.ScopeStart <= l.Start && l.Start < d.ScopeEnd && (null == found || d.ScopeStart >= found.ScopeStart))
                        found = d;
                if (null != found)
                {
                    t.Role = IdentifierRole.CodeLocal;
                    t.Declaration = found.Token;
                }
                else if (l.Text.Length > "Evaluate".Length && l.Text.StartsWith("Evaluate", StringComparison.Ordinal))
                {
                    t.Role = IdentifierRole.CodeEvaluate;
                    t.Name = l.Text.Substring("Evaluate".Length);
                    t.NameStart += "Evaluate".Length;
                }
                else t.Role = IdentifierRole.CodeName;
            }
            return result;
        }

        /// <summary>
        /// True if the text is a C# keyword, so a name with it has to be written with an @
        /// </summary>
        public static bool IsKeyword(string text) => s_keywords.Contains(text);
    }
}
