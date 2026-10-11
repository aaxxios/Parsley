using System;
using System.Collections.Generic;
using System.Linq;
using Atn = Parsley.Runtime.Atn;

namespace Parsley
{
	/// <summary>
	/// Works out how much lookahead each decision needs by simulating the grammar's network one token at a
	/// time from the decision, without regard to where its production was called from (strong LL, the
	/// same thing ALL(*)'s SLL pass does). Only the inputs on which two or more branches are still alive
	/// are followed further, and positions that reach the same configurations are shared, so the work
	/// grows with how much the branches overlap rather than with the number of terminals to the power
	/// of k, as whole-grammar FIRST_k/FOLLOW_k sets do.
	/// </summary>
	/// <remarks>
	/// The lookahead strings it produces for a branch are the shortest that pick it: a string stops at the
	/// first token where only one branch is left. A trie built from them makes the same choices, at the
	/// same tokens, as one built from the full k-token strings, and every token in a string has been
	/// checked by the time the branch is taken.
	/// </remarks>
	internal sealed class LookaheadExplorer
	{
		internal sealed class Node
		{
			public Node(Atn.ConfigSet configs, int[] alts)
			{
				Configs = configs;
				Alts = alts;
			}
			public readonly Atn.ConfigSet Configs;
			/// <summary>
			/// The branches still alive, ascending
			/// </summary>
			public readonly int[] Alts;
			public List<(int Symbol, Node Next)>? Edges;
		}

		/// <summary>
		/// What exploring one decision found
		/// </summary>
		internal sealed class Result
		{
			public bool Resolved;
			public int Depth;
			public List<HashSet<LookaheadString>> Branches = new();
			public List<LookaheadString> Conflicts = new();
			public long ConflictCount;
		}

		const int MaxExamples = 16;

		readonly GrammarAtn _grammarAtn;
		readonly Atn _atn;
		readonly int _eos;
		readonly Atn.StackPool _pool = new();
		readonly Dictionary<Atn.ConfigSet, Node> _nodes = new();
		readonly Dictionary<Expression, int> _index = new(ReferenceEqualityComparer.Instance);

		public LookaheadExplorer(GrammarAnalysis a, IReadOnlyList<Decision> decisions)
		{
			_grammarAtn = a.Atn;
			_eos = a.Symbols.EndOfInput;
			for (var i = 0; i < decisions.Count; ++i) _index[decisions[i].Node] = i;
			_atn = _grammarAtn.ToRuntime(decisions.Select(d => _grammarAtn.GetDecisionState(d.Node)).ToArray());
		}

		Node _Intern(Atn.ConfigSet configs)
		{
			if (_nodes.TryGetValue(configs, out var n)) return n;
			n = new Node(configs, configs.Items.Select(c => c.Alt).Distinct().OrderBy(x => x).ToArray());
			_nodes.Add(configs, n);
			return n;
		}

		// where each next token leads; empty when the input has ended (only end-of-input configurations are left)
		List<(int Symbol, Node Next)> _Edges(Node n)
		{
			if (null != n.Edges) return n.Edges;
			// the branches that can match each next token
			var symbols = new SortedDictionary<int, SortedSet<int>>();
			void add(int t, int alt)
			{
				if (!symbols.TryGetValue(t, out var alts)) symbols.Add(t, alts = new SortedSet<int>());
				alts.Add(alt);
			}
			foreach (var c in n.Configs.Items)
			{
				if (Atn.Accept == c.State) add(_eos, c.Alt);
				else if (Atn.Final != c.State)
					foreach (var t in _grammarAtn.MatchSymbols(c.State)) add(t, c.Alt);
			}
			var edges = new List<(int, Node)>();
			foreach (var (t, alts) in symbols)
			{
				var reach = _atn.Move(n.Configs, t, false);
				if (0 < reach.Count) edges.Add((t, _Intern(reach)));
				// the token matched, but nothing can follow (in a rule that's never called): the input ends
				// there as far as these branches go, as it would at end of input
				else edges.Add((t, new Node(reach, alts.ToArray()) { Edges = new List<(int, Node)>() }));
			}
			n.Edges = edges;
			return edges;
		}

		/// <summary>
		/// Explores a decision.
		/// </summary>
		/// <param name="winner">For input that still matches several branches at maxK: which of them takes it.
		/// Null to leave such a decision unresolved (for ALL(*) to predict at parse time).</param>
		public Result Explore(Decision d, int branchCount, int maxK, Func<IReadOnlyList<int>, int>? winner)
		{
			var result = new Result();
			for (var i = 0; i < branchCount; ++i) result.Branches.Add(new HashSet<LookaheadString>());
			var start = _Intern(_atn.StartSet(_index[d.Node], Atn.Stack.Empty, _pool, false));
			var conflictMemo = new Dictionary<(Node, int), bool>();
			for (var k = 1; k <= maxK; ++k)
			{
				if (_ConflictAt(start, k, conflictMemo)) continue;
				result.Resolved = true;
				result.Depth = k;
				_Enumerate(start, new List<int>(), n => 1 == n.Alts.Length ? n.Alts[0] : -1, result.Branches);
				return result;
			}
			result.ConflictCount = _CountConflicts(start, maxK, new Dictionary<(Node, int), long>());
			_Examples(start, new List<int>(), maxK, result.Conflicts);
			if (null == winner) return result;

			// settle every input still ambiguous at maxK with the winner, then find the shallowest depth
			// at which each prefix leads to one branch
			var assignedMemo = new Dictionary<(Node, int), int[]>();
			int[] assigned(Node n, int remaining)
			{
				if (assignedMemo.TryGetValue((n, remaining), out var r)) return r;
				var edges = _Edges(n);
				if (1 == n.Alts.Length) r = n.Alts;
				else if (0 == remaining || 0 == edges.Count) r = new[] { winner(n.Alts) };
				else r = edges.SelectMany(e => assigned(e.Next, remaining - 1)).Distinct().OrderBy(x => x).ToArray();
				assignedMemo[(n, remaining)] = r;
				return r;
			}
			for (var k = 1; k <= maxK; ++k)
			{
				var okMemo = new Dictionary<(Node, int), bool>();
				bool ok(Node n, int consumed)
				{
					if (okMemo.TryGetValue((n, consumed), out var v)) return v;
					if (1 >= assigned(n, maxK - consumed).Length) v = true;
					else if (consumed == k) v = false;
					else v = _Edges(n).All(e => ok(e.Next, consumed + 1));
					okMemo[(n, consumed)] = v;
					return v;
				}
				if (!ok(start, 0)) continue;
				result.Depth = k;
				// the remaining depth of a node isn't part of the node, so this enumeration tracks it
				_EnumerateAssigned(start, new List<int>(), maxK, assigned, result.Branches);
				break;
			}
			return result;
		}

		// whether some input of exactly `remaining` more tokens (or ending sooner, at end of input) still
		// leaves more than one branch
		bool _ConflictAt(Node n, int remaining, Dictionary<(Node, int), bool> memo)
		{
			if (2 > n.Alts.Length) return false;
			if (memo.TryGetValue((n, remaining), out var v)) return v;
			var edges = _Edges(n);
			v = 0 == remaining || 0 == edges.Count || edges.Any(e => _ConflictAt(e.Next, remaining - 1, memo));
			memo[(n, remaining)] = v;
			return v;
		}

		long _CountConflicts(Node n, int remaining, Dictionary<(Node, int), long> memo)
		{
			if (2 > n.Alts.Length) return 0;
			if (memo.TryGetValue((n, remaining), out var v)) return v;
			var edges = _Edges(n);
			if (0 == remaining || 0 == edges.Count) v = 1;
			else
			{
				v = 0;
				foreach (var e in edges)
					v = Math.Min(long.MaxValue / 2, v + _CountConflicts(e.Next, remaining - 1, memo));
			}
			memo[(n, remaining)] = v;
			return v;
		}

		void _Examples(Node n, List<int> prefix, int remaining, List<LookaheadString> into)
		{
			if (MaxExamples <= into.Count || 2 > n.Alts.Length) return;
			var edges = _Edges(n);
			if (0 == remaining || 0 == edges.Count)
			{
				into.Add(new LookaheadString(prefix.ToArray()));
				return;
			}
			foreach (var (t, next) in edges)
			{
				prefix.Add(t);
				_Examples(next, prefix, remaining - 1, into);
				prefix.RemoveAt(prefix.Count - 1);
			}
		}

		// the shortest strings that pick each branch. Always at least one token, even if only one branch can
		// start here, so a branch's strings name the tokens it starts with.
		void _Enumerate(Node n, List<int> prefix, Func<Node, int> single, List<HashSet<LookaheadString>> into)
		{
			var alt = single(n);
			if (-1 < alt && 0 < prefix.Count)
			{
				into[alt].Add(new LookaheadString(prefix.ToArray()));
				return;
			}
			if (0 == n.Alts.Length) return;
			foreach (var (t, next) in _Edges(n))
			{
				prefix.Add(t);
				_Enumerate(next, prefix, single, into);
				prefix.RemoveAt(prefix.Count - 1);
			}
		}

		void _EnumerateAssigned(Node n, List<int> prefix, int maxK, Func<Node, int, int[]> assigned, List<HashSet<LookaheadString>> into)
		{
			var a = assigned(n, maxK - prefix.Count);
			if (1 == a.Length && 0 < prefix.Count)
			{
				into[a[0]].Add(new LookaheadString(prefix.ToArray()));
				return;
			}
			if (0 == a.Length) return;
			foreach (var (t, next) in _Edges(n))
			{
				prefix.Add(t);
				_EnumerateAssigned(next, prefix, maxK, assigned, into);
				prefix.RemoveAt(prefix.Count - 1);
			}
		}
	}
}
