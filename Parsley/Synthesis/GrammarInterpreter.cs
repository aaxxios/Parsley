using System;
using System.Collections.Generic;
using System.Linq;
using Parsley.Runtime;

namespace Parsley
{
	/// <summary>
	/// What the interpreter does where a synthesized parser would call the methods a grammar's hooks name.
	/// Each delegate gets the method name first. Any left null does nothing (and a contextual terminal is
	/// never chosen).
	/// </summary>
	public sealed class ParseHooks
	{
		/// <summary>
		/// For a contextual terminal: (when method, token, previous token) => is the token that terminal
		/// </summary>
		public Func<string, Token, Token, bool>? When { get; set; }
		/// <summary>
		/// (matched method, token)
		/// </summary>
		public Action<string, Token>? Matched { get; set; }
		/// <summary>
		/// (entering method)
		/// </summary>
		public Action<string>? Entering { get; set; }
		/// <summary>
		/// (parsed method, the finished node, or for a collapsed production the node its children went into)
		/// </summary>
		public Action<string, ParseNode>? Parsed { get; set; }
	}

	/// <summary>
	/// Parses directly from an analyzed grammar, making exactly the choices synthesized code makes,
	/// whichever engine the analysis chose. Useful for prototyping without compiling, and as an oracle
	/// for testing synthesized parsers.
	/// </summary>
	/// <remarks>
	/// It runs on the ALL(*) base, since that supports every kind of decision: fixed lookahead is just
	/// Peek, and adaptive decisions predict over the analysis's network exactly as a synthesized ALL(*)
	/// parser does.
	/// </remarks>
	public sealed class GrammarInterpreter : AllStarParserBase
	{
		readonly GrammarAnalysis _a;
		readonly SymbolTable _s;
		readonly ParseHooks? _hooks;
		// for each base terminal, its contextual terminals in declaration order
		readonly Dictionary<int, List<(int Id, string When)>> _contextual = new();

		public GrammarInterpreter(GrammarAnalysis analysis, IEnumerable<Token> tokens, ParseHooks? hooks = null) : base(tokens, CreateTables(_Checked(analysis)), analysis.RuntimeAtn)
		{
			_a = analysis;
			_s = analysis.Symbols;
			_hooks = hooks;
			foreach (var t in analysis.Grammar.Terminals.Where(t => t.IsContextual))
			{
				var b = _s.GetId(t.ContextualBase!);
				if (!_contextual.TryGetValue(b, out var list)) _contextual.Add(b, list = new List<(int, string)>());
				list.Add((_s.GetId(t.Name), t.When!));
			}
		}

		protected override int Classify(Token token, Token previous)
		{
			if (null != _hooks?.When && _contextual.TryGetValue(token.SymbolId, out var list))
				foreach (var (id, when) in list)
					if (_hooks.When(when, token, previous)) return id;
			return token.SymbolId;
		}

		protected override void OnMatched(Token token)
		{
			var m = _s.GetTerminal(token.SymbolId)?.Matched;
			if (null != m) _hooks?.Matched?.Invoke(m, token);
		}
		static GrammarAnalysis _Checked(GrammarAnalysis analysis)
		{
			if (analysis.HasErrors) throw new GrammarException(analysis.Messages);
			return analysis;
		}

		/// <summary>
		/// The runtime tables for a grammar, the same ones a synthesized parser embeds
		/// </summary>
		public static ParserTables CreateTables(GrammarAnalysis a)
		{
			var s = a.Symbols;
			var recovery = new int[s.NonTerminalCount][];
			for (var i = 0; i < s.NonTerminalCount; ++i)
			{
				var p = s.GetProduction(i)!;
				var set = new SortedSet<int>(a.GetFollowTokens(p.Name));
				void addSync(object? value)
				{
					if (!(value is string str)) return;
					foreach (var part in str.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
					{
						var id = s.GetId(part);
						if (-1 == id)
						{
							var t = a.Grammar.Terminals.FirstOrDefault(x => x.Literal == part);
							if (null != t) id = s.GetId(t.Name);
						}
						if (-1 != id && s.IsTerminal(id)) set.Add(id);
					}
				}
				addSync(p.Attributes.Get("sync"));
				foreach (var e in p.Expression.OwnDescendants())
					addSync(e.Attributes.Get("sync"));
				recovery[i] = set.ToArray();
			}
			return new ParserTables(
				s.NonTerminalCount,
				s.Names.ToArray(),
				Enumerable.Range(0, s.Count).Select(s.Display).ToArray(),
				Enumerable.Range(0, s.Count).Where(s.IsCollapsedTerminal).ToArray(),
				recovery,
				Enumerable.Range(0, s.Count).Where(i => s.GetTerminal(i)?.IsSync ?? false).ToArray(),
				s.EndOfInput,
				s.Error,
				Enumerable.Range(0, s.Count).Where(i => s.GetTerminal(i)?.IsContextual ?? false).ToArray());
		}

		public ParseNode Parse()
		{
			return Run(() => _Production(_a.Grammar.GetProduction(_a.Grammar.StartSymbol!)!, null, -1)!);
		}

		// the operator rule being applied by the innermost loop, and whether its form was too loose to apply here
		int _minLevel;
		bool _leave;

		// precedence climbing, as the synthesized method does it
		ParseNode _Operators(OperatorRule rule, int returnState, int minLevel)
		{
			var p = rule.Production;
			var id = _s.GetId(p.Name);
			Enter(id, returnState);
			var node = NewNode(id);
			if (null != p.Entering) _hooks?.Entering?.Invoke(p.Entering);
			_Expr(rule.Primary, ref node, p, true);
			if (null != rule.Loop)
			{
				var d = _a.GetDecision(rule.Loop)!;
				while (0 == _Choose(d))
				{
					_minLevel = minLevel;
					_Expr(rule.Loop.Body, ref node, p, false);
					if (_leave)
					{
						_leave = false;
						break;
					}
				}
			}
			if (null != p.Parsed) _hooks?.Parsed?.Invoke(p.Parsed, node);
			Leave();
			return node;
		}

		void _Form(OperatorForm f, ref ParseNode node, Production p)
		{
			var symbol = _s.GetId(f.Symbol);
			switch (f.Kind)
			{
				case OperatorKind.Atom:
					_Expr(f.Option, ref node, p, false, true);
					return;
				case OperatorKind.Prefix:
					if (null != f.Production) node = NewNode(symbol);
					break;
				default:
					// too loose to apply here: the call waiting for this operand applies it
					if (f.Level < _minLevel)
					{
						_leave = true;
						return;
					}
					node = Wrap(symbol, node);
					break;
			}
			foreach (var item in f.Operator)
				_Expr(item, ref node, p, false);
			if (null == f.Operand) return;
			var level = OperatorKind.Infix == f.Kind && !f.IsRightAssociative ? f.Level + 1 : f.Level;
			var ret = ParserEngine.AllStar == _a.Engine ? _a.Atn.GetReturnState(f.Operand) : -1;
			node.Add(_Operators(f.Rule, ret, level));
		}

		ParseNode? _Production(Production p, ParseNode? parent, int returnState)
		{
			if (null != p.Operators) return _Operators(p.Operators, returnState, 0);
			var id = _s.GetId(p.Name);
			Enter(id, returnState);
			ParseNode result;
			if (p.IsCollapsed)
			{
				result = parent!;
				if (null != p.Entering) _hooks?.Entering?.Invoke(p.Entering);
				_Expr(p.Expression, ref result, p, true);
				if (null != p.Parsed) _hooks?.Parsed?.Invoke(p.Parsed, result);
				Leave();
				return null;
			}
			result = NewNode(id);
			if (null != p.Entering) _hooks?.Entering?.Invoke(p.Entering);
			_Expr(p.Expression, ref result, p, true);
			if (null != p.Parsed) _hooks?.Parsed?.Invoke(p.Parsed, result);
			Leave();
			return result;
		}

		void _Expr(Expression e, ref ParseNode node, Production p, bool isRoot, bool asWritten = false)
		{
			if (!asWritten && _a.GetOperatorForm(e) is OperatorForm form)
			{
				_Form(form, ref node, p);
				return;
			}
			switch (e)
			{
				case EmptyExpression:
					return;
				case SymbolRef sr:
					{
						var id = _s.GetId(sr.Name!);
						if (_s.IsTerminal(id))
							Expect(id, node);
						else
						{
							var callee = _s.GetProduction(id)!;
							// only adaptive prediction reads the return state, and only an ALL(*) grammar has a network built
							var ret = ParserEngine.AllStar == _a.Engine ? _a.Atn.GetReturnState(sr) : -1;
							var child = _Production(callee, node, ret);
							if (null != child) node.Add(child);
						}
						return;
					}
				case Sequence seq:
					foreach (var item in seq.Items)
						_Expr(item, ref node, p, false);
					return;
				case InlinedProduction ip:
					{
						// as the synthesized code does it: the production's node takes over the tokens factoring moved in front
						var q = _a.Grammar.GetProduction(ip.Production)!;
						var qid = _s.GetId(q.Name);
						if (q.IsCollapsed)
						{
							EnterInlined(qid);
							_Expr(ip.Body, ref node, q, ip.Whole);
							if (null != q.Parsed) _hooks?.Parsed?.Invoke(q.Parsed, node);
							LeaveInlined();
							return;
						}
						var inner = Adopt(qid, node, ip.Back, ip.Adopt);
						EnterInlined(qid);
						_Expr(ip.Body, ref inner, q, ip.Whole);
						if (null != q.Parsed) _hooks?.Parsed?.Invoke(q.Parsed, inner);
						LeaveInlined();
						node.Add(inner);
						return;
					}
			}
			var d = _a.GetDecision(e)!;
			switch (e)
			{
				case Alternation a:
					{
						var b = _Choose(d);
						if (0 <= b)
							_Expr(a.Options[b], ref node, p, false);
						else
						{
							var tokens = d.Branches.SelectMany(x => x).Select(x => x[0]).Distinct().OrderBy(x => x).ToArray();
							var expected = isRoot ? p.Attributes.GetString("expected") : null;
							Error(node, expected ?? d.Node.Attributes.GetString("expected"), tokens);
						}
						return;
					}
				case OptionalExpression o:
					if (0 == _Choose(d))
						_Expr(o.Body, ref node, p, false);
					return;
				case Repeat r:
					{
						var fold = r.LeftFold && !p.IsCollapsed;
						if (1 == r.Min)
						{
							do
							{
								if (fold) node = Fold(node);
								_Expr(r.Body, ref node, p, false);
							} while (0 == _Choose(d));
						}
						else
						{
							while (0 == _Choose(d))
							{
								if (fold) node = Fold(node);
								_Expr(r.Body, ref node, p, false);
							}
						}
						return;
					}
			}
		}

		// the same choice the synthesized code makes, including what happens on input that matches nothing
		int _Choose(Decision d)
		{
			if (d.IsAdaptive)
			{
				var b = AdaptivePredict(d.AdaptiveIndex, d.DefaultBranch);
				return -1 < b ? b : d.DefaultBranch;
			}
			if (1 == d.Depth)
			{
				var t = Current.SymbolId;
				// an inverted loop or optional test also stops at end of input
				if (d.Kind != DecisionKind.Alternation && 0 == d.DefaultBranch && t == _s.EndOfInput)
					return 1;
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						if (x[0] == t) return i;
				return d.DefaultBranch;
			}
			// walk the lookahead one token at a time, as the synthesized trie does. Input no branch matches goes to
			// the default branch, unless it gets past the first token: then to the default branch if it got as far
			// as any, else the earliest that did
			var prefix = new List<int>();
			HashSet<int>? alive = null;
			for (var level = 0; level < d.Depth; ++level)
			{
				var tok = 0 == level ? Current : Peek(level);
				prefix.Add(tok.SymbolId);
				var matches = new HashSet<int>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						if (x.Count > level && _Prefix(x, prefix)) matches.Add(i);
				if (0 == matches.Count)
					return null == alive || alive.Contains(d.DefaultBranch) ? d.DefaultBranch : alive.Min();
				if (1 == matches.Count) return matches.First();
				alive = matches;
			}
			return d.DefaultBranch;
		}
		static bool _Prefix(LookaheadString s, List<int> prefix)
		{
			for (var i = 0; i < prefix.Count; ++i)
				if (s[i] != prefix[i]) return false;
			return true;
		}
	}
}
