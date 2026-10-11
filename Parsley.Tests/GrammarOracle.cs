using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley.Tests
{
	/// <summary>
	/// Ground truth for a grammar, independent of any engine: an Earley recognizer that says whether a
	/// token string is in the language, and a generator of random sentences
	/// </summary>
	sealed class GrammarOracle
	{
		readonly GrammarAnalysis _a;
		readonly SymbolTable _s;
		// BNF: each non-terminal (productions by name, helpers by "#n") has a list of right-hand sides.
		// a symbol is a terminal id (>= 0, as a string "t:id") or a non-terminal name
		readonly Dictionary<string, List<string[]>> _rules = new();
		readonly HashSet<string> _nullable = new();
		readonly Dictionary<string, int> _minLength = new();
		int _helpers;

		public GrammarOracle(GrammarAnalysis a)
		{
			_a = a;
			_s = a.Symbols;
			foreach (var p in a.Grammar.Productions)
				_rules[p.Name] = new List<string[]>();
			foreach (var p in a.Grammar.Productions)
				_rules[p.Name].AddRange(_Rhs(p.Expression));
			_ComputeNullable();
			_ComputeMinLength();
		}

		public string Start => _a.Grammar.StartSymbol!;
		static bool _IsTerminal(string sym) => sym.StartsWith("t:");
		static int _TerminalId(string sym) => int.Parse(sym.Substring(2));

		#region To BNF
		List<string[]> _Rhs(Expression e)
		{
			switch (e)
			{
				case Alternation alt:
					return alt.Options.SelectMany(_Rhs).ToList();
				default:
					return new List<string[]> { _Symbols(e).ToArray() };
			}
		}
		List<string> _Symbols(Expression e)
		{
			switch (e)
			{
				case EmptyExpression:
					return new List<string>();
				case SymbolRef sr:
					{
						var id = _s.GetId(sr.Name!);
						return new List<string> { _s.IsTerminal(id) ? "t:" + id : sr.Name! };
					}
				case Sequence seq:
					return seq.Items.SelectMany(_Symbols).ToList();
				case Alternation alt:
					{
						var h = _Helper();
						_rules[h].AddRange(alt.Options.SelectMany(_Rhs));
						return new List<string> { h };
					}
				case OptionalExpression opt:
					{
						var h = _Helper();
						_rules[h].Add(_Symbols(opt.Body).ToArray());
						_rules[h].Add(Array.Empty<string>());
						return new List<string> { h };
					}
				case Repeat r:
					{
						// h: body h | ε, and a one-or-more is body h
						var h = _Helper();
						var body = _Symbols(r.Body);
						_rules[h].Add(body.Append(h).ToArray());
						_rules[h].Add(Array.Empty<string>());
						return 1 == r.Min ? body.Append(h).ToList() : new List<string> { h };
					}
				default:
					throw new NotSupportedException(e.GetType().Name);
			}
		}
		string _Helper()
		{
			var h = "#" + (_helpers++).ToString();
			_rules[h] = new List<string[]>();
			return h;
		}
		void _ComputeNullable()
		{
			var changed = true;
			while (changed)
			{
				changed = false;
				foreach (var (nt, rhss) in _rules)
					if (!_nullable.Contains(nt) && rhss.Any(rhs => rhs.All(x => !_IsTerminal(x) && _nullable.Contains(x))))
					{
						_nullable.Add(nt);
						changed = true;
					}
			}
		}
		const int Infinite = int.MaxValue / 4;
		int _Len(string sym) => _IsTerminal(sym) ? 1 : _minLength.TryGetValue(sym, out var n) ? n : Infinite;
		int _Len(string[] rhs)
		{
			long l = 0;
			foreach (var x in rhs) l += _Len(x);
			return l >= Infinite ? Infinite : (int)l;
		}
		// the least height of a derivation tree: always choosing the right-hand side with the least height ends
		readonly Dictionary<string, int> _height = new();
		int _Height(string[] rhs)
		{
			var h = 0;
			foreach (var x in rhs)
				if (!_IsTerminal(x)) h = Math.Max(h, _height[x] >= Infinite ? Infinite : _height[x] + 1);
			return h;
		}
		void _ComputeMinLength()
		{
			foreach (var nt in _rules.Keys) _height[nt] = Infinite;
			for (var again = true; again;)
			{
				again = false;
				foreach (var (nt, rhss) in _rules)
				{
					var best = rhss.Select(_Height).DefaultIfEmpty(Infinite).Min();
					if (best < _height[nt])
					{
						_height[nt] = best;
						again = true;
					}
				}
			}
			foreach (var nt in _rules.Keys) _minLength[nt] = Infinite;
			var changed = true;
			while (changed)
			{
				changed = false;
				foreach (var (nt, rhss) in _rules)
				{
					var best = rhss.Select(_Len).DefaultIfEmpty(Infinite).Min();
					if (best < _minLength[nt])
					{
						_minLength[nt] = best;
						changed = true;
					}
				}
			}
		}
		/// <summary>
		/// The language has at least one finite sentence
		/// </summary>
		public bool IsProductive => _minLength[Start] < Infinite;
		#endregion

		#region Earley
		readonly record struct Item(string Lhs, int Rhs, int Dot, int Origin);

		/// <summary>
		/// Whether the parser ids are a sentence of the grammar
		/// </summary>
		public bool Recognizes(IReadOnlyList<int> ids)
		{
			var sets = new List<HashSet<Item>>();
			for (var i = 0; i <= ids.Count; ++i) sets.Add(new HashSet<Item>());
			var queue = new Queue<Item>();
			for (var r = 0; r < _rules[Start].Count; ++r)
				sets[0].Add(new Item(Start, r, 0, 0));
			for (var i = 0; i <= ids.Count; ++i)
			{
				foreach (var it in sets[i]) queue.Enqueue(it);
				while (0 < queue.Count)
				{
					var it = queue.Dequeue();
					var rhs = _rules[it.Lhs][it.Rhs];
					void add(int set, Item x)
					{
						if (sets[set].Add(x) && set == i) queue.Enqueue(x);
					}
					if (it.Dot == rhs.Length)
					{
						// complete
						foreach (var w in sets[it.Origin].ToList())
						{
							var wr = _rules[w.Lhs][w.Rhs];
							if (w.Dot < wr.Length && wr[w.Dot] == it.Lhs)
								add(i, w with { Dot = w.Dot + 1 });
						}
						continue;
					}
					var next = rhs[it.Dot];
					if (_IsTerminal(next))
					{
						if (i < ids.Count && _TerminalId(next) == ids[i])
							add(i + 1, it with { Dot = it.Dot + 1 });
						continue;
					}
					// predict, and step over a nullable non-terminal right away (so completions at i aren't missed)
					for (var r = 0; r < _rules[next].Count; ++r)
						add(i, new Item(next, r, 0, i));
					if (_nullable.Contains(next))
						add(i, it with { Dot = it.Dot + 1 });
				}
			}
			return sets[ids.Count].Any(x => x.Lhs == Start && 0 == x.Origin && x.Dot == _rules[Start][x.Rhs].Length);
		}
		#endregion

		#region Sentences
		/// <summary>
		/// A random sentence, its derivation kept shallow enough to end
		/// </summary>
		public List<int> Sentence(Random r, int budget = 30)
		{
			var result = new List<int>();
			_Expand(Start, r, result, budget, 0);
			return result;
		}
		void _Expand(string sym, Random r, List<int> result, int budget, int depth)
		{
			if (_IsTerminal(sym))
			{
				result.Add(_TerminalId(sym));
				return;
			}
			var options = _rules[sym].Where(x => _Height(x) < Infinite).ToList();
			// out of budget: take the quickest way out
			var pick = budget <= result.Count + 4 || 40 < depth ? options.OrderBy(_Height).First() : options[r.Next(options.Count)];
			foreach (var x in pick)
				_Expand(x, r, result, budget, depth + 1);
		}
		#endregion
	}
}
