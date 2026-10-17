// Runtime support shared by every parser Parsley synthesizes, whatever its algorithm. Include this
// file alongside the synthesized parser and its algorithm's base class (LL1ParserBase.cs, LLkParserBase.cs
// or AllStarParserBase.cs); it has no other dependencies. Requires C# 12.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Parsley.Runtime
{
    /// <summary>
    /// A token from whatever lexer feeds the parser. Lexers drop hidden terminals (whitespace, comments)
    /// before the parser sees them.
    /// </summary>
    /// <remarks>
    /// Coming from the lexer, SymbolId counts terminals from 0 in the grammar's terminal order, hidden and
    /// implicit ones included; a negative id means the lexer couldn't match the input. The parser adds
    /// <see cref="ParserTables.TerminalStart"/> as each token arrives, so inside the parser (Current, Peek,
    /// ParseNode.SymbolId) ids match the synthesized parser's constants.
    /// </remarks>
    public readonly record struct Token(int SymbolId, string Value, int Line, int Column, long Position)
    {
        /// <summary>
        /// Whatever the token's producer attaches to it, or null. A lexer can put its state after this token here,
        /// for a <see cref="LexerController"/> to hand back when it restarts the lexer after the token (see
        /// <see cref="LexerMode.Resume"/>). An island's token carries its <see cref="IslandParse"/>.
        /// </summary>
        public object? Tag { get; init; }
    }

    /// <summary>
    /// What an island's token carries: the tree its external grammar parsed, and the problems found
    /// </summary>
    public sealed class IslandParse
    {
        public IslandParse(ParseNode node, IReadOnlyList<ParseError> errors)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
            Errors = errors ?? throw new ArgumentNullException(nameof(errors));
        }
        /// <summary>
        /// The root of the island's tree, a node of its external grammar's shared production. Its symbol ids
        /// are the external grammar's.
        /// </summary>
        public ParseNode Node { get; }
        public IReadOnlyList<ParseError> Errors { get; }
        // the parser that consumes the token reports the errors once, however often it looks at it
        internal bool Reported;
    }

    /// <summary>
    /// Wraps an enumerator so code can look ahead of the current item without advancing.
    /// </summary>
    public sealed class LookAheadEnumerator<T> : IEnumerator<T>
    {
        readonly IEnumerator<T> _inner;
        readonly List<T> _buffer = new();
        int _head; // index of the current item in _buffer
        bool _started;
        bool _innerDone;

        public LookAheadEnumerator(IEnumerable<T> items)
        {
            _inner = (items ?? throw new ArgumentNullException(nameof(items))).GetEnumerator();
        }
        public T Current
        {
            get
            {
                if (!_started || _head >= _buffer.Count)
                    throw new InvalidOperationException("The enumerator is not positioned on an item");
                return _buffer[_head];
            }
        }
        object? IEnumerator.Current => Current;
        bool _Fill(int count)
        {
            // make sure there are count items from _head on
            while (_buffer.Count - _head < count)
            {
                if (_innerDone || !_inner.MoveNext())
                {
                    _innerDone = true;
                    return false;
                }
                _buffer.Add(_inner.Current);
            }
            return true;
        }
        public bool MoveNext()
        {
            if (_started)
                ++_head;
            _started = true;
            // drop consumed items now and then so the buffer doesn't grow with the input
            if (_head > 64 && _head * 2 > _buffer.Count)
            {
                _buffer.RemoveRange(0, _head);
                _head = 0;
            }
            return _Fill(1);
        }
        /// <summary>
        /// Looks n items past the current one (n = 1 is the next item) without advancing
        /// </summary>
        public bool TryPeek(int n, out T item)
        {
            if (0 > n) throw new ArgumentOutOfRangeException(nameof(n));
            if (!_started) throw new InvalidOperationException("Call MoveNext first");
            if (_Fill(n + 1))
            {
                item = _buffer[_head + n];
                return true;
            }
            item = default!;
            return false;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// A node in the parse tree: a terminal (with its text), a non-terminal, or an error node holding skipped tokens
    /// </summary>
    public sealed class ParseNode
    {
        public ParseNode(int symbolId, string symbol, string? value, bool isTerminal, int line, int column, long position)
        {
            SymbolId = symbolId;
            Symbol = symbol;
            Value = value;
            IsTerminal = isTerminal;
            Line = line;
            Column = column;
            Position = position;
        }
        public int SymbolId { get; }
        public string Symbol { get; }
        /// <summary>
        /// The matched text, for terminals
        /// </summary>
        public string? Value { get; }
        public bool IsTerminal { get; }
        /// <summary>
        /// True for a node holding tokens skipped during error recovery
        /// </summary>
        public bool IsError { get; init; }
        public int Line { get; }
        public int Column { get; }
        public long Position { get; }
        public List<ParseNode> Children { get; } = new();
        public void Add(ParseNode child) => Children.Add(child);
        /// <summary>
        /// The terminal text under this node, joined with spaces
        /// </summary>
        public string Text
        {
            get
            {
                if (IsTerminal) return Value ?? "";
                var sb = new StringBuilder();
                foreach (var c in Children)
                {
                    var t = c.Text;
                    if (0 == t.Length) continue;
                    if (0 < sb.Length) sb.Append(' ');
                    sb.Append(t);
                }
                return sb.ToString();
            }
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            _Write(sb, 0);
            return sb.ToString();
        }
        void _Write(StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2);
            sb.Append(Symbol);
            if (IsTerminal)
            {
                sb.Append(' ');
                sb.Append('"');
                sb.Append(Value);
                sb.Append('"');
            }
            sb.AppendLine();
            foreach (var c in Children)
                c._Write(sb, depth + 1);
        }
    }
    public sealed class ParseException : Exception
    {
        public IReadOnlyCollection<ParseError> Errors { get; }
        public ParseException(IReadOnlyCollection<ParseError> errors) : base("One or more parse errors occured.") { Errors = errors; }

    }
    public sealed record ParseError(string Message, int Line, int Column, long Position)
    {
        public override string ToString() => $"{Message} at line {Line}, column {Column}";
    }

    /// <summary>
    /// What a synthesized parser tells the runtime about its symbols
    /// </summary>
    public sealed class ParserTables
    {
        public ParserTables(int terminalStart, string[] symbolNames, string[] displayNames, int[] collapsedTerminals, int[][] recoverySets, int[] syncTerminals, int endOfInput, int error, int[]? contextualTerminals = null)
        {
            // contextual terminals come after every terminal the lexer produces
            LexedEnd = endOfInput;
            HasContextual = null != contextualTerminals && 0 < contextualTerminals.Length;
            if (HasContextual)
                foreach (var c in contextualTerminals!)
                    LexedEnd = Math.Min(LexedEnd, c);
            TerminalStart = terminalStart;
            SymbolNames = symbolNames;
            DisplayNames = displayNames;
            RecoverySets = recoverySets;
            EndOfInput = endOfInput;
            Error = error;
            Collapsed = Set(symbolNames.Length, collapsedTerminals);
            Sync = Set(symbolNames.Length, syncTerminals);
            Recovery = new bool[recoverySets.Length][];
            for (var i = 0; i < recoverySets.Length; ++i)
                Recovery[i] = Set(symbolNames.Length, recoverySets[i]);
        }
        public string[] SymbolNames { get; }
        /// <summary>
        /// How each symbol reads in an error message
        /// </summary>
        public string[] DisplayNames { get; }
        /// <summary>
        /// For each non-terminal, the terminals where error recovery may stop while it is being parsed
        /// </summary>
        public int[][] RecoverySets { get; }
        public int EndOfInput { get; }
        public int Error { get; }
        /// <summary>
        /// One past the last terminal a lexer can produce; contextual terminals, which only the parser assigns, follow it
        /// </summary>
        public int LexedEnd { get; }
        /// <summary>
        /// The grammar has contextual terminals, so tokens are classified as the parser looks at them
        /// </summary>
        public bool HasContextual { get; }
        internal bool[] Collapsed { get; }
        internal bool[] Sync { get; }
        internal bool[][] Recovery { get; }
        /// <summary>
        /// The parser id of the first terminal (the number of non-terminals). Lexer ids are offset by this.
        /// </summary>
        public int TerminalStart { get; }

        /// <summary>
        /// A lookup table with true at each of the given ids
        /// </summary>
        public static bool[] Set(int count, params int[] ids)
        {
            var result = new bool[count];
            foreach (var id in ids)
                result[id] = true;
            return result;
        }
    }


    /// <summary>
    /// What every synthesized parser shares, whatever its algorithm: token access, tree building and
    /// error recovery. Errors are recorded in <see cref="Errors"/> and parsing continues.
    /// </summary>
    /// <remarks>
    /// A parser derives from one of the algorithm-specific bases (LL1ParserBase, LLkParserBase or
    /// AllStarParserBase), which supply the tokens and whatever lookahead the algorithm needs.
    /// </remarks>
    public abstract class ParserBase
    {
        readonly ParserTables _tables;
        readonly List<int> _active = new();
        readonly List<ParseError> _errors = new();
        Token _current;
        Token _previous = new Token(-1, "", 0, 0, -1);
        long _index;
        long _lastErrorIndex = -1;
        int _errorsAtIndex;
        bool _quiet;
        ParseNode? _root;

        protected ParserBase(ParserTables tables)
        {
            _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        }

        /// <summary>
        /// Problems found while parsing, in order
        /// </summary>
        public IReadOnlyList<ParseError> Errors => _errors;
        protected ParserTables Tables => _tables;

        /// <summary>
        /// Reads the next token from the source the derived class set up with <see cref="FromLexer"/>.
        /// Returns false at the end of the input.
        /// </summary>
        protected abstract bool ReadToken(out Token token);

        /// <summary>
        /// Loads the first token. A derived constructor calls this once its token source is ready.
        /// </summary>
        protected void Start() => _current = ReadToken(out var t) ? t : EndToken(default);

        /// <summary>
        /// Converts lexer ids (terminals counted from 0) to parser ids as tokens arrive. Anything the
        /// lexer couldn't match, or an id past the last terminal, becomes #ERROR so the parser reports it.
        /// </summary>
        protected IEnumerable<Token> FromLexer(IEnumerable<Token> tokens) => _FromLexer(tokens ?? throw new ArgumentNullException(nameof(tokens)));
        IEnumerable<Token> _FromLexer(IEnumerable<Token> tokens)
        {
            foreach (var t in tokens)
            {
                var id = t.SymbolId + _tables.TerminalStart;
                if (0 > t.SymbolId || id >= _tables.LexedEnd)
                    id = _tables.Error;
                yield return t with { SymbolId = id };
            }
        }

        /// <summary>
        /// The end of input token, placed just after the given last token
        /// </summary>
        protected Token EndToken(Token last) => new Token(_tables.EndOfInput, "", last.Line, last.Column + (last.Value?.Length ?? 0), last.Position + (last.Value?.Length ?? 0));

        /// <summary>
        /// The token being looked at, classified (see <see cref="Classify"/>)
        /// </summary>
        protected Token Current => Classified(_current, _previous);
        /// <summary>
        /// The token being looked at, as the lexer produced it
        /// </summary>
        protected Token CurrentUnclassified => _current;
        /// <summary>
        /// The last token matched, as the lexer produced it. Its SymbolId is -1 before the first.
        /// </summary>
        protected Token Previous => _previous;
        /// <summary>
        /// For a grammar with contextual terminals: the symbol a token is, given the token before it. The
        /// parser asks each time it looks at a token, so the answer can depend on what has been parsed so far.
        /// </summary>
        protected virtual int Classify(Token token, Token previous) => token.SymbolId;
        /// <summary>
        /// The token with the symbol <see cref="Classify"/> gives it
        /// </summary>
        protected Token Classified(Token token, Token previous)
        {
            if (!_tables.HasContextual || token.SymbolId == _tables.EndOfInput || token.SymbolId == _tables.Error) return token;
            var id = Classify(token, previous);
            return id == token.SymbolId ? token : token with { SymbolId = id };
        }
        /// <summary>
        /// Called with each token the parser matches, after it is added to the tree
        /// </summary>
        protected virtual void OnMatched(Token token) { }
        protected void Advance()
        {
            if (_current.SymbolId == _tables.EndOfInput) return;
            var last = _current;
            _previous = last;
            _current = ReadToken(out var t) ? t : EndToken(last);
            ++_index;
        }
        ParseNode _Leaf(Token t)
        {
            // an island's token becomes a node holding the island's tree, and the island's problems become the parser's
            if (t.Tag is IslandParse island)
            {
                var node = new ParseNode(t.SymbolId, _tables.SymbolNames[t.SymbolId], null, false, t.Line, t.Column, t.Position);
                node.Add(island.Node);
                if (!island.Reported)
                {
                    island.Reported = true;
                    _errors.AddRange(island.Errors);
                }
                return node;
            }
            return new ParseNode(t.SymbolId, _tables.SymbolNames[t.SymbolId], t.Value, true, t.Line, t.Column, t.Position);
        }
        /// <summary>
        /// Adds Current to the tree (unless its terminal is collapsed) and moves past it
        /// </summary>
        protected void Consume(ParseNode parent)
        {
            var token = Current;
            if (!_tables.Collapsed[token.SymbolId])
                parent.Add(_Leaf(token));
            Advance();
            _quiet = false; // a matched token ends the quiet period after an error
            OnMatched(token);
        }
        /// <summary>
        /// Consumes the given terminal, or reports it missing and recovers
        /// </summary>
        protected void Expect(int symbolId, ParseNode parent)
        {
            if (Current.SymbolId == symbolId)
            {
                Consume(parent);
                return;
            }
            _Report(parent, $"Expected {_tables.DisplayNames[symbolId]}");
            _Recover(parent, symbolId);
            if (Current.SymbolId == symbolId)
                Consume(parent);
        }
        /// <summary>
        /// A new node for a non-terminal, located at Current
        /// </summary>
        protected ParseNode NewNode(int symbolId)
        {
            var result = new ParseNode(symbolId, _tables.SymbolNames[symbolId], null, false, _current.Line, _current.Column, _current.Position);
            _root ??= result;
            return result;
        }
        /// <summary>
        /// For a left-associative loop: wraps what was parsed so far in a new node of the same symbol
        /// </summary>
        protected ParseNode Fold(ParseNode node)
        {
            var result = new ParseNode(node.SymbolId, node.Symbol, null, false, node.Line, node.Column, node.Position);
            result.Add(node);
            return result;
        }
        /// <summary>
        /// For an operator rule applying an infix or postfix operator: a new node for the given symbol, located
        /// where its first child, the left operand, starts, with that operand already in it
        /// </summary>
        protected ParseNode Wrap(int symbolId, ParseNode first)
        {
            var result = new ParseNode(symbolId, _tables.SymbolNames[symbolId], null, false, first.Line, first.Column, first.Position);
            result.Add(first);
            return result;
        }
        /// <summary>
        /// Reports that none of the expected symbols was found, then recovers
        /// </summary>
        protected void Error(ParseNode parent, string? expected, params int[] symbols)
        {
            if (null == expected)
            {
                var names = new List<string>();
                foreach (var s in symbols)
                    if (!names.Contains(_tables.DisplayNames[s]))
                        names.Add(_tables.DisplayNames[s]);
                expected = 0 == names.Count ? "input this rule can match (it can't match any)"
                    : 1 == names.Count ? names[0]
                    : 2 == names.Count ? $"{names[0]} or {names[1]}"
                    : string.Join(", ", names.GetRange(0, names.Count - 1)) + ", or " + names[names.Count - 1];
            }
            _Report(parent, $"Expected {expected}");
            _Recover(parent, -1);
        }
        void _Report(ParseNode parent, string message)
        {
            if (_lastErrorIndex == _index)
            {
                // several failures at one token mean recovery stopped there and the parse isn't moving:
                // on the third, skip the token so a loop can't spin forever
                if (3 <= ++_errorsAtIndex)
                {
                    // at end of input there's nothing to skip, so a recursive rule could descend forever: give up
                    if (Current.SymbolId == _tables.EndOfInput)
                        throw new _Abandon();
                    var skipped = new ParseNode(_tables.Error, _tables.SymbolNames[_tables.Error], null, false, _current.Line, _current.Column, _current.Position) { IsError = true };
                    skipped.Add(_Leaf(Current));
                    parent.Add(skipped);
                    Advance();
                }
                return;
            }
            _lastErrorIndex = _index;
            _errorsAtIndex = 1;
            // follow-on errors before anything has matched again are usually noise from the first one
            if (_quiet) return;
            _quiet = true;
            // a literal terminal's display name is already its text in quotes; others show the text after the name
            var display = _tables.DisplayNames[Current.SymbolId];
            var found = Current.SymbolId == _tables.EndOfInput ? "end of input"
                : Current.SymbolId == _tables.Error ? $"unrecognized input \"{Current.Value}\""
                : display.StartsWith("\"", StringComparison.Ordinal) ? display
                : $"{display} {Current.Value}";
            _errors.Add(new ParseError($"{message} but found {found}", _current.Line, _current.Column, _current.Position));
        }
        bool _IsRecoveryPoint(int id)
        {
            if (id == _tables.EndOfInput || _tables.Sync[id]) return true;
            for (var i = _active.Count - 1; i >= 0; --i)
                if (_tables.Recovery[_active[i]][id]) return true;
            return false;
        }
        // skips tokens until one that the current construct or something enclosing it can continue with
        void _Recover(ParseNode parent, int stopAt)
        {
            ParseNode? err = null;
            while (Current.SymbolId != stopAt && !_IsRecoveryPoint(Current.SymbolId))
            {
                err ??= new ParseNode(_tables.Error, _tables.SymbolNames[_tables.Error], null, false, _current.Line, _current.Column, _current.Position) { IsError = true };
                err.Add(_Leaf(Current));
                Advance();
            }
            if (null != err)
                parent.Add(err);
        }
        /// <summary>
        /// Marks the start of a non-terminal, for error recovery
        /// </summary>
        protected void Enter(int nonTerminal) => _active.Add(nonTerminal);
        /// <summary>
        /// Marks the end of the non-terminal most recently entered
        /// </summary>
        protected virtual void Leave() => _active.RemoveAt(_active.Count - 1);
        sealed class _Abandon : Exception { }

        /// <summary>
        /// Runs the start production and reports anything left over after it. If the input ends in a way
        /// recovery can't get past, returns the tree built so far.
        /// </summary>
        protected ParseNode Run(Func<ParseNode> start)
        {
            try
            {
                var root = start();
                ExpectEnd(root);
                return root;
            }
            catch (_Abandon)
            {
                _active.Clear();
                return _root!;
            }
        }
        /// <summary>
        /// Reports anything left over after the start symbol
        /// </summary>
        protected void ExpectEnd(ParseNode root)
        {
            if (_current.SymbolId == _tables.EndOfInput) return;
            _Report(root, "Expected end of input");
            ParseNode? err = null;
            while (_current.SymbolId != _tables.EndOfInput)
            {
                err ??= new ParseNode(_tables.Error, _tables.SymbolNames[_tables.Error], null, false, _current.Line, _current.Column, _current.Position) { IsError = true };
                err.Add(_Leaf(Current));
                Advance();
            }
            if (null != err) root.Add(err);
        }
    }

    #region Lexer controller
    /// <summary>
    /// A lexer as a <see cref="LexerController"/> uses it: lexes from a reader, reporting positions, lines and
    /// columns from the given start. The static Tokenize a Luthor lexer has, or any adapter with its shape.
    /// </summary>
    public delegate IEnumerable<Token> TokenizeFunc(TextReader reader, long position, int line, int column, int tabWidth);
    /// <summary>
    /// A lexer that can carry on from the state it put in the <see cref="Token.Tag"/> of the last token before
    /// it stopped. Optional: see <see cref="LexerMode.Resume"/>.
    /// </summary>
    public delegate IEnumerable<Token> ResumeFunc(TextReader reader, long position, int line, int column, int tabWidth, object tag);
    /// <summary>
    /// Parses an island: the shared production's TryParse entry point of an external grammar's parser
    /// </summary>
    public delegate bool IslandParseFunc(IEnumerable<Token> tokens, out ParseNode result, out IReadOnlyList<ParseError> errors);
    /// <summary>
    /// Decides, from a token as the lexer produced it and the token before it, whether another lexer takes over
    /// </summary>
    public delegate bool LexerSwitchWhen(Token token, Token previous);

    /// <summary>
    /// A lexer and what a <see cref="LexerController"/> does around it: islands that start after its tokens,
    /// and terminals another lexer produces instead. Ids are the lexer's own, counting terminals from 0.
    /// </summary>
    public sealed class LexerMode
    {
        public LexerMode(TokenizeFunc tokenize, IslandRule[]? islands = null, LexerSwitch[]? switches = null, ResumeFunc? resume = null)
        {
            Tokenize = tokenize ?? throw new ArgumentNullException(nameof(tokenize));
            Islands = islands ?? Array.Empty<IslandRule>();
            Switches = switches ?? Array.Empty<LexerSwitch>();
            Resume = resume;
        }
        /// <summary>
        /// The lexer. Text it can't match it reports as a token with SymbolId -1, holding the text it skipped.
        /// </summary>
        public TokenizeFunc Tokenize { get; }
        public IReadOnlyList<IslandRule> Islands { get; }
        public IReadOnlyList<LexerSwitch> Switches { get; }
        /// <summary>
        /// Used instead of <see cref="Tokenize"/> to restart the lexer when the last token it produced before
        /// stopping has a <see cref="Token.Tag"/>. Null: the lexer always starts fresh.
        /// </summary>
        public ResumeFunc? Resume { get; }
    }

    /// <summary>
    /// An island: after one of <see cref="After"/>, the text is another language's, lexed by
    /// <see cref="Inner"/> and parsed by <see cref="Parse"/>, until a <see cref="Pop"/> that balances the
    /// <see cref="Nest"/>s before it, an error from the inner lexer (unless <see cref="PopOnError"/> is false),
    /// or the end of the input. When <see cref="Nest"/> and <see cref="Pop"/> are the same terminal, as a quote
    /// would be, the first one opens and the next one ends the island. The enclosing lexer sees it as one token
    /// of <see cref="Terminal"/>.
    /// </summary>
    public sealed class IslandRule
    {
        public IslandRule(int terminal, int[] after, LexerMode inner, IslandParseFunc parse, int nest = -1, int pop = -1, bool popOnError = true)
        {
            Terminal = terminal;
            After = after ?? throw new ArgumentNullException(nameof(after));
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Parse = parse ?? throw new ArgumentNullException(nameof(parse));
            Nest = nest;
            Pop = pop;
            PopOnError = popOnError;
        }
        /// <summary>
        /// The island's terminal, in the enclosing lexer's ids
        /// </summary>
        public int Terminal { get; }
        /// <summary>
        /// The enclosing lexer's terminals the island starts after
        /// </summary>
        public int[] After { get; }
        public LexerMode Inner { get; }
        public IslandParseFunc Parse { get; }
        /// <summary>
        /// The inner lexer's terminal that opens a nested pair, or -1. If it's also <see cref="Pop"/>, it opens and
        /// closes a pair that can't nest, as a quote does.
        /// </summary>
        public int Nest { get; }
        /// <summary>
        /// The inner lexer's terminal that ends the island, once it balances every <see cref="Nest"/> before it, or -1
        /// </summary>
        public int Pop { get; }
        public bool PopOnError { get; }
    }

    /// <summary>
    /// A terminal another lexer produces: where the enclosing lexer produces one of <see cref="On"/>, and
    /// <see cref="When"/> (if any) agrees, <see cref="Tokenize"/> lexes from there instead. If what it lexes is
    /// <see cref="LexedAs"/>, that becomes a token of <see cref="Terminal"/>, and the enclosing lexer goes on after it.
    /// </summary>
    public sealed class LexerSwitch
    {
        public LexerSwitch(int terminal, int[] on, TokenizeFunc tokenize, int lexedAs, LexerSwitchWhen? when = null)
        {
            Terminal = terminal;
            On = on ?? throw new ArgumentNullException(nameof(on));
            Tokenize = tokenize ?? throw new ArgumentNullException(nameof(tokenize));
            LexedAs = lexedAs;
            When = when;
        }
        /// <summary>
        /// The terminal, in the enclosing lexer's ids
        /// </summary>
        public int Terminal { get; }
        public int[] On { get; }
        public TokenizeFunc Tokenize { get; }
        /// <summary>
        /// The terminal in <see cref="Tokenize"/>'s own ids
        /// </summary>
        public int LexedAs { get; }
        public LexerSwitchWhen? When { get; }
    }

    /// <summary>
    /// The tokens of text in more than one language. It runs one lexer at a time over a buffer it owns and
    /// decides every switch as it lexes, from the tokens so far: an island starts after the tokens its
    /// <see cref="IslandRule.After"/> names and ends where the island's lexer says (its pop, or an error), and a
    /// <see cref="LexerSwitch"/> relexes one token with another lexer. Each switch restarts the lexer that takes
    /// over at the end of the last token produced, so lexers needn't know they're embedded.
    /// </summary>
    /// <remarks>
    /// An island is parsed as it's reached, by its external grammar's parser over the island's tokens, and
    /// comes out as one token whose <see cref="Token.Tag"/> is the <see cref="IslandParse"/>. A parser reading
    /// these tokens never sees inside an island, however far it looks ahead.
    /// <para>
    /// Lexers must report each token's Value as exactly the text it matched (or empty, for a token they make up),
    /// and text they can't match as a token with SymbolId -1: the controller works out where to restart from
    /// them, and where an island's text ends. Lines and
    /// columns are counted as Luthor lexers count them: \n ends a line, a tab moves to the next tab stop, other
    /// control characters take no width, and a surrogate pair is one column.
    /// </para>
    /// </remarks>
    public sealed class LexerController : IEnumerable<Token>
    {
        readonly LexerMode _mode;
        readonly Func<TextReader> _open;
        readonly long _position;
        readonly int _line, _column, _tabWidth;
        bool _opened;
        readonly bool _reopenable;

        public LexerController(LexerMode mode, TextReader reader, long position = 0, int line = 1, int column = 1, int tabWidth = 4)
            : this(mode, () => reader ?? throw new ArgumentNullException(nameof(reader)), false, position, line, column, tabWidth) { }
        public LexerController(LexerMode mode, string text, long position = 0, int line = 1, int column = 1, int tabWidth = 4)
            : this(mode, () => new StringReader(text ?? throw new ArgumentNullException(nameof(text))), true, position, line, column, tabWidth) { }
        LexerController(LexerMode mode, Func<TextReader> open, bool reopenable, long position, int line, int column, int tabWidth)
        {
            _mode = mode ?? throw new ArgumentNullException(nameof(mode));
            if (tabWidth < 1) throw new ArgumentOutOfRangeException(nameof(tabWidth));
            if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
            if (line < 1) throw new ArgumentOutOfRangeException(nameof(line));
            if (column < 1) throw new ArgumentOutOfRangeException(nameof(column));
            _open = open;
            _reopenable = reopenable;
            _position = position;
            _line = line;
            _column = column;
            _tabWidth = tabWidth;
        }

        public IEnumerator<Token> GetEnumerator()
        {
            // a reader can only be read once
            if (_opened && !_reopenable) throw new InvalidOperationException("The tokens of a TextReader can only be enumerated once");
            _opened = true;
            return new _Run(_mode, new _Source(_open(), _position), _position, _line, _column, _tabWidth).Tokens().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// The line and column after the text, starting from the given ones, counted as Luthor lexers count them
        /// </summary>
        public static void Advance(string text, int tabWidth, ref int line, ref int column)
        {
            var afterHigh = false;
            foreach (var c in text)
            {
                if ('\n' == c) { ++line; column = 1; afterHigh = false; }
                else if ('\t' == c) { column = ((column - 1) / tabWidth + 1) * tabWidth + 1; afterHigh = false; }
                else if (c < ' ') afterHigh = false;
                else if (char.IsLowSurrogate(c) && afterHigh) afterHigh = false;
                else { ++column; afterHigh = char.IsHighSurrogate(c); }
            }
        }

        // the input, read as lexers need it and dropped once no lexer can restart before it. Positions are absolute.
        sealed class _Source
        {
            readonly TextReader _reader;
            char[] _buf = new char[4096];
            long _base; // the position of _buf[0]
            int _count;
            bool _eof;
            public _Source(TextReader reader, long position)
            {
                _reader = reader;
                _base = position;
            }
            // the character at the position, or -1 past the end
            public int At(long position)
            {
                while (position >= _base + _count)
                {
                    if (_eof || !_Fill()) return -1;
                }
                return _buf[position - _base];
            }
            bool _Fill()
            {
                if (_count == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
                var n = _reader.Read(_buf, _count, _buf.Length - _count);
                if (0 >= n)
                {
                    _eof = true;
                    return false;
                }
                _count += n;
                return true;
            }
            // copies up to count characters from the position, returning how many
            public int Read(long position, char[] buffer, int index, int count)
            {
                if (count <= 0 || -1 == At(position)) return 0;
                var available = (int)Math.Min(count, _base + _count - position);
                Array.Copy(_buf, position - _base, buffer, index, available);
                return available;
            }
            public string Text(long from, long to) => from >= to ? "" : new string(_buf, (int)(from - _base), (int)(to - from));
            // nothing before floor is needed again
            public void Release(long floor)
            {
                var drop = (int)Math.Min(floor - _base, _count);
                // only when it's worth moving what's left
                if (drop < 4096 || drop * 2 < _count) return;
                Array.Copy(_buf, drop, _buf, 0, _count - drop);
                _count -= drop;
                _base += drop;
            }
        }

        // what a lexer reads: the source from a position on
        sealed class _View : TextReader
        {
            readonly _Source _source;
            long _position;
            public _View(_Source source, long position)
            {
                _source = source;
                _position = position;
            }
            public override int Peek() => _source.At(_position);
            public override int Read()
            {
                var c = _source.At(_position);
                if (-1 != c) ++_position;
                return c;
            }
            public override int Read(char[] buffer, int index, int count)
            {
                var n = _source.Read(_position, buffer, index, count);
                _position += n;
                return n;
            }
        }

        // one lexer in use: the outermost, or an island's
        sealed class _Level
        {
            public _Level(LexerMode mode, IslandRule? island, long end, int line, int column, object? tag)
            {
                Mode = mode;
                Island = island;
                End = end;
                EndLine = line;
                EndColumn = column;
                Tag = tag;
            }
            public readonly LexerMode Mode;
            public readonly IslandRule? Island;
            // null: start the lexer at End before the next token
            public IEnumerator<Token>? Lexer;
            // the furthest end of the tokens produced at this level: where its lexer restarts
            public long End;
            public int EndLine, EndColumn;
            // the Tag of the last token its lexer produced, for Resume
            public object? Tag;
            public Token Previous = new Token(-1, "", 0, 0, -1);
            // an island to start before the next token
            public IslandRule? Pending;
            public int Depth; // nesting, in an island with Nest
            public bool Done;
        }

        sealed class _Run
        {
            readonly LexerMode _mode;
            readonly _Source _source;
            readonly long _position;
            readonly int _line, _column, _tabWidth;
            readonly List<_Level> _levels = new();

            public _Run(LexerMode mode, _Source source, long position, int line, int column, int tabWidth)
            {
                _mode = mode;
                _source = source;
                _position = position;
                _line = line;
                _column = column;
                _tabWidth = tabWidth;
            }

            public IEnumerable<Token> Tokens()
            {
                var top = new _Level(_mode, null, _position, _line, _column, null);
                _levels.Add(top);
                try
                {
                    while (true)
                    {
                        var t = _Next(top);
                        if (null == t) yield break;
                        yield return t.Value;
                    }
                }
                finally
                {
                    foreach (var l in _levels) l.Lexer?.Dispose();
                }
            }

            void _Stop(_Level level)
            {
                level.Lexer?.Dispose();
                level.Lexer = null;
            }

            bool _Lex(_Level level, out Token token)
            {
                if (null == level.Lexer)
                {
                    var view = new _View(_source, level.End);
                    var tokens = null != level.Mode.Resume && null != level.Tag
                        ? level.Mode.Resume(view, level.End, level.EndLine, level.EndColumn, _tabWidth, level.Tag)
                        : level.Mode.Tokenize(view, level.End, level.EndLine, level.EndColumn, _tabWidth);
                    level.Lexer = tokens.GetEnumerator();
                }
                if (level.Lexer.MoveNext())
                {
                    token = level.Lexer.Current;
                    return true;
                }
                token = default;
                return false;
            }

            // the next token at the level, or null where it ends
            public Token? _Next(_Level level)
            {
                if (level.Done) return null;
                if (null != level.Pending)
                {
                    var rule = level.Pending;
                    level.Pending = null;
                    return _Island(level, rule);
                }
                if (!_Lex(level, out var t))
                {
                    level.Done = true;
                    _Stop(level);
                    return null;
                }
                // the island's lexer can't read this: the island ends before it, and the enclosing lexer goes
                // on from the end of the island's last token
                if (null != level.Island && level.Island.PopOnError && 0 > t.SymbolId)
                {
                    level.Done = true;
                    _Stop(level);
                    return null;
                }
                var fromLexer = true;
                foreach (var sw in level.Mode.Switches)
                {
                    if (0 > Array.IndexOf(sw.On, t.SymbolId)) continue;
                    if (null != sw.When && !sw.When(t, level.Previous)) continue;
                    var other = _LexOne(sw.Tokenize, t.Position, t.Line, t.Column);
                    if (null == other || other.Value.SymbolId != sw.LexedAs) continue;
                    t = new Token(sw.Terminal, other.Value.Value, t.Line, t.Column, t.Position);
                    // the level's lexer read on with its own idea of the text; it restarts after this token
                    _Stop(level);
                    fromLexer = false;
                    break;
                }
                _Produced(level, t, fromLexer);
                if (null != level.Island && -1 != level.Island.Pop)
                {
                    var rule = level.Island;
                    var ends = false;
                    // a delimiter that both opens and closes, like a quote, can't nest: the one after the opener ends it
                    if (t.SymbolId == rule.Nest && rule.Nest == rule.Pop) ends = 0 < level.Depth++;
                    else if (t.SymbolId == rule.Nest) ++level.Depth;
                    else if (t.SymbolId == rule.Pop) ends = 0 >= --level.Depth;
                    if (ends)
                    {
                        level.Done = true;
                        _Stop(level);
                        return t;
                    }
                }
                foreach (var island in level.Mode.Islands)
                    if (0 <= Array.IndexOf(island.After, t.SymbolId))
                    {
                        level.Pending = island;
                        // the level's lexer may have read into the island; it restarts after it
                        _Stop(level);
                        break;
                    }
                return t;
            }

            Token? _LexOne(TokenizeFunc tokenize, long position, int line, int column)
            {
                using var e = tokenize(new _View(_source, position), position, line, column, _tabWidth).GetEnumerator();
                return e.MoveNext() ? e.Current : null;
            }

            void _Produced(_Level level, Token t, bool fromLexer)
            {
                level.Previous = t;
                if (fromLexer) level.Tag = t.Tag;
                var value = t.Value ?? "";
                var end = t.Position + value.Length;
                // a token a lexer made up (empty) can sit anywhere; only one that reaches further moves the restart point
                if (end >= level.End && (0 < value.Length || end > level.End))
                {
                    int line = t.Line, column = t.Column;
                    Advance(value, _tabWidth, ref line, ref column);
                    level.End = end;
                    level.EndLine = line;
                    level.EndColumn = column;
                }
                _Release();
            }

            void _Release()
            {
                var floor = long.MaxValue;
                foreach (var l in _levels) floor = Math.Min(floor, l.End);
                _source.Release(floor);
            }

            // parses an island with its grammar, and makes it one token of the enclosing level
            Token _Island(_Level outer, IslandRule rule)
            {
                var level = new _Level(rule.Inner, rule, outer.End, outer.EndLine, outer.EndColumn, null);
                _levels.Add(level);
                var stream = new _IslandTokens(this, level);
                ParseNode node;
                IReadOnlyList<ParseError> errors;
                try
                {
                    rule.Parse(stream, out node, out errors);
                    // a parser that gave up early leaves the rest of the island unread
                    stream.Drain();
                }
                finally
                {
                    _Stop(level);
                    _levels.Remove(level);
                }
                var first = stream.First;
                var start = first?.Position ?? outer.End;
                var token = new Token(rule.Terminal, _source.Text(start, level.End), first?.Line ?? outer.EndLine, first?.Column ?? outer.EndColumn, start)
                {
                    Tag = new IslandParse(node, errors)
                };
                outer.Previous = token;
                outer.Tag = null;
                outer.End = level.End;
                outer.EndLine = level.EndLine;
                outer.EndColumn = level.EndColumn;
                _Release();
                return token;
            }

            // an island's tokens, as its parser reads them. One pass: the parser's and Drain share it.
            sealed class _IslandTokens : IEnumerable<Token>
            {
                readonly _Run _run;
                readonly _Level _level;
                IEnumerator<Token>? _e;
                public Token? First;
                public _IslandTokens(_Run run, _Level level)
                {
                    _run = run;
                    _level = level;
                }
                IEnumerable<Token> _Tokens()
                {
                    while (true)
                    {
                        var t = _run._Next(_level);
                        if (null == t) yield break;
                        First ??= t;
                        yield return t.Value;
                    }
                }
                public IEnumerator<Token> GetEnumerator() => _e ??= _Tokens().GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                public void Drain()
                {
                    var e = GetEnumerator();
                    while (e.MoveNext()) { }
                }
            }
        }
    }
    #endregion
}
