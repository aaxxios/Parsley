using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parsley.Runtime;

namespace Parsley.Tests
{
	/// <summary>
	/// Engine selection, and that LL(1), LL(k) and ALL(*) parse the same way wherever they all can
	/// </summary>
	public static class EngineTests
	{
		const string Ws = " ws<hidden>= '[ \\t\\r\\n]+';";

		sealed class Built
		{
			public GrammarAnalysis Analysis = null!;
			public CompiledParser Parser = null!;
			public RegexLexer Lexer = null!;
			public string Source = "";
			public (string Tree, List<string> Errors) Parse(string text) => Parser.Parse(Lexer.Tokenize(text).ToList());
			public bool Accepts(string text) => 0 == Parse(text).Errors.Count;
		}

		static GrammarAnalysis _Analyze(string xbnf, ParserEngine engine = ParserEngine.Auto, string name = "g")
			=> GrammarAnalysis.Analyze(Grammar.Parse(xbnf, name + ".xbnf"), engine);

		static Built _Build(string name, string xbnf, ParserEngine engine = ParserEngine.Auto)
		{
			var a = _Analyze(xbnf, engine, name);
			GrammarException.ThrowIfErrors(a.Messages);
			var src = CSharpSynthesizer.Synthesize(a);
			File.WriteAllText(Path.Combine(SynthesisTests.OutDir, $"{a.Grammar.ClassName}.{a.Engine}.cs"), src);
			var p = CompiledParser.Compile(src, a.Grammar.ClassName!);
			T.Equal("", string.Join("\n", p.Diagnostics), $"{name} on {a.Engine} compiles without warnings");
			return new Built { Analysis = a, Parser = p, Lexer = new RegexLexer(a), Source = src };
		}

		static string Errors(GrammarAnalysis a) => string.Join("; ", a.Messages.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message));

		const string Json = "json.xbnf";
		const string Fixed = "P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";";
		const string Unbounded = "S= A x | A y; A= a A | b; a=\"a\"; b=\"b\"; x=\"x\"; y=\"y\";";
		const string UnboundedLoop = "S= { X semi } X dot; X= a X | b; a=\"a\"; b=\"b\"; semi=\";\"; dot=\".\";";
		const string NeedsCallStack = "S= x A a | y A; A= b [ a ]; a=\"a\"; b=\"b\"; x=\"x\"; y=\"y\";";
		const string DanglingElse = "S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";";
		const string DanglingElseGreedy = "S= \"if\" e \"then\" S [ \"else\" S ]<policy=greedy> | s; e=\"e\"; s=\"s\";";
		const string Expr = "E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';";

		public static void ChoosesTheLightestEngine()
		{
			var json = GrammarAnalysis.Analyze(Grammar.ReadFrom(Path.Combine(T.Dir, Json)));
			T.Equal(ParserEngine.LL1, json.Engine, "json is LL(1)");
			T.Equal(ParserEngine.LL1, _Analyze(Expr).Engine, "left recursion rewritten into LL(1)");
			var fixedK = _Analyze(Fixed);
			T.Equal(ParserEngine.LLk, fixedK.Engine, "three tokens is LL(k)");
			T.Equal(3, fixedK.Depth, "k = 3");
			var unbounded = _Analyze(Unbounded);
			T.Equal("", Errors(unbounded), "no fixed k is not an error when ALL(*) can do it");
			T.Equal(ParserEngine.AllStar, unbounded.Engine, "unbounded lookahead is ALL(*)");
			var d = unbounded.Decisions.Single(x => x.Production.Name == "S");
			T.Check(d.IsAdaptive && 0 == d.Depth && 0 == d.AdaptiveIndex, "the decision is adaptive");
			T.Check(unbounded.Messages.Any(m => m.Level == ErrorLevel.Message && m.Message.Contains("predicted adaptively")), "and says so");
			T.Equal(ParserEngine.LL1, _Analyze(DanglingElseGreedy).Engine, "a policy keeps the dangling else LL(1)");
			var dangling = _Analyze(DanglingElse);
			T.Equal(ParserEngine.AllStar, dangling.Engine, "without one, ALL(*) decides it");
			T.Check(!dangling.Messages.Any(m => m.Level != ErrorLevel.Message), "with no warning");
			T.Check(dangling.Messages.Any(m => m.Message.Contains("a <policy> settles it")), "but a hint that a policy would do");
		}

		public static void ForcedEngines()
		{
			var g = Grammar.ReadFrom(Path.Combine(T.Dir, Json));
			var llk = GrammarAnalysis.Analyze(g, ParserEngine.LLk);
			T.Equal(ParserEngine.LLk, llk.Engine, "LL(k) when asked for");
			T.Check(CSharpSynthesizer.Synthesize(llk).Contains("partial class JsonParser : LLkParserBase"), "derives from LLkParserBase");
			var all = GrammarAnalysis.Analyze(g, ParserEngine.AllStar);
			var src = CSharpSynthesizer.Synthesize(all);
			T.Check(src.Contains("partial class JsonParser : AllStarParserBase") && src.Contains("static readonly Atn s_atn"), "an ALL(*) parser carries its network");
			T.Equal(0, all.AdaptiveDecisions.Count, "one-token decisions stay switches");
			T.Check(src.Contains("Enter(Json, returnState);") && src.Contains("parser.Run(() => parser.ParseJson(-1))"), "productions know where they return to");
			var ll1 = CSharpSynthesizer.Synthesize(GrammarAnalysis.Analyze(g));
			T.Check(ll1.Contains(": LL1ParserBase") && !ll1.Contains("Peek(") && !ll1.Contains("returnState"), "LL(1) needs no lookahead and no return states");

			T.Check(Errors(_Analyze(Fixed, ParserEngine.LL1)).Contains("can't be told apart within 1 tokens"), "LL(1) can't do a three-token decision");
			T.Check(Errors(_Analyze(Unbounded, ParserEngine.LLk)).Contains("can't be told apart within 4 tokens"), "LL(k) can't do unbounded lookahead");
			var loop = _Analyze(UnboundedLoop, ParserEngine.LLk);
			T.Check(loop.Messages.Any(m => m.Level == ErrorLevel.Warning && m.Message.Contains("resolved greedily")), "LL(k) settles a loop greedily, as before");
			var fixedAll = _Analyze(Fixed, ParserEngine.AllStar);
			T.Equal(1, fixedAll.AdaptiveDecisions.Count, "on ALL(*), a decision needing more than one token is adaptive");
			T.Equal(3, fixedAll.AdaptiveDecisions[0].Depth, "and keeps its depth");
			var fixedSrc = CSharpSynthesizer.Synthesize(fixedAll);
			T.Check(fixedSrc.Contains("switch (AdaptivePredict(0))") && !fixedSrc.Contains("PredictS1"), "predicted adaptively instead of by a synthesized predictor");
			var policy = _Analyze(DanglingElseGreedy, ParserEngine.AllStar);
			T.Equal(0, policy.AdaptiveDecisions.Count, "a decision with a policy is settled statically on every engine");
		}

		public static void BaseSources()
		{
			T.Equal("LL1ParserBase.cs", CSharpSynthesizer.BaseFileName(ParserEngine.LL1), "LL(1) file");
			T.Equal("LLkParserBase.cs", CSharpSynthesizer.BaseFileName(ParserEngine.LLk), "LL(k) file");
			T.Equal("AllStarParserBase.cs", CSharpSynthesizer.BaseFileName(ParserEngine.AllStar), "ALL(*) file");
			T.Throws<ArgumentException>(() => CSharpSynthesizer.BaseFileName(ParserEngine.Auto), "Auto has no base");
			foreach (var e in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
				T.Check(CSharpSynthesizer.BaseSource(e).Contains($"public abstract class {e.BaseClassName()} : ParserBase"), e + " base derives from the shared ParserBase");
			var rt = CSharpSynthesizer.RuntimeSource;
			T.Check(rt.Contains("public abstract class ParserBase") && !rt.Contains("class LL1ParserBase") && !rt.Contains("class AllStarParserBase"), "the shared runtime holds no engine");
		}

		// one grammar, every engine that can take it without a policy: same trees, same errors
		static void _Agree(string name, string xbnf, string[] words, int count, int seed, params ParserEngine[] engines)
		{
			var built = engines.Select(e => _Build(name, xbnf + Ws, e)).ToList();
			for (var i = 0; i < built.Count; ++i)
				T.Equal(engines[i], built[i].Analysis.Engine, $"{name} built for {engines[i]}");
			var r = new Random(seed);
			var mismatches = new List<string>();
			for (var i = 0; i < count; ++i)
			{
				var text = string.Join(" ", Enumerable.Range(0, r.Next(14)).Select(_ => words[r.Next(words.Length)]));
				var tokens = built[0].Lexer.Tokenize(text).ToList();
				var first = built[0].Parser.Parse(tokens);
				for (var j = 1; j < built.Count; ++j)
				{
					var other = built[j].Parser.Parse(tokens);
					if (first.Tree != other.Tree || !first.Errors.SequenceEqual(other.Errors))
						mismatches.Add($"{engines[j]}: {text}");
				}
				var interp = Interp.Parse(built[^1].Analysis, tokens);
				if (first.Tree != interp.Tree || !first.Errors.SequenceEqual(interp.Errors))
					mismatches.Add($"interpreter: {text}");
			}
			T.Equal("", string.Join(" | ", mismatches.Take(5)), $"{name}: every engine agrees on trees and errors");
		}

		public static void EnginesAgreeOnLL1Grammars()
		{
			_Agree("agreeExpr", Expr, new[] { "1", "+", "-", "*", "(", ")" }, 300, 5, ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar);
			_Agree("agreeBlock", "Block= \"{\" { Stmt }<sync=\"semi\"> \"}\"; Stmt= a \"=\" n semi | b \"(\" \")\" semi | c semi; a=\"a\"; b=\"b\"; c=\"c\"; n='[0-9]+'; semi= \";\";",
				new[] { "{", "}", "a", "b", "c", "=", "1", "(", ")", ";" }, 300, 6, ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar);
			_Agree("agreeElse", DanglingElseGreedy, new[] { "if", "e", "then", "else", "s" }, 300, 7, ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar);
		}

		public static void EnginesAgreeOnLLkGrammars()
		{
			_Agree("agreeFixed", "P= S x | y; S= a b c | a b d | { a } e; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; e=\"e\"; x=\"x\"; y=\"y\";",
				new[] { "a", "b", "c", "d", "e", "x", "y" }, 400, 8, ParserEngine.LLk, ParserEngine.AllStar);
			_Agree("agreeLoop", "S= { a b } a c; a=\"a\"; b=\"b\"; c=\"c\";", new[] { "a", "b", "c" }, 300, 9, ParserEngine.LLk, ParserEngine.AllStar);
			_Agree("agreeFollow", "S= A a; A= b [ a ]; a=\"a\"; b=\"b\";", new[] { "a", "b" }, 300, 10, ParserEngine.LLk, ParserEngine.AllStar);
		}

		public static void EnginesAgreeOnJson()
		{
			var xbnf = File.ReadAllText(Path.Combine(T.Dir, Json));
			var built = new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar }.Select(e => _Build("agreeJson", xbnf, e)).ToList();
			var mismatches = new List<string>();
			foreach (var text in SynthesisTests._samples.Concat(SynthesisTests._RandomDocuments(300, 13)))
			{
				var tokens = built[0].Lexer.Tokenize(text).ToList();
				var first = built[0].Parser.Parse(tokens);
				foreach (var b in built.Skip(1))
				{
					var other = b.Parser.Parse(tokens);
					if (first.Tree != other.Tree || !first.Errors.SequenceEqual(other.Errors))
						mismatches.Add($"{b.Analysis.Engine}: {text}");
				}
			}
			T.Equal("", string.Join(" | ", mismatches.Take(5)), "json: every engine agrees");
		}

		public static void EvaluatorIsTheSameOnEveryEngine()
		{
			var vars = new Dictionary<string, int> { ["x"] = 5 };
			foreach (var e in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
			{
				var a = GrammarAnalysis.Analyze(Grammar.Parse(EvaluatorTests.Calc, "calc.xbnf"), e);
				GrammarException.ThrowIfErrors(a.Messages);
				var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(a), "CalcParser");
				T.Equal("", string.Join("\n", p.Diagnostics), $"calculator on {e} compiles cleanly");
				var lexer = new RegexLexer(a);
				T.Equal((object)7, p.Evaluate("CalcEvaluator", lexer.Tokenize("1 + 2 * 3"), vars), $"{e}: precedence");
				T.Equal((object)9, p.Evaluate("CalcEvaluator", lexer.Tokenize("(1 + 2) * 3"), vars), $"{e}: parentheses");
				T.Equal((object)26, p.Evaluate("CalcEvaluator", lexer.Tokenize("x * x + 1"), vars), $"{e}: state");
				T.Equal((object)6, p.Evaluate("CalcEvaluator", lexer.Tokenize("1 * 2 + 3 * x * 0 + 4 + 0 * 9"), vars), $"{e}: a longer one");
			}
			// an evaluator over a grammar that needs ALL(*)
			const string adaptive = @"
				@evaluator ""PickEvaluator"";
				S<type=""string"">= A x | A y => {
					var a = EvaluateA(node.Children[0]);
					return a + (X == node.Children[1].SymbolId ? ""x"" : ""y"");
				};
				A<type=""string"">= a A | b => {
					if (1 == node.Children.Count) return ""b"";
					return ""a"" + EvaluateA(node.Children[1]);
				};
				a=""a""; b=""b""; x=""x""; y=""y"";" + Ws;
			var ga = GrammarAnalysis.Analyze(Grammar.Parse(adaptive, "pick.xbnf"));
			T.Equal(ParserEngine.AllStar, ga.Engine, "the evaluator's grammar needs ALL(*)");
			var pa = CompiledParser.Compile(CSharpSynthesizer.Synthesize(ga), "PickParser");
			T.Equal((object)"aaby", pa.Evaluate("PickEvaluator", new RegexLexer(ga).Tokenize("a a b y")), "evaluated on the ALL(*) tree");
		}

		public static void AllStarLooksAsFarAsItHasTo()
		{
			var h = _Build("unbounded", Unbounded + Ws);
			T.Check(h.Source.Contains("switch (AdaptivePredict(0))"), "adaptive switch");
			T.Check(h.Source.Contains("ALL(*) looks as far as it has to"), "explained in a comment");
			foreach (var text in new[] { "b x", "b y", "a b x", "a a a a a a a a a b y", "a a a a a a a a a b x" })
				T.Check(h.Accepts(text), "accepts " + text);
			foreach (var text in new[] { "a a b", "b", "x", "a a b z", "a a b x y" })
				T.Check(!h.Accepts(text), "rejects " + text);
			T.Check(h.Parse("a a a b y").Tree.Contains("y \"y\""), "took the y alternative");
			var loop = _Build("unboundedLoop", UnboundedLoop + Ws);
			foreach (var text in new[] { "b .", "a b .", "b ; a a b .", "a a a b ; a b ; b ; a a a a b ." })
				T.Check(loop.Accepts(text), "loop accepts " + text);
			foreach (var text in new[] { "b ;", "b ; .", ".", "a a a ." })
				T.Check(!loop.Accepts(text), "loop rejects " + text);
		}

		public static void AllStarUsesTheCallStack()
		{
			// strong LL(k) can't do this for any k: after b, [a #EOS] means take the a under y but skip it
			// under x. SLL sees the conflict; the full-context retry settles it with the parser's call stack.
			var h = _Build("callstack", NeedsCallStack + Ws);
			T.Equal(ParserEngine.AllStar, h.Analysis.Engine, "needs ALL(*)");
			foreach (var text in new[] { "x b a", "x b a a", "y b", "y b a" })
				T.Check(h.Accepts(text), "accepts " + text);
			foreach (var text in new[] { "x b", "y b a a", "x b a a a" })
				T.Check(!h.Accepts(text), "rejects " + text);
			var lines = h.Parse("y b a").Tree.Split('\n');
			T.Check(lines.Any(l => l.StartsWith("    a \"a\"")), "under y, the a belongs to A");
			lines = h.Parse("x b a").Tree.Split('\n');
			T.Check(lines.Any(l => l.StartsWith("  a \"a\"")), "under x, the a belongs to S");
		}

		public static void CollapsedRulesDontChangePrediction()
		{
			// collapsing only changes the tree: the rule is still a call and return in the network, so
			// lookahead that runs through it or out of it predicts exactly as it does uncollapsed
			var plain = _Build("collapseOff", NeedsCallStack + Ws);
			var collapsed = _Build("collapseOn", NeedsCallStack.Replace("A= b", "A<collapsed>= b") + Ws);
			var loopPlain = _Build("collapseLoopOff", UnboundedLoop + Ws);
			var loopCollapsed = _Build("collapseLoopOn", UnboundedLoop.Replace("X= a X", "X<collapsed>= a X") + Ws);
			T.Equal(ParserEngine.AllStar, collapsed.Analysis.Engine, "still ALL(*)");
			T.Check(collapsed.Source.Contains("void ParseA(ParseNode parent, int returnState)"), "a collapsed production carries its return state too");
			var r = new Random(31);
			var mismatches = new List<string>();
			foreach (var (on, off, words) in new[] { (collapsed, plain, new[] { "x", "y", "a", "b" }), (loopCollapsed, loopPlain, new[] { "a", "b", ";", "." }) })
				for (var i = 0; i < 400; ++i)
				{
					var text = string.Join(" ", Enumerable.Range(0, r.Next(12)).Select(_ => words[r.Next(words.Length)]));
					var tokens = on.Lexer.Tokenize(text).ToList();
					var x = on.Parser.Parse(tokens);
					var y = off.Parser.Parse(tokens);
					var interp = Interp.Parse(on.Analysis, tokens);
					// same acceptance and errors; the trees differ only by the collapsed nodes
					if (!x.Errors.SequenceEqual(y.Errors) || x.Tree != interp.Tree || !x.Errors.SequenceEqual(interp.Errors)) mismatches.Add(text);
				}
			T.Equal("", string.Join(" | ", mismatches.Take(5)), "collapsed and uncollapsed rules predict the same");
			T.Check(collapsed.Accepts("x b a a") && collapsed.Accepts("y b a") && !collapsed.Accepts("x b"), "the call-stack case still works collapsed");
			T.Check(!collapsed.Parse("y b a").Tree.Contains("\n  A"), "and A leaves no node");
		}

		public static void AmbiguityResolvesLikeTheDefaultPolicies()
		{
			// an ambiguous decision left to ALL(*) comes out the way the default policy would settle it
			var adaptive = _Build("elseAdaptive", DanglingElse + Ws);
			var greedy = _Build("elseGreedy", DanglingElseGreedy + Ws);
			T.Equal(ParserEngine.AllStar, adaptive.Analysis.Engine, "left to ALL(*)");
			T.Equal(ParserEngine.LL1, greedy.Analysis.Engine, "settled greedily");
			var r = new Random(21);
			var words = new[] { "if", "e", "then", "else", "s" };
			var mismatches = new List<string>();
			for (var i = 0; i < 300; ++i)
			{
				var text = string.Join(" ", Enumerable.Range(0, r.Next(16)).Select(_ => words[r.Next(words.Length)]));
				var tokens = adaptive.Lexer.Tokenize(text).ToList();
				var x = adaptive.Parser.Parse(tokens);
				var y = greedy.Parser.Parse(tokens);
				// on bad input they may report differently: ALL(*) sees past the else that LL(1) takes on trust
				if (0 == x.Errors.Count != (0 == y.Errors.Count) || (0 == x.Errors.Count && x.Tree != y.Tree)) mismatches.Add(text);
			}
			T.Equal("", string.Join(" | ", mismatches.Take(5)), "the adaptive dangling else accepts the same and binds like policy=greedy");
			var alt = _Build("ambiguousAlt", "S= A | B; A= a b; B= a b; a=\"a\"; b=\"b\";" + Ws);
			T.Check(alt.Accepts("a b") && alt.Parse("a b").Tree.Contains("\n  A"), "an ambiguous alternation takes the first, like policy=first");
		}

		#region Random grammars
		// a small random grammar over a, b, c, d. Most come out LL(1) or LL(k); plenty need ALL(*).
		static string _RandomGrammar(Random r)
		{
			string[] nts = { "S", "N1", "N2", "N3" };
			var sb = new StringBuilder();
			string item(int depth)
			{
				var k = r.Next(100);
				if (k < 45 || 2 < depth) return "abcd".Substring(r.Next(4), 1);
				if (k < 70) return nts[r.Next(nts.Length)];
				if (k < 80) return "[ " + seq(depth + 1) + " ]";
				if (k < 90) return "{ " + seq(depth + 1) + " }";
				if (k < 95) return "{ " + seq(depth + 1) + " }+";
				return "( " + alt(depth + 1) + " )";
			}
			string seq(int depth) => string.Join(" ", Enumerable.Range(0, 1 + r.Next(3)).Select(_ => item(depth)));
			string alt(int depth) => string.Join(" | ", Enumerable.Range(0, 1 + r.Next(3)).Select(_ => seq(depth)));
			foreach (var nt in nts)
				sb.Append(nt).Append("<nowarn>= ").Append(alt(0)).Append(";\n");
			sb.Append("a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\";");
			return sb.ToString();
		}

		static List<Token> _Tokens(GrammarAnalysis a, IEnumerable<int> ids)
			=> ids.Select((id, i) => new Token(id - a.Symbols.NonTerminalCount, a.Symbols.GetName(id), 1, 2 * i + 1, 2 * i)).ToList();

		/// <summary>
		/// On random grammars, whatever engine is chosen accepts exactly the grammar's language (checked
		/// against an Earley recognizer), and LL(1), LL(k) and ALL(*) build the same trees and report the
		/// same errors wherever each of them can take the grammar
		/// </summary>
		public static void RandomGrammarsMatchEarleyAndEachOther()
		{
			var r = new Random(1234);
			int grammars = 0, adaptive = 0, compared = 0, wrong = 0;
			var failures = new List<string>();
			var compiled = 0;
			for (var attempt = 0; attempt < 1500 && grammars < 250; ++attempt)
			{
				var xbnf = _RandomGrammar(r);
				GrammarAnalysis auto;
				try
				{
					auto = _Analyze(xbnf, ParserEngine.Auto, "rnd");
				}
				catch (GrammarException) { continue; }
				// a rule that can never finish leaves error handling undefined; it's warned about
				if (auto.HasErrors || auto.Messages.Any(m => m.Message.Contains("can't match any finite input"))) continue;
				var oracle = new GrammarOracle(auto);
				if (!oracle.IsProductive) continue;
				++grammars;
				if (ParserEngine.AllStar == auto.Engine) ++adaptive;
				var s = auto.Symbols;
				var terminals = "abcd".Select(ch => s.GetId(ch.ToString())).ToArray();

				// the other engines, where they take the grammar without settling anything by policy
				var others = new List<GrammarAnalysis>();
				foreach (var e in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
				{
					if (e == auto.Engine) continue;
					var x = _Analyze(xbnf, e, "rnd");
					if (!x.HasErrors && x.Decisions.All(d => null == d.AppliedPolicy)) others.Add(x);
				}

				// synthesize and compile a few ALL(*) ones too, to check the generated code against the interpreter
				CompiledParser? cp = null;
				if (ParserEngine.AllStar == auto.Engine && compiled < 12)
				{
					++compiled;
					var src = CSharpSynthesizer.Synthesize(auto);
					File.WriteAllText(Path.Combine(SynthesisTests.OutDir, $"RandomParser{compiled}.cs"), src);
					cp = CompiledParser.Compile(src, auto.Grammar.ClassName!);
				}

				var inputs = new List<List<int>>();
				for (var i = 0; i < 25; ++i)
				{
					var sentence = oracle.Sentence(r);
					inputs.Add(sentence);
					var damaged = sentence.ToList();
					var edits = 1 + r.Next(2);
					for (var e = 0; e < edits; ++e)
					{
						var pos = r.Next(damaged.Count + 1);
						switch (r.Next(3))
						{
							case 0: if (pos < damaged.Count) damaged.RemoveAt(pos); break;
							case 1: damaged.Insert(pos, terminals[r.Next(4)]); break;
							default: if (pos < damaged.Count) damaged[pos] = terminals[r.Next(4)]; break;
						}
					}
					inputs.Add(damaged);
				}
				inputs.Add(new List<int>());
				foreach (var ids in inputs)
				{
					var tokens = _Tokens(auto, ids);
					var mine = Interp.Parse(auto, tokens);
					var inLanguage = oracle.Recognizes(ids);
					++compared;
					if (inLanguage != (0 == mine.Errors.Count))
					{
						++wrong;
						if (failures.Count < 3) failures.Add($"{auto.Engine} {(inLanguage ? "rejected" : "accepted")} [{string.Join(" ", ids.Select(s.GetName))}] in\n{xbnf}\n{string.Join("; ", mine.Errors)}");
					}
					foreach (var o in others)
					{
						var theirs = Interp.Parse(o, tokens);
						if (theirs.Tree != mine.Tree || !theirs.Errors.SequenceEqual(mine.Errors))
						{
							++wrong;
							if (failures.Count < 3) failures.Add($"{o.Engine} and {auto.Engine} disagree on [{string.Join(" ", ids.Select(s.GetName))}] in\n{xbnf}\n{theirs.Tree}\n{mine.Tree}");
						}
					}
					if (null != cp)
					{
						var syn = cp.Parse(tokens);
						if (syn.Tree != mine.Tree || !syn.Errors.SequenceEqual(mine.Errors))
						{
							++wrong;
							if (failures.Count < 3) failures.Add($"synthesized and interpreted ALL(*) disagree on [{string.Join(" ", ids.Select(s.GetName))}] in\n{xbnf}");
						}
					}
				}
			}
			T.Check(200 <= grammars, $"enough grammars ({grammars})");
			T.Check(40 <= adaptive, $"enough of them need ALL(*) ({adaptive})");
			T.Equal("", string.Join("\n----\n", failures), $"{wrong} of {compared} parses wrong");
		}
		#endregion
	}
}
