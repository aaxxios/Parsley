using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parsley.Runtime;

namespace Parsley.Tests
{
	/// <summary>
	/// Left factoring (@factor, factor=): what it factors, that it never changes the tree, and per-decision maxk
	/// </summary>
	public static class FactoringTests
	{
		static GrammarAnalysis _Analyze(string xbnf, ParserEngine engine = ParserEngine.Auto, string name = "f")
			=> GrammarAnalysis.Analyze(Grammar.Parse(xbnf, name + ".xbnf"), engine);

		static GrammarAnalysis _Checked(string xbnf, ParserEngine engine = ParserEngine.Auto, string name = "f")
		{
			var a = _Analyze(xbnf, engine, name);
			GrammarException.ThrowIfErrors(a.Messages);
			return a;
		}

		// every node with its location, so a tree whose nodes say they're somewhere else comes out different
		static string _Dump(ParseNode node)
		{
			var sb = new StringBuilder();
			void write(ParseNode n, int depth)
			{
				sb.Append(' ', depth * 2).Append(n.Symbol).Append('@').Append(n.Line).Append(':').Append(n.Column).Append(':').Append(n.Position);
				if (null != n.Value) sb.Append(' ').Append(n.Value);
				sb.Append('\n');
				foreach (var c in n.Children) write(c, depth + 1);
			}
			write(node, 0);
			return sb.ToString();
		}

		static (string Tree, List<string> Errors) _Interpret(GrammarAnalysis a, IEnumerable<Token> tokens, ParseHooks? hooks = null)
		{
			var p = new GrammarInterpreter(a, tokens, hooks);
			var root = p.Parse();
			return (_Dump(root), p.Errors.Select(e => e.ToString()).ToList());
		}

		static (string Tree, List<string> Errors) _Interpret(GrammarAnalysis a, string text, ParseHooks? hooks = null)
			=> _Interpret(a, new RegexLexer(a).Tokenize(text).ToList(), hooks);

		static int _Factored(GrammarAnalysis a) => a.Messages.Count(m => m.Message.StartsWith("Left factored"));

		static CompiledParser _Compile(GrammarAnalysis a, string tag)
		{
			var src = CSharpSynthesizer.Synthesize(a);
			File.WriteAllText(Path.Combine(SynthesisTests.OutDir, $"{a.Grammar.ClassName}.{tag}.cs"), src);
			var p = CompiledParser.Compile(src, a.Grammar.ClassName!);
			T.Equal("", string.Join("\n", p.Diagnostics), $"{a.Grammar.ClassName} ({tag}) compiles without warnings");
			return p;
		}

		static (string Tree, List<string> Errors) _Run(CompiledParser p, GrammarAnalysis a, string text)
		{
			var (root, errors) = p.ParseRaw(new RegexLexer(a).Tokenize(text).ToList());
			return (CompiledParser.Dump(root), errors);
		}

		const string Ws = " ws<hidden>= '[ \\t]+';";

		// what factoring promises: on input in the language, the same tree, locations and all; on input that isn't,
		// an error just the same. These small grammars also get the same first error, which isn't a promise: where an
		// LL parser notices an error depends on where it commits to a branch, which factoring moves
		static void _Same((string Tree, List<string> Errors) expected, (string Tree, List<string> Errors) actual, string what)
		{
			if (0 == expected.Errors.Count)
			{
				T.Equal(expected.Tree, actual.Tree, $"the same tree for {what}");
				T.Equal("", string.Join("; ", actual.Errors), $"no errors for {what}");
			}
			else
				T.Equal(expected.Errors[0], actual.Errors.FirstOrDefault(), $"the same first error for {what}");
		}

		// a call and a path both start with a name, two productions down
		const string Paths = @"
			Path<start>= Call | Steps;
			Call= name lp [ Path { comma Path } ] rp { Pred };
			Steps= Step { slash Step };
			Step= name { Pred } | dot;
			Pred= lb Path rb;
			name= '[a-z]+'; lp<collapsed>= ""(""; rp<collapsed>= "")""; comma<collapsed>= "",""; slash= ""/""; dot= "".""; lb<collapsed>= ""[""; rb<collapsed>= ""]"";" + Ws;

		static readonly string[] PathInputs = { "a", "a(b)", "a[b]/c", "f(a, b)[c]", "a/b/c", ".", "./a", "f()", "f(g(h))[i(j)]/k", "a(", "(", "a[", "a/", "a(b c)", "" };

		/// <summary>
		/// What factoring factors, and that the parser comes out lighter and parses the same
		/// </summary>
		public static void FactorsTokensInsideProductions()
		{
			var plain = _Checked(Paths);
			var factored = _Checked("@factor true;" + Paths);
			T.Equal(ParserEngine.LLk, plain.Engine, "without factoring, telling a call from a path takes two tokens");
			T.Equal(ParserEngine.LL1, factored.Engine, "factored, one");
			T.Equal(1, _Factored(factored), "one alternation factored");
			T.Check(factored.Messages.Any(m => m.Message.Contains("Left factored Call | Steps in \"Path\" on \"name\"")), "and it says which, on what");
			T.Check(!plain.Messages.Any(m => m.Message.StartsWith("Left factored")), "nothing is factored unless asked");
			var pp = _Compile(plain, "plain");
			var fp = _Compile(factored, "factored");
			foreach (var text in PathInputs)
			{
				var expected = _Interpret(plain, text);
				var actual = _Interpret(factored, text);
				_Same(expected, actual, $"\"{text}\"");
				var synthesized = _Run(fp, factored, text);
				_Same(_Run(pp, plain, text), synthesized, $"\"{text}\", synthesized");
				T.Equal(actual.Tree, synthesized.Tree, $"synthesized and interpreted agree on \"{text}\"");
				T.Equal(string.Join("; ", actual.Errors), string.Join("; ", synthesized.Errors), $"and on its errors");
			}
			var src = CSharpSynthesizer.Synthesize(factored);
			T.Check(src.Contains("Adopt(") && src.Contains("EnterInlined("), "the factored parser makes the inlined productions' nodes itself");
			var report = new StringWriter();
			GrammarReport.Write(factored, report);
			T.Check(report.ToString().Contains("1 left factored") && report.ToString().Contains("factored"), "the report says which rules were factored");
			T.Check(!report.ToString().Contains("unreachable"), "and counts a rule that was inlined everywhere as used");
		}

		/// <summary>
		/// The nodes of inlined productions are where they'd have been, even when the tokens they start with
		/// are collapsed and make no node to take over, and collapsed productions stay without one
		/// </summary>
		public static void KeepsTheTreeAndItsLocations()
		{
			const string types = @"
				S<start>= { T };
				T= Name | Typed;
				Name= kw;
				Typed= kw lp rp | kw lp arg rp;
				kw<collapsed>= ""text""; lp<collapsed>= ""(""; rp<collapsed>= "")""; arg= '[a-z]';" + Ws;
			foreach (var variant in new[] { types, types.Replace("Name= kw;", "Name<collapsed>= kw;"), types.Replace("kw<collapsed>=", "kw="), types.Replace("T= Name", "T<collapsed>= Name") })
			{
				var plain = _Checked(variant);
				var factored = _Checked("@factor true;" + variant);
				T.Equal(ParserEngine.LL1, factored.Engine, "factored to LL(1)");
				T.Check(0 < _Factored(factored), "factored");
				foreach (var text in new[] { "text", "text()", "text text()  text(x) text", "  text (  ) text", "text(", "text(x", "text )" })
				{
					_Same(_Interpret(plain, text), _Interpret(factored, text), $"\"{text}\"");
				}
			}
			// three tokens moved out of a production: its node takes all three over
			const string three = "S<start>= A x | A y; A= a b c; a=\"a\"; b=\"b\"; c=\"c\"; x=\"x\"; y=\"y\";" + Ws;
			var p3 = _Checked(three);
			var f3 = _Checked("@factor true;" + three);
			T.Equal(4, p3.Depth, "unfactored, it takes four tokens of lookahead");
			T.Equal(ParserEngine.LL1, f3.Engine, "factored, one");
			foreach (var text in new[] { "a b c x", "a b c y", "a b c", "a b x" })
				_Same(_Interpret(p3, text), _Interpret(f3, text), $"\"{text}\"");
			var c3 = _Compile(f3, "three");
			T.Equal(_Interpret(p3, "a b c y").Tree, _Run(c3, f3, "a b c y").Tree, "synthesized, the node holds the three tokens");
		}

		/// <summary>
		/// Hooks still run for inlined productions, with the same nodes; a production with an entering hook isn't
		/// inlined, since the hook would run after the tokens moved in front of it
		/// </summary>
		public static void Hooks()
		{
			const string g = @"
				S<start>= { X };
				X= A | B;
				A<parsed=""Done"">= n p;
				B<parsed=""Done"">= n q;
				n= ""n""; p= ""p""; q= ""q"";" + Ws;
			string run(GrammarAnalysis a, string text)
			{
				var log = new List<string>();
				var hooks = new ParseHooks { Parsed = (m, node) => log.Add(m + " " + node.Symbol + "@" + node.Column) };
				_Interpret(a, text, hooks);
				return string.Join(", ", log);
			}
			var plain = _Checked(g);
			var factored = _Checked("@factor true;" + g);
			T.Equal(1, _Factored(factored), "factored");
			T.Equal(run(plain, "n p n q"), run(factored, "n p n q"), "parsed hooks run with the same nodes");
			var entering = _Checked("@factor true;" + g.Replace("A<parsed=\"Done\">", "A<parsed=\"Done\", entering=\"Start\">"));
			T.Equal(0, _Factored(entering), "not through a production with an entering hook");
			T.Equal(ParserEngine.LLk, entering.Engine, "which leaves it to lookahead");
		}

		/// <summary>
		/// @factor, and factor= on rules and groups, turn it on and off
		/// </summary>
		public static void Switches()
		{
			const string g = "S<start>= X; X= A | B; A= n p; B= n q; n=\"n\"; p=\"p\"; q=\"q\";";
			T.Equal(1, _Factored(_Checked("@factor true;" + g)), "@factor true");
			T.Equal(0, _Factored(_Checked("@factor false;" + g)), "@factor false");
			T.Equal(1, _Factored(_Checked(g.Replace("X=", "X<factor>="))), "factor on the rule");
			T.Equal(1, _Factored(_Checked("S<start>= X; X= ( A | B )<factor>; A= n p; B= n q; n=\"n\"; p=\"p\"; q=\"q\";")), "factor on the group");
			T.Equal(0, _Factored(_Checked("@factor true;" + g.Replace("X=", "X<factor=false>="))), "factor=false on the rule wins over @factor");
			T.Equal(0, _Factored(_Checked("@factor true;" + g.Replace("A=", "A<factor=false>="))), "and keeps a production from being inlined");
			T.Throws<GrammarException>(() => _Analyze("@factor maybe;" + g), "@factor takes true or false");
			T.Check(_Analyze(g.Replace("X=", "X<factor=2>=")).Messages.Any(m => ErrorLevel.Warning == m.Level && m.Message.Contains("factor expects true or false")), "factor= takes true or false");
			// an operator rule is parsed by precedence climbing, and isn't factored or inlined
			const string ops = "@factor true; E<start, operators>= E plus E | n | n lp rp; plus=\"+\"; n=\"n\"; lp=\"(\"; rp=\")\";";
			T.Equal(0, _Factored(_Checked(ops)), "operator rules are left alone");
		}

		/// <summary>
		/// maxk= on a decision or a rule sets that decision's own lookahead limit
		/// </summary>
		public static void MaxKOnADecision()
		{
			const string g = "S<start>= A x | A y; A= a b c; a=\"a\"; b=\"b\"; c=\"c\"; x=\"x\"; y=\"y\";";
			var plain = _Checked(g);
			var d = plain.Decisions.Single(x => "S" == x.Production.Name);
			T.Equal(4, d.MaxK, "the default limit");
			T.Equal(4, d.Depth, "four tokens decide it");
			var two = _Checked("S<start>= ( A x | A y )<maxk=2>; A= a b c; a=\"a\"; b=\"b\"; c=\"c\"; x=\"x\"; y=\"y\";");
			var d2 = two.Decisions.Single(x => "S" == x.Production.Name);
			T.Equal(2, d2.MaxK, "maxk on the group");
			T.Check(d2.IsAdaptive, "which can't decide it in two, so ALL(*) does");
			var rule = _Checked(g.Replace("S<start>=", "S<start, maxk=2>="));
			T.Equal(2, rule.Decisions.Single(x => "S" == x.Production.Name).MaxK, "maxk on the rule");
			var more = _Checked("@maxk 2;" + g.Replace("S<start>=", "S<start, maxk=4>="));
			T.Equal(4, more.Decisions.Single(x => "S" == x.Production.Name).Depth, "it can raise the limit as well as lower it");
			T.Check(_Analyze(g.Replace("S<start>=", "S<start, maxk=0>=")).HasErrors, "maxk is at least 1");
			// the dangling else, greedy at one token where the rest of the grammar may look further
			const string dangling = "@maxk 4; S<start>= \"if\" e \"then\" S [ \"else\" S ]<policy=greedy, maxk=1> | s | t u v; e=\"e\"; s=\"s\"; t=\"t\"; u=\"u\"; v=\"v\";";
			var dd = _Checked(dangling).Decisions.Single(x => DecisionKind.Optional == x.Kind);
			T.Equal(1, dd.MaxK, "a policy at one token");
			T.Equal(1, dd.Depth, "decided on one");
		}

		/// <summary>
		/// Factoring that can't make an alternation's decisions one token deep is undone: on an ambiguous grammar it
		/// would only inline, and leave more for ALL(*) to predict
		/// </summary>
		public static void UndoesWhatDoesntHelp()
		{
			const string g = @"S<start, nowarn>= N2 N1;
				N1<nowarn>= N3 N3 | d;
				N2<nowarn>= a N3;
				N3<nowarn>= N1 { N2 [ ( d a d | d b ) N3 ] } N1 | N1 c | N1 { { d { d } } N1 }+;
				a<collapsed>=""a""; b=""b""; c=""c""; d<collapsed>=""d"";";
			var plain = _Checked(g);
			var factored = _Checked("@factor true;" + g);
			T.Check(factored.Messages.Any(m => m.Message.StartsWith("Didn't left factor") && m.Message.Contains("more than one token")), "it says which it left as written, and why");
			T.Check(factored.Decisions.Count(d => d.IsAdaptive) <= plain.Decisions.Count(d => d.IsAdaptive), "and leaves no more for ALL(*) than there was");
			foreach (var d in factored.Decisions.Where(d => d.Node is Alternation a && (a.Factored || a.FactoredTail)))
				T.Check(!d.IsAdaptive && 1 == d.Depth, $"what it kept is decided on one token: {d}");
		}

		/// <summary>
		/// On random grammars, factoring never changes the language, the tree, where its nodes are, or (on input the
		/// language has) the errors, interpreted or synthesized
		/// </summary>
		public static void RandomGrammarsKeepTheirTrees()
		{
			var r = new Random(4321);
			int grammars = 0, factoredGrammars = 0, lighter = 0, compared = 0, wrong = 0, compiled = 0, invalid = 0, sameFirstError = 0;
			var failures = new List<string>();
			for (var attempt = 0; attempt < 2000 && grammars < 200; ++attempt)
			{
				var xbnf = _RandomGrammar(r);
				GrammarAnalysis plain, factored;
				try
				{
					plain = _Analyze(xbnf, ParserEngine.Auto, "rnd");
					factored = _Analyze("@factor true;\n" + xbnf, ParserEngine.Auto, "rnd");
				}
				catch (GrammarException) { continue; }
				if (plain.HasErrors || plain.Messages.Any(m => m.Message.Contains("can't match any finite input"))) continue;
				if (factored.HasErrors)
				{
					++wrong;
					if (failures.Count < 3) failures.Add($"factoring made errors in\n{xbnf}\n{string.Join("; ", factored.Messages.Where(m => ErrorLevel.Error == m.Level))}");
					continue;
				}
				var oracle = new GrammarOracle(plain);
				if (!oracle.IsProductive) continue;
				++grammars;
				if (0 == _Factored(factored)) continue;
				++factoredGrammars;
				if (factored.Engine < plain.Engine || (factored.Engine == plain.Engine && factored.Depth < plain.Depth)) ++lighter;
				CompiledParser? cp = null;
				if (compiled < 10)
				{
					++compiled;
					cp = _Compile(factored, $"Random{compiled}");
				}
				var s = plain.Symbols;
				var terminals = "abcd".Select(ch => s.GetId(ch.ToString())).ToArray();
				var inputs = new List<List<int>>();
				for (var i = 0; i < 20; ++i)
				{
					// short sentences: on these deliberately ambiguous grammars, ALL(*) does a lot of work per token
					var sentence = oracle.Sentence(r);
					for (var tries = 0; 16 < sentence.Count && tries < 10; ++tries) sentence = oracle.Sentence(r);
					if (16 < sentence.Count) continue;
					inputs.Add(sentence);
					var damaged = sentence.ToList();
					var pos = r.Next(damaged.Count + 1);
					switch (r.Next(3))
					{
						case 0: if (pos < damaged.Count) damaged.RemoveAt(pos); break;
						case 1: damaged.Insert(pos, terminals[r.Next(4)]); break;
						default: if (pos < damaged.Count) damaged[pos] = terminals[r.Next(4)]; break;
					}
					inputs.Add(damaged);
				}
				inputs.Add(new List<int>());
				foreach (var ids in inputs)
				{
					// the same token ids in both: factoring adds no symbols
					var tokens = ids.Select((id, i) => new Token(id - s.NonTerminalCount, s.GetName(id), 1, 2 * i + 1, 2 * i)).ToList();
					var inLanguage = oracle.Recognizes(ids);
					var mine = _Interpret(plain, tokens);
					var theirs = _Interpret(factored, tokens);
					++compared;
					var text = string.Join(" ", ids.Select(s.GetName));
					if (!inLanguage)
					{
						++invalid;
						if (mine.Errors.FirstOrDefault() == theirs.Errors.FirstOrDefault()) ++sameFirstError;
					}
					if (inLanguage != (0 == theirs.Errors.Count))
					{
						++wrong;
						if (failures.Count < 3) failures.Add($"factored {(inLanguage ? "rejected" : "accepted")} [{text}] in\n{xbnf}\n{string.Join("; ", theirs.Errors)}");
					}
					else if (inLanguage && (mine.Tree != theirs.Tree || 0 < mine.Errors.Count))
					{
						++wrong;
						if (failures.Count < 3) failures.Add($"factoring changed the tree of [{text}] in\n{xbnf}\n{mine.Tree}\n{theirs.Tree}");
					}
					if (null != cp)
					{
						var syn = cp.ParseRaw(tokens);
						if (CompiledParser.Dump(syn.Root) != theirs.Tree || !syn.Errors.SequenceEqual(theirs.Errors))
						{
							++wrong;
							if (failures.Count < 3) failures.Add($"synthesized and interpreted factored parsers disagree on [{text}] in\n{xbnf}\n{CompiledParser.Dump(syn.Root)}\n{theirs.Tree}");
						}
					}
				}
			}
			T.Check(180 <= grammars, $"enough grammars ({grammars})");
			T.Check(60 <= factoredGrammars, $"enough of them factored ({factoredGrammars})");
			T.Check(20 <= lighter, $"and some of those got a lighter parser ({lighter})");
			// where an LL parser notices an error depends on where it commits to a branch, which factoring moves,
			// so this is a measure, not a promise: most errors come out the same
			T.Check(invalid * 9 <= sameFirstError * 10, $"most input that isn't in the language gets the same first error ({sameFirstError} of {invalid})");
			Console.WriteLine($"  {grammars} grammars, {factoredGrammars} factored, {lighter} lighter; the same first error on {sameFirstError} of {invalid} bad inputs");
			T.Equal("", string.Join("\n----\n", failures), $"{wrong} of {compared} parses wrong");
		}

		// like EngineTests.RandomGrammar, with more alternatives that start alike, and some collapsed productions
		// and terminals, whose nodes factoring must still leave out
		static string _RandomGrammar(Random r)
		{
			string[] nts = { "S", "N1", "N2", "N3" };
			var sb = new StringBuilder();
			string item(int depth)
			{
				var k = r.Next(100);
				if (k < 45 || 2 < depth) return "abcd".Substring(r.Next(4), 1);
				if (k < 72) return nts[1 + r.Next(nts.Length - 1)];
				if (k < 82) return "[ " + seq(depth + 1) + " ]";
				if (k < 90) return "{ " + seq(depth + 1) + " }";
				if (k < 94) return "{ " + seq(depth + 1) + " }+";
				return "( " + alt(depth + 1) + " )";
			}
			string seq(int depth) => string.Join(" ", Enumerable.Range(0, 1 + r.Next(3)).Select(_ => item(depth)));
			string alt(int depth)
			{
				var options = Enumerable.Range(0, 1 + r.Next(3)).Select(_ => seq(depth)).ToList();
				// often, two alternatives that start the same way
				if (r.Next(2) == 0 && 0 < options.Count)
				{
					var start = item(depth + 1);
					options = options.Select(o => r.Next(2) == 0 ? start + " " + o : o).ToList();
				}
				return string.Join(" | ", options);
			}
			foreach (var nt in nts)
			{
				var attrs = "S" == nt ? "nowarn" : r.Next(3) == 0 ? "nowarn, collapsed" : "nowarn";
				sb.Append(nt).Append('<').Append(attrs).Append(">= ").Append(alt(0)).Append(";\n");
			}
			sb.Append(r.Next(2) == 0 ? "a<collapsed>=\"a\";" : "a=\"a\";").Append(" b=\"b\"; c=\"c\";").Append(r.Next(3) == 0 ? " d<collapsed>=\"d\";" : " d=\"d\";");
			return sb.ToString();
		}
	}
}
