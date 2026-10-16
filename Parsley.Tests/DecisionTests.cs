using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	/// <summary>
	/// One grammar per decision kind or feature: synthesize, compile, parse, and check against the interpreter
	/// </summary>
	public static class DecisionTests
	{
		sealed class Harness
		{
			public GrammarAnalysis Analysis = null!;
			public CompiledParser Parser = null!;
			public RegexLexer Lexer = null!;
			public string Source = "";
			public (string Tree, List<string> Errors) Parse(string text)
			{
				var tokens = Lexer.Tokenize(text).ToList();
				var s = Parser.Parse(tokens);
				var i = Interp.Parse(Analysis, tokens);
				T.Equal(i.Tree, s.Tree, $"interpreter and synthesized trees agree on \"{text}\"");
				T.Equal(string.Join("; ", i.Errors), string.Join("; ", s.Errors), $"interpreter and synthesized errors agree on \"{text}\"");
				return s;
			}
			public bool Accepts(string text) => 0 == Parse(text).Errors.Count;
		}
		static Harness _Build(string name, string xbnf, ParserEngine engine = ParserEngine.Auto)
		{
			var g = Grammar.Parse(xbnf, name + ".xbnf");
			var a = GrammarAnalysis.Analyze(g, engine);
			GrammarException.ThrowIfErrors(a.Messages);
			var src = CSharpSynthesizer.Synthesize(a);
			File.WriteAllText(Path.Combine(SynthesisTests.OutDir, g.ClassName + ".cs"), src);
			var h = new Harness { Analysis = a, Source = src, Parser = CompiledParser.Compile(src, g.ClassName!), Lexer = new RegexLexer(a) };
			T.Equal("", string.Join("\n", h.Parser.Diagnostics), name + " compiles without warnings");
			return h;
		}
		const string Ws = " ws<hidden>= '[ \\t\\r\\n]+';";

		public static void FixedLookaheadUsesAPredictor()
		{
			var h = _Build("fixed", "P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";" + Ws);
			T.Check(h.Source.Contains("int PredictS1()"), "predictor method emitted");
			T.Check(h.Source.Contains("switch (Peek(2).SymbolId)"), "looks three tokens deep");
			T.Check(h.Accepts("a b c x") && h.Accepts("a b d x") && h.Accepts("y"), "accepts");
			T.Check(!h.Accepts("a b x") && !h.Accepts("a b c"), "rejects");
			T.Check(h.Parse("a b d x").Tree.Contains("d \"d\""), "picked the second alternative");
		}

		public static void PredictorChecksTokensItDidntLookAt()
		{
			// the predictor settles on e f g after seeing e alone, so f and g must still be checked
			var h = _Build("unchecked", "S= a b c | a b d | e f g; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; e=\"e\"; f=\"f\"; g=\"g\";" + Ws, ParserEngine.LLk);
			T.Check(h.Accepts("e f g") && h.Accepts("a b d"), "accepts");
			T.Check(!h.Accepts("e x g") && !h.Accepts("e f x"), "rejects tokens the predictor never looked at");
			T.Check(h.Source.Contains("Expect(f, node);"), "f is expected, not consumed blindly");
		}

		public static void LoopNeedingTwoTokens()
		{
			var h = _Build("loop2", "S= { a b } a c; a=\"a\"; b=\"b\"; c=\"c\";" + Ws);
			T.Check(h.Source.Contains("while (0 == PredictS1())"), "loop driven by a predictor");
			T.Check(h.Accepts("a c") && h.Accepts("a b a c") && h.Accepts("a b a b a c"), "accepts");
			T.Check(!h.Accepts("a b") && !h.Accepts("a b c"), "rejects");
		}

		public static void DanglingElseBindsToTheNearestIf()
		{
			// settled greedily by LL(k), or left to ALL(*), the else goes to the nearest if
			foreach (var engine in new[] { ParserEngine.LLk, ParserEngine.Auto })
			{
				var h = _Build("ifelse", "S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";" + Ws, engine);
				if (ParserEngine.LLk == engine)
					T.Check(h.Source.Contains("resolved greedily"), "the policy is explained in a comment");
				else
					T.Check(h.Source.Contains("AdaptivePredict(0, 1)"), "predicted adaptively, skipping the optional on input that matches neither");
				var tree = h.Parse("if e then if e then s else s").Tree;
				// the else belongs to the inner if: the outer S has 4 children, the inner one 6
				var lines = tree.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
				var elseLine = lines.First(l => l.Trim().StartsWith("elseKeyword"));
				T.Equal(4, elseLine.Length - elseLine.TrimStart().Length, $"{engine}: else is a child of the inner S (indent 4)");
			}
		}

		public static void LeftRecursionFoldsLeft()
		{
			var h = _Build("expr", "E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';" + Ws);
			T.Check(h.Source.Contains("node = Fold(node);"), "fold emitted");
			T.Check(h.Source.Contains("(left recursion rewritten as a loop)"), "rewrite noted in the comment");
			var tree = h.Parse("1 - 2 - 3").Tree;
			var expected = string.Join(Environment.NewLine,
				"E", "  E", "    E", "      T", "        F", "          n \"1\"",
				"    minus \"-\"", "    T", "      F", "        n \"2\"",
				"  minus \"-\"", "  T", "    F", "      n \"3\"", "");
			T.Equal(expected, tree, "(1 - 2) - 3");
			T.Check(h.Accepts("(1 + 2) * 3 - 4 * (5)"), "precedence grammar accepts");
			T.Check(!h.Accepts("1 + * 2"), "rejects");
		}

		public static void InvertedLoopTestAndSyncRecovery()
		{
			var h = _Build("block",
				"Block= \"{\" { Stmt }<sync=\"semi\"> \"}\";" +
				"Stmt<expected=\"a statement\">= a \"=\" n semi | b \"(\" \")\" semi | c semi | d semi;" +
				"a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; n='[0-9]+'; semi= \";\";" + Ws);
			T.Check(h.Source.Contains("while (Current.SymbolId is not (rbrace or EndOfInput))"), "tests the small exit side");
			T.Check(h.Accepts("{ a = 1; b(); c; }"), "accepts");
			var (tree, errors) = h.Parse("{ a = ; c; 7 7; d; }");
			T.Equal(2, errors.Count, "two separate errors: " + string.Join(" / ", errors));
			T.Check(errors[1].StartsWith("Expected a statement but found n 7"), "expected attribute names the production: " + errors[1]);
			T.Check(tree.Contains("d \"d\""), "parsing resumed at the statement after the bad ones");
		}

		public static void OneOrMoreAndCollapsed()
		{
			var h = _Build("list", "L= { Item }+; Item<collapsed>= Word | n; Word= w; w='[a-z]+'; n='[0-9]+';" + Ws);
			T.Check(h.Source.Contains("do\n") || h.Source.Contains("do\r\n"), "do/while for one-or-more");
			T.Check(h.Accepts("x") && h.Accepts("x 1 y"), "accepts");
			T.Check(!h.Accepts(""), "needs at least one");
			T.Check(!h.Parse("x 1").Tree.Contains("Item"), "collapsed Item leaves no node");
		}

		public static void AwkwardNamesStillCompile()
		{
			var h = _Build("names",
				"Parse= error class \"+=\" Current \"while\" Errors; Current= \"x\"; Errors= int; error=\"e\"; class=\"c\"; int='[0-9]+';" + Ws);
			T.Check(h.Accepts("e c += x while 5"), "accepts");
		}

		public static void FailedPredictionsReportWhereTheInputGoesWrong()
		{
			// a^n b^n or a^n b^2n: only the number of b's says which, so no fixed lookahead decides it
			var h = _Build("anbn", "S= A | B; A= a A b | ; B= a B b b | ; a=\"a\"; b=\"b\";" + Ws);
			T.Equal(ParserEngine.AllStar, h.Analysis.Engine, "anbn needs ALL(*)");
			T.Check(h.Accepts("a a b b") && h.Accepts("a a b b b b") && h.Accepts(""), "anbn accepts");
			// not "Expected "a" or end of input but found "a"" at the first token, which some branch could take
			T.Equal("Expected \"b\" but found end of input at line 1, column 6", string.Join(" / ", h.Parse("a a b").Errors), "a missing b is reported where it's missing");
			T.Equal("Expected \"a\" or end of input but found \"b\" at line 1, column 1", string.Join(" / ", h.Parse("b a").Errors), "a first token no branch takes is reported there");
			// the same for a fixed lookahead, on both engines, which report the same
			foreach (var engine in new[] { ParserEngine.LLk, ParserEngine.AllStar })
			{
				var f = _Build("deepfail", "P= S x | y; S= a b c | a b d | { a } e; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; e=\"e\"; x=\"x\"; y=\"y\";" + Ws, engine);
				T.Equal("Expected \"b\" but found \"d\" at line 1, column 3", string.Join(" / ", f.Parse("a d e").Errors), $"{engine}: the second token");
				T.Equal("Expected \"c\" but found \"e\" at line 1, column 5", string.Join(" / ", f.Parse("a b e x").Errors), $"{engine}: the third token");
				// a loop: the default branch, exiting, when it gets as far as continuing would
				var l = _Build("deeploop", "S= { a b } a c; a=\"a\"; b=\"b\"; c=\"c\";" + Ws, engine);
				T.Equal("Expected \"c\" but found \"a\" at line 1, column 3", string.Join(" / ", l.Parse("a a c").Errors), $"{engine}: a loop exits when exiting gets as far");
			}
		}

		public static void ConstantsKeepTheirSymbolsNames()
		{
			// lowercase and snake_case symbols, keywords, and words C# reads as something else in a pattern or a declaration
			const string xbnf =
				"block= open { stmt_list } _; stmt_list= not semi | nint semi | nuint semi | partial semi | struct semi | and semi | or semi | var semi;" +
				"open=\"{\"; _=\"}\"; not=\"!\"; nint=\"n\"; nuint=\"u\"; partial=\"p\"; struct=\"s\"; and=\"&\"; or=\"|\"; var=\"v\"; semi=\";\";" + Ws;
			foreach (var engine in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
			{
				var h = _Build("kw", xbnf, engine);
				foreach (var c in new[] { "block", "stmt_list", "open", "semi", "and", "or", "var" })
					T.Check(h.Source.Contains($"public const int {c} = "), $"{engine}: {c} is named as written");
				foreach (var c in new[] { "_", "not", "nint", "nuint", "partial", "struct" })
					T.Check(h.Source.Contains($"public const int @{c} = "), $"{engine}: {c} gets an @");
				T.Check(h.Source.Contains("ParseBlock(") && h.Source.Contains("ParseStmtList("), $"{engine}: the methods built from the names are still PascalCase");
				T.Equal("", string.Join("; ", CSharpSynthesizer.CheckNames(h.Analysis)), $"{engine}: nothing renamed");
				T.Check(h.Accepts("{ ! ; n ; u ; p ; s ; & ; | ; v ; }") && h.Accepts("{ }"), $"{engine}: accepts");
				T.Check(!h.Accepts("{ ! }") && !h.Accepts("{ ; }"), $"{engine}: rejects");
			}
		}

		public static void ConstantClashesAreRenamedWithAWarning()
		{
			// symbols named like a parser member, a static field, a type, a local or parameter the code declares,
			// a special constant, a method built from another name, an evaluator member, or dynamic, which is a
			// type in a pattern even written @dynamic
			const string xbnf = @"
				@evaluator ""ClashEvaluator"";
				expr<start, type=""int"">= { node | token | Token | Current | s_tables | s_set2 | dynamic | EndOfInput | ParseExpr | State } done => {
					var n = 0;
					foreach (var c in node.Children) if (node2 == c.SymbolId || dynamic2 == c.SymbolId || s_set2 == c.SymbolId) ++n;
					return n;
				};
				node=""a""; token=""b""; Token=""c""; Current=""d""; s_tables=""e""; s_set2=""f""; dynamic=""g""; EndOfInput=""h""; ParseExpr=""i""; State=""j""; done="";"";" + Ws;
			var renamed = new[] { "node", "token", "Token", "Current", "s_tables", "dynamic", "EndOfInput", "ParseExpr", "State" };
			foreach (var engine in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
			{
				var h = _Build("clash", xbnf, engine);
				foreach (var c in renamed)
					T.Check(h.Source.Contains($"public const int {c}2 = "), $"{engine}: {c} becomes {c}2");
				T.Check(h.Source.Contains("public const int s_set2 = "), $"{engine}: s_set2 keeps its name");
				var messages = CSharpSynthesizer.CheckNames(h.Analysis);
				T.Equal(renamed.Length, messages.Count, $"{engine}: a warning for each renamed constant");
				T.Check(messages.All(m => ErrorLevel.Warning == m.Level), $"{engine}: they're warnings");
				var forNode = messages.FirstOrDefault(m => m.Message.Contains("node2"));
				T.Check(null != forNode && forNode.Message.Contains("parameter or local") && 0 < forNode.Line, $"{engine}: says why, and where the symbol is declared");
				T.Equal(string.Join("; ", messages), string.Join("; ", CSharpSynthesizer.SynthesizeParser(h.Analysis).Messages), $"{engine}: synthesis gives the same warnings");
				T.Check(h.Accepts("a g f c b j ;"), $"{engine}: accepts");
				T.Equal((object)3, h.Parser.Evaluate("ClashEvaluator", h.Lexer.Tokenize("a g f d ;")), $"{engine}: actions use the renamed constants");
			}
		}

		public static void ConstantsOfBuiltGrammarsAreIdentifiers()
		{
			// a grammar built in code can use names XBNF can't: what can't be in an identifier becomes _, which
			// can make two names the same
			var g = new Grammar();
			g.Terminal("x-y", "a"); g.Terminal("x_y", "b"); g.Terminal("1st", "c");
			g.Production("S", Ebnf.Seq(Ebnf.Rep("x-y"), "x_y", "1st"));
			var a = GrammarAnalysis.Analyze(g);
			GrammarException.ThrowIfErrors(a.Messages);
			var p = CSharpSynthesizer.SynthesizeParser(a, new SynthesisOptions { ClassName = "BuiltParser" });
			T.Check(p.Code.Contains("public const int x_y = ") && p.Code.Contains("public const int x_y2 = ") && p.Code.Contains("public const int _1st = "), "made into identifiers, not recased");
			T.Equal(1, p.Messages.Count, "one renamed");
			var compiled = CompiledParser.Compile(p.Code, "BuiltParser");
			T.Equal("", string.Join("\n", compiled.Diagnostics), "compiles without warnings");
		}

		public static void OptionalNeedingFollowContext()
		{
			var h = _Build("follow", "S= A a; A= b [ a ]; a=\"a\"; b=\"b\";" + Ws);
			T.Check(h.Accepts("b a") && h.Accepts("b a a"), "two tokens tell take from skip");
			T.Check(!h.Accepts("b a a a"), "rejects");
		}

		public static void FuzzAlwaysTerminatesAndAgrees()
		{
			var grammars = new (string Name, string Xbnf, string[] Words)[]
			{
				("fz1", "E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';", new[] { "1", "+", "-", "*", "(", ")" }),
				("fz2", "Block= \"{\" { Stmt }<sync=\"semi\"> \"}\"; Stmt= a \"=\" n semi | b \"(\" \")\" semi | c semi; a=\"a\"; b=\"b\"; c=\"c\"; n='[0-9]+'; semi= \";\";", new[] { "{", "}", "a", "b", "c", "=", "1", "(", ")", ";" }),
				("fz3", "S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";", new[] { "if", "e", "then", "else", "s" }),
				("fz4", "P= S x | y; S= a b c | a b d | { a } e; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; e=\"e\"; x=\"x\"; y=\"y\";", new[] { "a", "b", "c", "d", "e", "x", "y" }),
			};
			var r = new Random(3);
			foreach (var (name, xbnf, words) in grammars)
			{
				var h = _Build(name, xbnf + Ws);
				var before = T.Fails;
				for (var i = 0; i < 300 && T.Fails == before; ++i)
				{
					var n = r.Next(14);
					var text = string.Join(" ", Enumerable.Range(0, n).Select(_ => words[r.Next(words.Length)]));
					h.Parse(text);
				}
			}
		}

		public static void SlangImportsAreReported()
		{
			// Slang.xbnf imports files we don't have and declares virtual productions
			var msgs = XbnfParser.TryReadFrom(Path.Combine(T.Dir, "Slang.xbnf"), out _);
			T.Check(msgs.Any(m => m.Message.Contains("Could not import")), "missing imports reported");
			T.Check(msgs.Any(m => m.Message.Contains("Virtual productions are not supported")), "virtual productions reported");
		}
	}
}
