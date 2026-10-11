using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Parsley.Tests
{
	public static class AnalysisTests
	{
		static GrammarAnalysis A(string xbnf, int maxK = 4) => GrammarAnalysis.Analyze(Grammar.Parse(xbnf), maxK);
		// LL(k) only, as Parsley was before ALL(*): what can't be decided in k tokens is settled by policy
		static GrammarAnalysis LLk(string xbnf, int maxK = 4) => GrammarAnalysis.Analyze(Grammar.Parse(xbnf), ParserEngine.LLk, maxK);
		static string Errors(GrammarAnalysis a) => string.Join("; ", a.Messages.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message));

		public static void JsonIsAllOneToken()
		{
			var a = GrammarAnalysis.Analyze(Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf")));
			T.Equal("", Errors(a), "no errors");
			T.Equal(1, a.Depth, "json needs one token everywhere");
			var obj = a.Grammar.GetProduction("Object")!;
			var opt = (OptionalExpression)((Sequence)obj.Expression).Items[1];
			var d = a.GetDecision(opt)!;
			T.Equal(DecisionKind.Optional, d.Kind, "optional decision");
			T.Equal("string", string.Join(",", d.TokensOf(0).Select(a.Symbols.GetName)), "optional starts with string");
			T.Equal(1, d.DefaultBranch, "tests the take side: 1 token vs 2");
			var loop = a.Decisions.First(x => x.Production.Name == "Object" && x.Kind == DecisionKind.Loop);
			T.Equal("comma", string.Join(",", loop.TokensOf(0).Select(a.Symbols.GetName)), "loop continues on comma");
			T.Equal(1, loop.DefaultBranch, "comma loop: tie, but } is checked right after, so test the comma");
			var value = a.Decisions.First(x => x.Production.Name == "Value");
			T.Equal(-1, value.DefaultBranch, "6 options: switch with an error default");
		}
		public static void FixedLookahead()
		{
			var a = A("P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";");
			T.Equal("", Errors(a), "no errors");
			T.Equal(1, a.Decisions.First(d => d.Production.Name == "P").Depth, "P needs 1");
			T.Equal(3, a.Decisions.First(d => d.Production.Name == "S").Depth, "S needs 3");
		}
		public static void UsesFollowContext()
		{
			// the optional's exit depends on what follows it: [a #EOS] skips, [a a] takes
			var a = A("S= A a; A= b [ a ]; a=\"a\"; b=\"b\";");
			var d = a.Decisions.Single();
			T.Equal("", Errors(a), "no errors");
			T.Equal(2, d.Depth, "two tokens separate take from skip");
			T.Equal(null, d.AppliedPolicy, "no policy needed");
		}
		public static void LoopVersusExitNeedsTwoTokens()
		{
			// { a b } a c : continue on [a b], exit on [a c]
			var a = A("S= { a b } a c; a=\"a\"; b=\"b\"; c=\"c\";");
			T.Equal("", Errors(a), "no errors");
			var d = a.Decisions.Single();
			T.Equal(2, d.Depth, "loop needs 2");
		}
		public static void InversionPrefersSmallerSide()
		{
			// a block of statements: many ways to continue, one way out
			var a = A("Block= \"{\" { Stmt } \"}\"; Stmt= a \";\" | b \";\" | c \";\" | d \";\"; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\";");
			var loop = a.Decisions.First(x => x.Kind == DecisionKind.Loop);
			T.Equal(0, loop.DefaultBranch, "4 to continue vs } and EOS to exit: test the exit");
		}
		// the lookahead strings of each branch, the way the analysis used to compute them: FIRST_k of the
		// branch followed by FOLLOW_k of the decision, over the whole grammar
		static List<HashSet<LookaheadString>> _SetBranches(GrammarAnalysis a, Decision d, int k)
		{
			var eos = a.Symbols.EndOfInput;
			HashSet<LookaheadString> concat(IEnumerable<LookaheadString> lhs, IEnumerable<LookaheadString> rhs)
			{
				var result = new HashSet<LookaheadString>();
				foreach (var x in lhs)
				{
					if (x.IsComplete(k, eos)) { result.Add(x.Truncate(k)); continue; }
					foreach (var y in rhs) result.Add(x.Concat(y, k, eos));
				}
				return result;
			}
			var follow = a.GetFollow(d.Node, k);
			switch (d.Node)
			{
				case Alternation alt:
					return alt.Options.Select(o => concat(a.GetFirst(o, k), follow)).ToList();
				case OptionalExpression o:
					return new List<HashSet<LookaheadString>> { concat(a.GetFirst(o.Body, k), follow), new HashSet<LookaheadString>(follow) };
				default:
					var r = (Repeat)d.Node;
					return new List<HashSet<LookaheadString>> { concat(a.GetFirst(r.Body, k), a.GetFollow(r.Body, k)), new HashSet<LookaheadString>(follow) };
			}
		}

		/// <summary>
		/// The lookahead analysis simulates the network instead of building whole-grammar FIRST_k and FOLLOW_k
		/// sets. On random grammars, each decision must come out exactly as the sets say: the same depth, every
		/// set string chosen the same way by the new strings, and adaptive exactly where the sets conflict at k.
		/// </summary>
		public static void LookaheadMatchesFirstAndFollowSets()
		{
			var r = new Random(77);
			int checkedDecisions = 0, deep = 0, unresolved = 0;
			var failures = new List<string>();
			for (var n = 0; n < 800; ++n)
			{
				var xbnf = EngineTests.RandomGrammar(r);
				GrammarAnalysis a;
				try
				{
					// a rule nobody calls still adds to FOLLOW sets, though nothing can follow there in a parse,
					// so the sets and the simulation disagree about it; leave such rules out
					var g = Grammar.Parse(xbnf, "la.xbnf");
					var reachable = new HashSet<string> { g.StartSymbol! };
					for (var grew = true; grew;)
					{
						grew = false;
						foreach (var p in g.Productions.Where(p => reachable.Contains(p.Name)).ToList())
							foreach (var sr in p.Expression.Descendants().OfType<SymbolRef>())
								if (null != sr.Name && null != g.GetProduction(sr.Name) && reachable.Add(sr.Name)) grew = true;
					}
					g.Productions.RemoveAll(p => !reachable.Contains(p.Name));
					a = GrammarAnalysis.Analyze(g, 3);
				}
				catch (GrammarException) { continue; }
				// nor does a rule that can never finish have meaningful lookahead
				if (a.HasErrors || a.Messages.Any(m => m.Message.Contains("can't match any finite input"))) continue;
				// rewriting left recursion can inline a rule into its only caller, leaving it unreachable
				var live = new HashSet<string> { a.Grammar.StartSymbol! };
				for (var grew = true; grew;)
				{
					grew = false;
					foreach (var p in a.Grammar.Productions.Where(p => live.Contains(p.Name)).ToList())
						foreach (var sr in p.Expression.Descendants().OfType<SymbolRef>())
							if (null != sr.Name && null != a.Grammar.GetProduction(sr.Name) && live.Add(sr.Name)) grew = true;
				}
				if (a.Grammar.Productions.Any(p => !live.Contains(p.Name))) continue;
				foreach (var d in a.Decisions)
				{
					++checkedDecisions;
					if (d.IsAdaptive && 0 == d.Depth)
					{
						++unresolved;
						var sets = _SetBranches(a, d, 3);
						var seen = new Dictionary<LookaheadString, int>();
						var conflict = false;
						for (var i = 0; i < sets.Count && !conflict; ++i)
							foreach (var x in sets[i])
								if (seen.TryGetValue(x, out var j) && j != i) { conflict = true; break; }
								else seen[x] = i;
						if (!conflict) failures.Add($"{d} is adaptive but the sets don't conflict at 3 in\n{xbnf}");
						continue;
					}
					if (1 < d.Depth) ++deep;
					// the shallowest k at which the sets are disjoint
					var k = 1;
					List<HashSet<LookaheadString>> b;
					while (true)
					{
						b = _SetBranches(a, d, k);
						var owner = new Dictionary<LookaheadString, int>();
						var ok = true;
						for (var i = 0; i < b.Count && ok; ++i)
							foreach (var x in b[i])
								if (owner.TryGetValue(x, out var j) && j != i) { ok = false; break; }
								else owner[x] = i;
						if (ok || 3 == k) break;
						++k;
					}
					if (k != d.Depth) { failures.Add($"{d}: sets say depth {k} in\n{xbnf}"); continue; }
					for (var i = 0; i < b.Count; ++i)
						foreach (var x in b[i])
						{
							var picks = Enumerable.Range(0, d.Branches.Count).Where(j => d.Branches[j].Any(p => x.StartsWith(p))).ToList();
							if (1 != picks.Count || picks[0] != i)
								failures.Add($"{d}: [{x.ToString(a.Symbols)}] is branch {i} but the new strings pick {string.Join(",", picks)} in\n{xbnf}");
						}
				}
			}
			T.Check(1000 < checkedDecisions && 50 < deep && 20 < unresolved, $"enough decisions ({checkedDecisions}, {deep} deeper than one token, {unresolved} unresolved)");
			T.Equal("", string.Join("\n----\n", failures.Take(3)), $"{failures.Count} decisions differ");
		}

		public static void MaxKDirective()
		{
			const string body = "S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\";";
			var dflt = GrammarAnalysis.Analyze(Grammar.Parse(body));
			T.Equal(GrammarAnalysis.DefaultMaxK, dflt.MaxK, "defaults to 4");
			T.Equal(ParserEngine.LLk, dflt.Engine, "three tokens is LL(k)");
			var two = GrammarAnalysis.Analyze(Grammar.Parse("@maxk 2;" + body));
			T.Equal(2, two.MaxK, "@maxk 2 is read bare");
			T.Equal(ParserEngine.AllStar, two.Engine, "past the limit, ALL(*) decides");
			T.Equal(3, GrammarAnalysis.Analyze(Grammar.Parse("@maxk \"3\";" + body)).MaxK, "or quoted, as the writer writes it");
			T.Equal(4, GrammarAnalysis.Analyze(Grammar.Parse("@maxk 2;" + body), 4).MaxK, "an explicit maxK wins over the directive");
			T.Equal(1, GrammarAnalysis.Analyze(Grammar.Parse("@maxk 3;" + body), ParserEngine.LL1).MaxK, "LL(1) is one token, whatever @maxk says");
			var g = Grammar.Parse("@maxk 2;" + body);
			T.Equal(2, Grammar.Parse(g.ToString()).MaxK, "survives writing and reading back");
			foreach (var bad in new[] { "@maxk 0;", "@maxk \"x\";", "@maxk \"-1\";" })
			{
				var msgs = XbnfReader.TryParse(bad + body, null, out _);
				T.Check(msgs.Any(m => m.Level == ErrorLevel.Error && m.Message.Contains("@maxk expects")), bad + " is an error");
			}
		}

		public static void AlternationConflictIsAnErrorOnLLk()
		{
			var a = LLk("S= A | B; A= { a } x; B= { a } y; a=\"a\"; x=\"x\"; y=\"y\";", 3);
			T.Check(Errors(a).Contains("can't be told apart"), "unresolved alternation reported");
			var b = LLk("S= ( A | B )<policy=first>; A= { a } x; B= { a } y; a=\"a\"; x=\"x\"; y=\"y\";", 3);
			T.Equal("", Errors(b), "explicit policy accepted");
			var auto = A("S= A | B; A= { a } x; B= { a } y; a=\"a\"; x=\"x\"; y=\"y\";", 3);
			T.Equal("", Errors(auto), "left to ALL(*) when the engine is chosen automatically");
			T.Check(auto.Decisions.Single(d => d.Kind == DecisionKind.Alternation).IsAdaptive, "adaptive");
		}
		public static void DanglingElseIsGreedy()
		{
			var auto = A("S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";");
			var adaptive = auto.Decisions.First(d => d.Kind == DecisionKind.Optional);
			T.Check(adaptive.IsAdaptive && null == adaptive.AppliedPolicy, "without a policy, ALL(*) decides it at parse time");
			var a = LLk("S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";");
			T.Equal("", Errors(a), "no errors");
			var opt = a.Decisions.First(d => d.Kind == DecisionKind.Optional);
			T.Equal("greedy", opt.AppliedPolicy, "greedy");
			T.Equal(1, opt.Depth, "depth 1");
			T.Equal("\"else\"", string.Join(",", opt.TokensOf(0).Select(a.Symbols.Display)), "else always taken");
			var lazy = A("S= \"if\" e \"then\" S [ \"else\" S ]<policy=lazy> | s; e=\"e\"; s=\"s\";");
			T.Equal(0, lazy.Decisions.First(d => d.Kind == DecisionKind.Optional).Branches[0].Count, "lazy never takes else");
			T.Check(!lazy.Messages.Any(m => m.Level == ErrorLevel.Warning && m.Message.Contains("ambiguous")), "explicit policy is quiet");
		}
		public static void DirectLeftRecursionBecomesAFoldingLoop()
		{
			var a = A("E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';");
			T.Equal("", Errors(a), "no errors");
			var e = a.Grammar.GetProduction("E")!;
			T.Equal("T { \"+\" T | \"-\" T }", e.Expression.ToString(), "E rewritten");
			T.Check(((Repeat)((Sequence)e.Expression).Items[1]).LeftFold, "loop folds left");
			T.Equal(1, a.Depth, "expression grammar is one token after rewriting");
		}
		public static void IndirectLeftRecursion()
		{
			var a = A("A= B x | y; B= A z | w; x=\"x\"; y=\"y\"; z=\"z\"; w=\"w\";");
			T.Equal("", Errors(a), "no errors");
			T.Check(a.Messages.Any(m => m.Message.Contains("Inlined \"B\" into \"A\"")), "inlining reported");
			T.Equal("( w x | y ) { z x }", a.Grammar.GetProduction("A")!.Expression.ToString(), "A rewritten");
		}
		public static void HiddenLeftRecursionIsReported()
		{
			var a = A("A= N A x | y; N= [ n ]; n=\"n\"; x=\"x\"; y=\"y\";");
			T.Check(Errors(a).Contains("nullable prefix"), "hidden left recursion reported");
		}
		public static void NullableLoopBodyIsAnError()
		{
			var a = A("S= { [ a ] } b; a=\"a\"; b=\"b\";");
			T.Check(Errors(a).Contains("can match nothing"), "nullable loop body");
		}
		public static void InputGrammarIsUnchanged()
		{
			var g = Grammar.Parse("E= E \"+\" n | n; n='[0-9]+';");
			var before = g.ToString();
			GrammarAnalysis.Analyze(g);
			T.Equal(before, g.ToString(), "analysis works on a copy");
		}
	}
}
