using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	/// <summary>
	/// The parsing algorithm a synthesized parser uses, and the base class it derives from
	/// </summary>
	public enum ParserEngine
	{
		/// <summary>
		/// The lightest one the grammar allows: LL1 if every decision needs one token, LLk if every
		/// decision is settled within a fixed number of tokens, AllStar otherwise
		/// </summary>
		Auto,
		/// <summary>
		/// One token of lookahead everywhere. Derives from LL1ParserBase.
		/// </summary>
		LL1,
		/// <summary>
		/// Up to <see cref="GrammarAnalysis.MaxK"/> tokens of lookahead, decided at synthesis time. Derives from LLkParserBase.
		/// </summary>
		LLk,
		/// <summary>
		/// Adaptive prediction at parse time for decisions that need more than one token. Derives from AllStarParserBase.
		/// </summary>
		AllStar
	}

	public static class ParserEngines
	{
		/// <summary>
		/// The name of the runtime base class for an engine
		/// </summary>
		public static string BaseClassName(this ParserEngine engine) => engine switch
		{
			ParserEngine.LL1 => "LL1ParserBase",
			ParserEngine.LLk => "LLkParserBase",
			ParserEngine.AllStar => "AllStarParserBase",
			_ => throw new ArgumentException("Auto has no base class; analyze the grammar to choose an engine", nameof(engine))
		};
		public static string DisplayName(this ParserEngine engine) => engine switch
		{
			ParserEngine.LL1 => "LL(1)",
			ParserEngine.LLk => "LL(k)",
			ParserEngine.AllStar => "ALL(*)",
			_ => "automatic"
		};
	}

	/// <summary>
	/// The grammar as an augmented transition network, the form ALL(*) prediction simulates. Each
	/// non-terminal gets a start and a stop state; each decision gets a state whose epsilon transitions
	/// lead to its branches in order (for a loop or optional: in, then out); each reference to a
	/// non-terminal gets the state to return to after the call.
	/// </summary>
	public sealed class GrammarAtn
	{
		readonly List<List<int>> _states = new(); // kind, a, b triples
		readonly Dictionary<Expression, int> _decisionStates = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<Expression, int> _returnStates = new(ReferenceEqualityComparer.Instance);
		readonly int[] _rules;
		readonly SymbolTable _s;

		internal GrammarAtn(GrammarAnalysis a)
		{
			_s = a.Symbols;
			var n = _s.NonTerminalCount;
			_rules = new int[n * 2];
			for (var i = 0; i < n; ++i)
			{
				_rules[i * 2] = _New();
				_rules[i * 2 + 1] = _New();
			}
			for (var i = 0; i < n; ++i)
			{
				var p = _s.GetProduction(i)!;
				var end = _Build(p.Expression, _rules[i * 2]);
				_Add(end, Runtime.Atn.Epsilon, _rules[i * 2 + 1], 0);
			}
			StartRule = _s.GetId(a.Grammar.StartSymbol!);
			EndOfInput = _s.EndOfInput;
		}

		public int StartRule { get; }
		public int EndOfInput { get; }
		public int StateCount => _states.Count;
		/// <summary>
		/// For each non-terminal in id order, its start state then its stop state
		/// </summary>
		public IReadOnlyList<int> Rules => _rules;
		/// <summary>
		/// The state where a decision chooses its branch
		/// </summary>
		public int GetDecisionState(Expression decision) => _decisionStates[decision];
		/// <summary>
		/// The state a reference to a non-terminal returns to after the call
		/// </summary>
		public int GetReturnState(SymbolRef reference) => _returnStates[reference];
		/// <summary>
		/// The states in the form <see cref="Runtime.Atn"/> takes: for each state its transition count, then kind, a, b for each
		/// </summary>
		public int[] SerializeStates()
		{
			var result = new List<int>();
			foreach (var s in _states)
			{
				result.Add(s.Count / 3);
				result.AddRange(s);
			}
			return result.ToArray();
		}
		/// <summary>
		/// A runtime network for the given adaptive decisions, in order
		/// </summary>
		public Runtime.Atn ToRuntime(IEnumerable<Decision> adaptive)
			=> new Runtime.Atn(StartRule, EndOfInput, _rules.ToArray(), SerializeStates(), adaptive.Select(d => GetDecisionState(d.Node)).ToArray());
		internal Runtime.Atn ToRuntime(int[] decisionStates) => new Runtime.Atn(StartRule, EndOfInput, _rules.ToArray(), SerializeStates(), decisionStates);
		/// <summary>
		/// The terminals a state's transitions match
		/// </summary>
		internal IEnumerable<int> MatchSymbols(int state)
		{
			var t = _states[state];
			for (var i = 0; i < t.Count; i += 3)
				if (Runtime.Atn.Match == t[i]) yield return t[i + 1];
		}

		int _New()
		{
			_states.Add(new List<int>());
			return _states.Count - 1;
		}
		void _Add(int from, int kind, int a, int b) => _states[from].AddRange(new[] { kind, a, b });
		void _Epsilon(int from, int to) => _Add(from, Runtime.Atn.Epsilon, to, 0);

		// builds e starting at state from; returns the state where it ends
		int _Build(Expression e, int from)
		{
			switch (e)
			{
				case EmptyExpression:
					return from;
				case SymbolRef sr:
					{
						var id = _s.GetId(sr.Name!);
						var to = _New();
						if (_s.IsTerminal(id))
							_Add(from, Runtime.Atn.Match, id, to);
						else
						{
							_Add(from, Runtime.Atn.Call, id, to);
							// a node shared between two places in the grammar would need two return states
							if (!_returnStates.TryAdd(sr, to)) throw new InvalidOperationException($"The reference {sr} appears twice in the analyzed grammar");
						}
						return to;
					}
				case Sequence seq:
					foreach (var item in seq.Items)
						from = _Build(item, from);
					return from;
				case Alternation alt:
					{
						var d = _Decision(e, from);
						var end = _New();
						foreach (var o in alt.Options)
						{
							var start = _New();
							_Epsilon(d, start);
							_Epsilon(_Build(o, start), end);
						}
						return end;
					}
				case OptionalExpression opt:
					{
						var d = _Decision(e, from);
						var start = _New();
						var end = _New();
						_Epsilon(d, start);
						_Epsilon(d, end);
						_Epsilon(_Build(opt.Body, start), end);
						return end;
					}
				case Repeat r when 1 == r.Min:
					{
						// the body once, then the decision to go around again, as the do/while does
						var start = _New();
						_Epsilon(from, start);
						var bodyEnd = _Build(r.Body, start);
						var d = _Decision(e, bodyEnd);
						var end = _New();
						_Epsilon(d, start);
						_Epsilon(d, end);
						return end;
					}
				case Repeat r:
					{
						var d = _Decision(e, from);
						var start = _New();
						var end = _New();
						_Epsilon(d, start);
						_Epsilon(d, end);
						_Epsilon(_Build(r.Body, start), d);
						return end;
					}
				default:
					throw new NotSupportedException(e.GetType().Name);
			}
		}

		// a fresh state holding only the decision's branches
		int _Decision(Expression e, int from)
		{
			var d = _New();
			_Epsilon(from, d);
			if (!_decisionStates.TryAdd(e, d)) throw new InvalidOperationException($"The decision {e} appears twice in the analyzed grammar");
			return d;
		}
	}
}
