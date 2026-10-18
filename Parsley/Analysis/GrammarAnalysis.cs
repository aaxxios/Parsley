using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	public enum DecisionKind
	{
		/// <summary>
		/// <c>a | b | c</c>: branch i is option i
		/// </summary>
		Alternation,
		/// <summary>
		/// <c>[ a ]</c>: branch 0 takes it, branch 1 skips it
		/// </summary>
		Optional,
		/// <summary>
		/// <c>{ a }</c>: branch 0 goes around again, branch 1 exits
		/// </summary>
		Loop
	}

	/// <summary>
	/// One choice point and how to make it: the lookahead depth, which lookahead strings pick which
	/// branch, and what to do with input that matches none of them.
	/// </summary>
	public sealed class Decision
	{
		internal Decision(Production production, Expression node, DecisionKind kind, int index)
		{
			Production = production;
			Node = node;
			Kind = kind;
			Index = index;
		}
		public Production Production { get; }
		public Expression Node { get; }
		public DecisionKind Kind { get; }
		/// <summary>
		/// The decision's position among its production's decisions, in source order, from 1
		/// </summary>
		public int Index { get; }
		/// <summary>
		/// How many tokens it needs to look at, or 0 when no fixed number of tokens settles it
		/// </summary>
		public int Depth { get; internal set; }
		/// <summary>
		/// True when the decision is made at parse time by ALL(*) adaptive prediction rather than by a
		/// test synthesized from <see cref="Branches"/>
		/// </summary>
		public bool IsAdaptive { get; internal set; }
		/// <summary>
		/// The decision's number among the grammar's adaptive decisions, or -1
		/// </summary>
		public int AdaptiveIndex { get; internal set; } = -1;
		/// <summary>
		/// For each branch, the lookahead strings that choose it: each is the shortest prefix, at most Depth
		/// long, after which only that branch is possible. The sets don't overlap, except for a decision with a
		/// Depth of 0, where they're the single tokens each branch can start with.
		/// </summary>
		public IReadOnlyList<IReadOnlyCollection<LookaheadString>> Branches { get; internal set; } = Array.Empty<IReadOnlyCollection<LookaheadString>>();
		/// <summary>
		/// The branch taken when the input matches none of the strings, or -1 to report an error.
		/// For loops and optionals this decides whether code tests the continue set or the inverse of the exit set.
		/// </summary>
		public int DefaultBranch { get; internal set; } = -1;
		/// <summary>
		/// The policy used to settle input that matched more than one branch, if any was needed
		/// </summary>
		public string? AppliedPolicy { get; internal set; }
		/// <summary>
		/// Some of the lookahead strings, MaxK tokens long or ending at end of input, that matched more than
		/// one branch (up to 16 examples; <see cref="AmbiguityCount"/> says how many there are)
		/// </summary>
		public IReadOnlyList<LookaheadString> Ambiguities { get; internal set; } = Array.Empty<LookaheadString>();
		/// <summary>
		/// How many lookahead strings matched more than one branch
		/// </summary>
		public long AmbiguityCount { get; internal set; }
		/// <summary>
		/// The single tokens that start each branch, when Depth is 1
		/// </summary>
		public IReadOnlyList<int> TokensOf(int branch) => Branches[branch].Select(s => s[0]).Distinct().OrderBy(x => x).ToList();
		public override string ToString() => $"{Production.Name} #{Index} {Kind} {(IsAdaptive ? "adaptive" : "depth " + Depth.ToString())}: {Node}";
	}

	/// <summary>
	/// Analyzes a grammar for synthesis: removes left recursion, computes FIRST and FOLLOW for
	/// every expression node, and classifies every decision by the lookahead it needs.
	/// </summary>
	public sealed class GrammarAnalysis
	{
		sealed class KSets
		{
			public int K;
			public Dictionary<string, HashSet<LookaheadString>> ProductionFirst = new();
			public Dictionary<Expression, HashSet<LookaheadString>> ExprFirst = new(ReferenceEqualityComparer.Instance);
			public Dictionary<string, HashSet<LookaheadString>> ProductionFollow = new();
			public Dictionary<Expression, HashSet<LookaheadString>> NodeFollow = new(ReferenceEqualityComparer.Instance);
			public bool Converged;
		}

		readonly Dictionary<int, KSets> _sets = new();
		readonly Dictionary<Expression, Decision> _decisions = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<Expression, Expression?> _parents = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<Expression, Production> _owners = new(ReferenceEqualityComparer.Instance);
		readonly List<GrammarMessage> _messages = new();
		readonly Dictionary<Expression, OperatorForm> _forms = new(ReferenceEqualityComparer.Instance);
		readonly HashSet<Expression> _operatorLoops = new(ReferenceEqualityComparer.Instance);
		HashSet<string> _nullable = new();
		GrammarAtn? _atn;
		Runtime.Atn? _runtimeAtn;

		GrammarAnalysis(Grammar g, int maxK, ParserEngine engine)
		{
			Grammar = g;
			MaxK = maxK;
			RequestedEngine = engine;
			Engine = ParserEngine.Auto == engine ? ParserEngine.LLk : engine;
		}

		/// <summary>
		/// The grammar as analyzed: literals resolved and left recursion rewritten. The input grammar is not changed.
		/// </summary>
		public Grammar Grammar { get; }
		public int MaxK { get; }
		/// <summary>
		/// The engine asked for, which may be <see cref="ParserEngine.Auto"/>
		/// </summary>
		public ParserEngine RequestedEngine { get; }
		/// <summary>
		/// The engine the parser uses: the one asked for, or for Auto, the lightest the grammar allows
		/// </summary>
		public ParserEngine Engine { get; private set; }
		/// <summary>
		/// The decisions made by adaptive prediction, in the order of their <see cref="Decision.AdaptiveIndex"/>
		/// </summary>
		public IReadOnlyList<Decision> AdaptiveDecisions => Decisions.Where(d => d.IsAdaptive).OrderBy(d => d.AdaptiveIndex).ToList();
		/// <summary>
		/// The grammar as a transition network, for ALL(*) prediction
		/// </summary>
		public GrammarAtn Atn => _atn ??= new GrammarAtn(this);
		/// <summary>
		/// The runtime network a parser of this grammar predicts with, its predictions cached across parses
		/// </summary>
		public Runtime.Atn RuntimeAtn => _runtimeAtn ??= Atn.ToRuntime(AdaptiveDecisions);
		public SymbolTable Symbols { get; private set; } = null!;
		public IReadOnlyList<GrammarMessage> Messages => _messages;
		public bool HasErrors => _messages.Any(m => m.Level == ErrorLevel.Error);
		public IReadOnlyList<Decision> Decisions => _decisions.Values.OrderBy(d => Grammar.Productions.IndexOf(d.Production)).ThenBy(d => d.Index).ToList();
		public Decision? GetDecision(Expression node) => _decisions.TryGetValue(node, out var d) ? d : null;
		public Expression? GetParent(Expression node) => _parents.TryGetValue(node, out var p) ? p : null;
		public Production GetOwner(Expression node) => _owners[node];
		/// <summary>
		/// The operator form an alternative of an operator rule is, or null if the expression isn't one
		/// </summary>
		public OperatorForm? GetOperatorForm(Expression option) => _forms.TryGetValue(option, out var f) ? f : null;
		/// <summary>
		/// True for the loop over an operator rule's infix and postfix forms
		/// </summary>
		public bool IsOperatorLoop(Expression node) => _operatorLoops.Contains(node);
		public bool IsNullable(Expression e) => LeftRecursion.IsNullable(e, _nullable);
		/// <summary>
		/// The deepest lookahead any decision needs
		/// </summary>
		public int Depth => 0 == _decisions.Count ? 1 : _decisions.Values.Max(d => d.Depth);

		/// <summary>
		/// Analyzes the grammar, choosing the lightest engine it allows. Problems are reported in Messages rather than thrown.
		/// </summary>
		public static GrammarAnalysis Analyze(Grammar grammar, int? maxK = null) => Analyze(grammar, ParserEngine.Auto, maxK);

		/// <summary>
		/// The most tokens a fixed decision looks at when neither the caller nor the grammar's @maxk says
		/// </summary>
		public const int DefaultMaxK = 4;

		/// <summary>
		/// Analyzes the grammar for the given engine. Problems are reported in Messages rather than thrown.
		/// </summary>
		/// <param name="engine">The engine, or Auto to choose. LL1 limits lookahead to one token; LL1 and LLk
		/// settle decisions they can't decide with a policy (an error for an alternation without one)</param>
		/// <param name="maxK">The most tokens a fixed decision looks at. Null uses the grammar's @maxk, or
		/// <see cref="DefaultMaxK"/>. Decisions that need more are left to ALL(*), or to a policy.</param>
		public static GrammarAnalysis Analyze(Grammar grammar, ParserEngine engine, int? maxK = null)
		{
			var k = maxK ?? grammar.MaxK ?? DefaultMaxK;
			if (1 > k) throw new ArgumentOutOfRangeException(nameof(maxK));
			if (ParserEngine.LL1 == engine) k = 1;
			var g = grammar.Clone();
			g.ResolveLiterals();
			// the terminals the grammar's own lexer produces come first, so its ids match the parser's. Then
			// those from other lexers (lexer=), grouped by lexer, then islands, which the lexer controller
			// produces, and last contextual terminals, which only the parser assigns
			var groups = g.LexerGroups;
			var ordered = g.Terminals.Where(t => t.IsLexed)
				.Concat(groups.SelectMany(alias => g.Terminals.Where(t => t.Lexer == alias && !t.IsIsland)))
				.Concat(g.Terminals.Where(t => t.IsIsland))
				.Concat(g.Terminals.Where(t => t.IsContextual))
				.ToList();
			g.Terminals.Clear();
			g.Terminals.AddRange(ordered);
			var a = new GrammarAnalysis(g, k, engine);
			a._Run();
			return a;
		}

		void _Run()
		{
			_messages.AddRange(Grammar.Validate());
			if (HasErrors) return;
			OperatorRules.Rewrite(Grammar, _messages);
			if (HasErrors) return;
			LeftRecursion.Eliminate(Grammar, _messages);
			if (HasErrors) return;
			Symbols = new SymbolTable(Grammar);
			_nullable = LeftRecursion.ComputeNullable(Grammar);
			foreach (var p in Grammar.Productions)
				_Index(p, p.Expression, null);
			foreach (var p in Grammar.Productions)
				if (null != p.Operators)
				{
					foreach (var f in p.Operators.Forms)
						_forms[f.Option] = f;
					if (null != p.Operators.Loop)
						_operatorLoops.Add(p.Operators.Loop);
				}
			foreach (var p in Grammar.Productions)
			{
				var index = 0;
				_CollectDecisions(p, p.Expression, ref index);
			}
			// a rule with no finite derivation (every path leads back into itself) can never finish parsing
			var productive = _Productive();
			foreach (var p in Grammar.Productions)
				if (!productive.Contains(p.Name))
					_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"\"{p.Name}\" can't match any finite input, so parsing it always fails", p));
			foreach (var d in _decisions.Values.OrderBy(d => Grammar.Productions.IndexOf(d.Production)).ThenBy(d => d.Index))
				_Resolve(d);
			_ChooseEngine();
			_CheckIslandTriggers();
		}

		// the lexer controller starts an island after every token its after= names, wherever it is, so anything
		// else that can follow that token in the grammar can never be parsed. Islands that share a token are tried
		// in order: one with startsWith only starts where its lexer reads one of those first, and the first one
		// without starts everywhere else, so islands after it never start there.
		void _CheckIslandTriggers()
		{
			var islands = Grammar.Terminals.Where(t => t.IsIsland).ToList();
			var triggers = new List<TerminalDeclaration>();
			foreach (var island in islands)
				foreach (var name in island.GetNames("after"))
				{
					var trigger = Grammar.FindTerminal(name);
					if (null != trigger && !triggers.Contains(trigger)) triggers.Add(trigger);
				}
			foreach (var trigger in triggers)
			{
				var sharing = islands.Where(i => i.GetNames("after").Any(n => Grammar.FindTerminal(n) == trigger)).ToList();
				var fallback = sharing.FirstOrDefault(i => 0 == i.GetNames("startsWith").Count);
				if (null == fallback) continue;     // where none of them starts, the grammar's own lexer goes on
				foreach (var later in sharing.Skip(sharing.IndexOf(fallback) + 1))
					_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"The island \"{later.Name}\" never starts after \"{trigger.Name}\", since \"{fallback.Name}\" comes first and has no startsWith, so it always starts there", later));
				var ids = new HashSet<int>(sharing.Select(i => Symbols.GetId(i.Name)));
				var tid = Symbols.GetId(trigger.Name);
				var others = new SortedSet<int>();
				foreach (var p in Grammar.Productions)
					foreach (var sr in p.Expression.Descendants().OfType<SymbolRef>())
						if (sr.Name == trigger.Name)
							foreach (var x in GetFollow(sr, 1))
								if (0 < x.Count && !ids.Contains(x[0])) others.Add(x[0]);
				if (0 < others.Count)
					_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"The island \"{fallback.Name}\" starts after every \"{trigger.Name}\", but {string.Join(", ", others.Select(Symbols.Display))} can follow \"{trigger.Name}\" too, and won't be lexed as this grammar where that happens", fallback));
			}
		}

		bool _AllowsAdaptive => ParserEngine.Auto == RequestedEngine || ParserEngine.AllStar == RequestedEngine;

		void _ChooseEngine()
		{
			var decisions = Decisions;
			if (ParserEngine.Auto == RequestedEngine)
				Engine = decisions.Any(d => d.IsAdaptive) ? ParserEngine.AllStar
					: decisions.All(d => 1 >= d.Depth) ? ParserEngine.LL1
					: ParserEngine.LLk;
			if (ParserEngine.AllStar != Engine) return;
			// an ALL(*) parser predicts adaptively wherever one token isn't enough, except where a policy
			// was asked for: that settles the decision the same way in every engine
			var index = 0;
			foreach (var d in decisions)
			{
				if (!d.IsAdaptive && 1 < d.Depth && null == d.AppliedPolicy)
					d.IsAdaptive = true;
				if (d.IsAdaptive) d.AdaptiveIndex = index++;
			}
		}

		// the productions that derive at least one finite string of terminals
		HashSet<string> _Productive()
		{
			var result = new HashSet<string>();
			bool derives(Expression e) => e switch
			{
				EmptyExpression => true,
				SymbolRef sr => Symbols.IsTerminal(Symbols.GetId(sr.Name!)) || result.Contains(sr.Name!),
				Sequence seq => seq.Items.All(derives),
				Alternation alt => alt.Options.Any(derives),
				OptionalExpression => true,
				Repeat r => 0 == r.Min || derives(r.Body),
				_ => true
			};
			for (var changed = true; changed;)
			{
				changed = false;
				foreach (var p in Grammar.Productions)
					if (!result.Contains(p.Name) && derives(p.Expression))
						changed = result.Add(p.Name);
			}
			return result;
		}

		void _Index(Production p, Expression e, Expression? parent)
		{
			_parents[e] = parent;
			_owners[e] = p;
			foreach (var c in e.Children)
				_Index(p, c, e);
		}

		void _CollectDecisions(Production p, Expression e, ref int index)
		{
			switch (e)
			{
				case Alternation:
					_decisions[e] = new Decision(p, e, DecisionKind.Alternation, ++index);
					break;
				case OptionalExpression:
					_decisions[e] = new Decision(p, e, DecisionKind.Optional, ++index);
					break;
				case Repeat r:
					_decisions[e] = new Decision(p, e, DecisionKind.Loop, ++index);
					if (IsNullable(r.Body))
						_messages.Add(new GrammarMessage(ErrorLevel.Error, $"The loop body {r.Body} can match nothing, so the loop could repeat forever", r));
					break;
			}
			foreach (var c in e.Children)
				_CollectDecisions(p, c, ref index);
		}

		#region FIRST and FOLLOW
		KSets _Sets(int k)
		{
			if (_sets.TryGetValue(k, out var s) && s.Converged) return s;
			s = new KSets { K = k };
			_sets[k] = s;
			var alg = new LookaheadAlgebra(k, Symbols.EndOfInput);
			foreach (var p in Grammar.Productions)
				s.ProductionFirst[p.Name] = new HashSet<LookaheadString>();
			var changed = true;
			while (changed)
			{
				changed = false;
				foreach (var p in Grammar.Productions)
				{
					var f = _First(p.Expression, s, alg, false);
					var target = s.ProductionFirst[p.Name];
					foreach (var x in f)
						if (target.Add(x)) changed = true;
				}
			}
			// follows
			foreach (var p in Grammar.Productions)
				s.ProductionFollow[p.Name] = new HashSet<LookaheadString>();
			s.ProductionFollow[Grammar.StartSymbol!].Add(new LookaheadString(Symbols.EndOfInput));
			// a shared production is parsed on its own as an island, so the island's end can follow it
			foreach (var p in Grammar.SharedProductions)
				s.ProductionFollow[p.Name].Add(new LookaheadString(Symbols.EndOfInput));
			changed = true;
			while (changed)
			{
				changed = false;
				foreach (var p in Grammar.Productions)
					if (_WalkFollow(p.Expression, s.ProductionFollow[p.Name], s, alg))
						changed = true;
			}
			s.Converged = true;
			return s;
		}

		HashSet<LookaheadString> _First(Expression e, KSets s, LookaheadAlgebra alg, bool memo)
		{
			if (memo && s.ExprFirst.TryGetValue(e, out var cached)) return cached;
			HashSet<LookaheadString> result;
			switch (e)
			{
				case EmptyExpression:
					result = LookaheadAlgebra.EmptyOnly();
					break;
				case SymbolRef sr:
					if (s.ProductionFirst.TryGetValue(sr.Name!, out var pf))
						result = new HashSet<LookaheadString>(pf);
					else
						result = new HashSet<LookaheadString> { new LookaheadString(Symbols.GetId(sr.Name!)) };
					break;
				case Sequence seq:
					result = LookaheadAlgebra.EmptyOnly();
					foreach (var item in seq.Items)
					{
						result = alg.Concat(result, _First(item, s, alg, memo));
						if (0 == result.Count || alg.AllComplete(result)) break;
					}
					break;
				case Alternation a:
					result = new HashSet<LookaheadString>();
					foreach (var o in a.Options)
						result.UnionWith(_First(o, s, alg, memo));
					break;
				case OptionalExpression o:
					result = new HashSet<LookaheadString>(_First(o.Body, s, alg, memo)) { LookaheadString.Empty };
					break;
				case Repeat r:
					{
						var body = _First(r.Body, s, alg, memo);
						// star = {ε} ∪ body·star, to a fixed point
						var star = LookaheadAlgebra.EmptyOnly();
						while (true)
						{
							var next = alg.Concat(body, star);
							var before = star.Count;
							star.UnionWith(next);
							if (star.Count == before) break;
						}
						result = 1 == r.Min ? alg.Concat(body, star) : star;
						break;
					}
				default:
					throw new NotSupportedException(e.GetType().Name);
			}
			if (memo) s.ExprFirst[e] = result;
			return result;
		}

		// walks an expression with the set of strings that can follow it; returns true if any production follow grew
		bool _WalkFollow(Expression e, HashSet<LookaheadString> follow, KSets s, LookaheadAlgebra alg)
		{
			s.NodeFollow[e] = follow;
			var changed = false;
			switch (e)
			{
				case SymbolRef sr:
					if (s.ProductionFollow.TryGetValue(sr.Name!, out var pf))
						foreach (var x in follow)
							if (pf.Add(x)) changed = true;
					break;
				case Sequence seq:
					{
						var f = follow;
						for (var i = seq.Items.Count - 1; i >= 0; --i)
						{
							if (_WalkFollow(seq.Items[i], f, s, alg)) changed = true;
							f = alg.Concat(_First(seq.Items[i], s, alg, true), f);
						}
						break;
					}
				case Alternation a:
					foreach (var o in a.Options)
						if (_WalkFollow(o, follow, s, alg)) changed = true;
					break;
				case OptionalExpression o:
					if (_WalkFollow(o.Body, follow, s, alg)) changed = true;
					break;
				case Repeat r:
					{
						// after one pass of the body comes more passes or the exit
						var again = alg.Concat(_First(new Repeat(r.Body, 0), s, alg, false), follow);
						if (_WalkFollow(r.Body, again, s, alg)) changed = true;
						break;
					}
			}
			return changed;
		}

		/// <summary>
		/// FIRST_k of an expression, with ε as the empty string
		/// </summary>
		public IReadOnlyCollection<LookaheadString> GetFirst(Expression e, int k = 1)
		{
			var s = _Sets(k);
			return _First(e, s, new LookaheadAlgebra(k, Symbols.EndOfInput), true);
		}
		/// <summary>
		/// What can follow an expression node, k tokens deep
		/// </summary>
		public IReadOnlyCollection<LookaheadString> GetFollow(Expression e, int k = 1) => _Sets(k).NodeFollow[e];
		public IReadOnlyCollection<LookaheadString> GetProductionFollow(string production, int k = 1) => _Sets(k).ProductionFollow[production];
		public IReadOnlyCollection<LookaheadString> GetProductionFirst(string production, int k = 1) => _Sets(k).ProductionFirst[production];
		/// <summary>
		/// The terminals that can start an expression (ε excluded)
		/// </summary>
		public ISet<int> GetFirstTokens(Expression e) => new HashSet<int>(GetFirst(e, 1).Where(x => 0 < x.Count).Select(x => x[0]));
		public ISet<int> GetFollowTokens(string production) => new HashSet<int>(GetProductionFollow(production, 1).Select(x => x[0]));
		#endregion

		#region Decisions
		List<HashSet<LookaheadString>> _BranchSets(Decision d, int k)
		{
			var s = _Sets(k);
			var alg = new LookaheadAlgebra(k, Symbols.EndOfInput);
			var follow = s.NodeFollow[d.Node];
			var result = new List<HashSet<LookaheadString>>();
			switch (d.Node)
			{
				case Alternation a:
					foreach (var o in a.Options)
						result.Add(alg.Concat(_First(o, s, alg, true), follow));
					break;
				case OptionalExpression o:
					result.Add(alg.Concat(_First(o.Body, s, alg, true), follow));
					result.Add(new HashSet<LookaheadString>(follow));
					break;
				case Repeat r:
					result.Add(alg.Concat(_First(r.Body, s, alg, true), s.NodeFollow[r.Body]));
					result.Add(new HashSet<LookaheadString>(follow));
					break;
			}
			return result;
		}

		string? _Policy(Decision d)
		{
			// the decision's own attribute wins, then its production's
			return d.Node.Attributes.GetString("policy") ?? d.Production.Attributes.GetString("policy");
		}

		LookaheadExplorer? _explorer;

		void _Resolve(Decision d)
		{
			if (_operatorLoops.Contains(d.Node))
			{
				_ResolveOperatorLoop(d);
				return;
			}
			_explorer ??= new LookaheadExplorer(this, Decisions);
			var branchCount = d.Node is Alternation alt ? alt.Options.Count : 2;
			var policy = _Policy(d);
			var explicitPolicy = null != policy;
			// with no policy, ALL(*) settles at parse time what lookahead up to MaxK can't
			var adaptive = !explicitPolicy && _AllowsAdaptive;
			var applied = policy ?? (d.Kind == DecisionKind.Alternation ? "first" : "greedy");
			Func<IReadOnlyList<int>, int>? winner = null;
			if (!adaptive)
				winner = alts =>
				{
					if (d.Kind == DecisionKind.Alternation) return alts.Min();
					var w = "lazy" == applied ? 1 : 0;
					return alts.Contains(w) ? w : alts.Min();
				};
			var r = _explorer.Explore(d, branchCount, MaxK, winner);
			if (r.Resolved)
			{
				d.Depth = r.Depth;
				d.Branches = r.Branches;
				_ChooseDefault(d);
				return;
			}
			var conflictText = string.Join(", ", r.Conflicts.Take(4).Select(c => $"[{c.ToString(Symbols)}]")) + (4 < r.ConflictCount ? $" and {r.ConflictCount - 4} more" : "");
			d.Ambiguities = r.Conflicts;
			d.AmbiguityCount = r.ConflictCount;
			if (adaptive)
			{
				// leave it to ALL(*), which looks as far as it has to and, if the input really is ambiguous,
				// takes the earliest alternative, keeps a loop going, and takes an optional
				d.IsAdaptive = true;
				d.Depth = 0;
				d.Branches = _BranchSets(d, 1);
				_ChooseDefault(d);
				_messages.Add(new GrammarMessage(ErrorLevel.Message, $"In \"{d.Production.Name}\", {d.Node} can't be decided within {MaxK} tokens on {conflictText}, so it's predicted adaptively (ALL(*)) at parse time. If that input really is ambiguous, a <policy> settles it at synthesis time instead.", d.Node));
				return;
			}
			if (d.Kind == DecisionKind.Alternation && !explicitPolicy)
				_messages.Add(new GrammarMessage(ErrorLevel.Error, $"In \"{d.Production.Name}\", the alternatives of {d.Node} can't be told apart within {MaxK} tokens on {conflictText}. Add <policy=\"first\"> if the earliest alternative should win, or use the ALL(*) engine to look further.", d.Node));
			else if (!explicitPolicy)
				_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"In \"{d.Production.Name}\", {d.Node} is ambiguous on {conflictText}; resolved greedily (it keeps matching). Add <policy=\"greedy\"> or <policy=\"lazy\"> to choose explicitly.", d.Node));
			else if (d.Kind == DecisionKind.Alternation && "first" != policy)
				_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"policy=\"{policy}\" on an alternation acts like \"first\"", d.Node));
			d.Depth = r.Depth;
			d.Branches = r.Branches;
			d.AppliedPolicy = applied;
			_ChooseDefault(d);
		}

		// the loop of an operator rule goes around whenever the next token can start an infix or postfix form.
		// Without precedence, the rewritten rule is ambiguous there: an operand's own loop could take the
		// operator, or leave it to the loop of the call that's waiting for it. Precedence is what settles
		// that, at parse time, so one token is all the loop looks at.
		void _ResolveOperatorLoop(Decision d)
		{
			var sets = _BranchSets(d, 1);
			var go = new HashSet<LookaheadString>(sets[0].Where(x => 0 < x.Count));
			var tokens = new HashSet<int>(go.Select(x => x[0]));
			var stop = new HashSet<LookaheadString>(sets[1].Where(x => 0 < x.Count && !tokens.Contains(x[0])));
			d.Depth = 1;
			d.Branches = new[] { go, stop };
			if (stop.Count != sets[1].Count(x => 0 < x.Count))
				d.AppliedPolicy = "greedy";
			// test for the operators rather than for what can follow: anything else ends the operand, and
			// the caller says what it expected there
			d.DefaultBranch = 1;
		}

		// picks what unmatched input does, which also decides the shape of the emitted test
		void _ChooseDefault(Decision d)
		{
			switch (d.Kind)
			{
				case DecisionKind.Alternation:
					{
						var a = (Alternation)d.Node;
						var nullable = Enumerable.Range(0, a.Options.Count).Where(i => IsNullable(a.Options[i])).ToList();
						if (1 == nullable.Count)
							d.DefaultBranch = nullable[0];
						else if (2 == a.Options.Count && 1 == d.Depth)
						{
							// if/else: test the branch with fewer tokens, the other is the else
							d.DefaultBranch = d.TokensOf(0).Count <= d.TokensOf(1).Count ? 1 : 0;
						}
						else
							d.DefaultBranch = -1;
						break;
					}
				default:
					if (1 != d.Depth)
					{
						d.DefaultBranch = 1;
						break;
					}
					var c = d.TokensOf(0).Count;
					var x = d.TokensOf(1).Union(new[] { Symbols.EndOfInput }).Count();
					// test the inverse of the exit set only when it is strictly smaller; ties test the continue set,
					// which reads more naturally and doesn't depend on what follows the construct
					d.DefaultBranch = x < c ? 0 : 1;
					break;
			}
		}

		#endregion
	}
}
