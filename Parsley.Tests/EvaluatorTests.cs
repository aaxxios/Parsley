using System;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	public static class EvaluatorTests
	{
		const string Ws = " ws<hidden>= '[ \\t\\r\\n]+';";

		// + and * over integers, with State as a typed table of variables
		internal const string Calc = @"
			@evaluator ""CalcEvaluator"";
			Sum<start, type=""int"", state=""System.Collections.Generic.IDictionary<string, int>"">= Sum plus Product | Product => {
				if (1 == node.Children.Count) return (int)Evaluate(node.Children[0]);
				return EvaluateSum(node.Children[0]) + EvaluateProduct(node.Children[2]);
			};
			Product<type=""int"">= Product times Atom | Atom => {
				if (1 == node.Children.Count) return EvaluateAtom(node.Children[0]);
				return EvaluateProduct(node.Children[0]) * EvaluateAtom(node.Children[2]);
			};
			// no action: passes through to the one non-terminal child, so ( and ) don't matter
			Atom<type=""int"">= Value | lparen Sum rparen;
			Value<type=""int"">= number | name => {
				var t = node.Children[0];
				if (number == t.SymbolId) return int.Parse(t.Value!);
				return State[t.Value!]; // a } in a comment, and ""}"" in a string: '}'
			};
			number= '[0-9]+'; name= '[a-z]+'; plus= ""+""; times= ""*""; lparen= ""(""; rparen= "")"";" + Ws;

		public static void ReadsSemanticActions()
		{
			var g = Grammar.Parse("S= a => { return \"}\" + '}' + @\"{\"; /* } */ }; T= a; a= \"a\";");
			var s = g.GetProduction("S")!;
			T.Check(null != s.Action, "action read");
			T.Equal(" return \"}\" + '}' + @\"{\"; /* } */ ", s.Action!.Code, "braces in strings, chars and comments don't end the action");
			T.Equal("a", s.Expression.ToString(), "=> ends the expression");
			T.Check(null == g.GetProduction("T")!.Action, "no action");
			T.Equal("S= a => { return \"}\" + '}' + @\"{\"; /* } */ };", s.ToString(), "round trip");
			T.Equal("S= a;", XbnfWriter.Write(s, false), "written without the action");
			var g2 = Grammar.Parse(g.ToString());
			T.Equal(s.Action.Code, g2.GetProduction("S")!.Action!.Code, "reads back what it writes");
		}

		public static void ReadsInterpolatedAndRawStrings()
		{
			var g = Grammar.Parse("S= a => { var x = $\"{{{node.Symbol}}}\" + $@\"{(true ? \"}\" : \"{\")}\" + $$\"\"\"{{node.Symbol}}}\"\"\"; return x; }; a= \"a\";");
			var code = g.GetProduction("S")!.Action!.Code;
			T.Check(code.EndsWith("return x; "), "interpolation holes and raw strings are skipped over");
			T.Check(!g.GetProduction("S")!.Action!.HasMultilineLiteral, "single line literals");
			var g2 = Grammar.Parse("S= a => {\n\tvar x = @\"one\ntwo\";\n\treturn x;\n}; a= \"a\";");
			T.Check(g2.GetProduction("S")!.Action!.HasMultilineLiteral, "verbatim string spanning lines");
		}

		public static void ActionErrors()
		{
			static string[] errors(string xbnf)
			{
				var msgs = XbnfReader.TryParse(xbnf, null, out var g).ToList();
				if (!msgs.Any(m => m.Level == ErrorLevel.Error))
					msgs.AddRange(GrammarAnalysis.Analyze(g!).Messages);
				return msgs.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message).ToArray();
			}
			T.Check(errors("S= T; T= a => { return 1; }; a= \"a\";").Any(m => m.Contains("start production \"S\" does not")), "an action elsewhere needs one on start");
			T.Check(errors("S= T => { return 1; }; T<collapsed>= a => { return 2; }; a= \"a\";").Any(m => m.Contains("collapsed")), "no action on a collapsed production");
			T.Check(errors("S= a => { return 1; }; a= \"a\" => { return 2; };").Any(m => m.Contains("terminal")), "no action on a terminal");
			T.Check(errors("S<type=5>= a => { return 1; }; a= \"a\";").Any(m => m.Contains("type expects")), "type must be a string");
			T.Check(errors("S= a => { return \"x; }; a= \"a\";").Any(), "unterminated string in an action");
			T.Check(errors("S= a => return 1;; a= \"a\";").Any(m => m.Contains("expecting '{'")), "action needs braces");
			T.Equal(0, errors("S= a => { return 1; }; a= \"a\";").Length, "an action on start alone is fine");
			var a = GrammarAnalysis.Analyze(Grammar.Parse("@class \"P\"; @evaluator \"P\"; S= a => { return 1; }; a= \"a\";"));
			T.Throws<GrammarException>(() => CSharpSynthesizer.Synthesize(a), "parser and evaluator named the same");
		}

		public static void NoEvaluatorWithoutActions()
		{
			var src = CSharpSynthesizer.Synthesize(Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf")));
			T.Check(!src.Contains("Evaluate"), "no evaluator for a grammar without actions");
			T.Check(!src.Contains("using System;"), "parser output unchanged");
		}

		public static void EvaluatorNames()
		{
			static string evaluatorClass(string xbnf, string? filename)
			{
				var src = CSharpSynthesizer.Synthesize(Grammar.Parse(xbnf, filename));
				var line = src.Split('\n').First(l => l.StartsWith("public partial class") && !l.Contains("ParserBase"));
				return line.Substring(line.IndexOf("partial class ") + 14).Trim();
			}
			const string body = "S= a => { return 1; }; a= \"a\";";
			T.Equal("CalcEvaluator", evaluatorClass(body, "calc.xbnf"), "file name + Evaluator");
			T.Equal("SEvaluator", evaluatorClass(body, null), "start symbol + Evaluator without a file");
			T.Equal("my_Eval", evaluatorClass("@evaluator \"my_Eval\";" + body, null), "@evaluator used verbatim");
			T.Equal("@class", evaluatorClass("@evaluator \"class\";" + body, null), "keywords escaped");
		}

		public static void EvaluatesWithActionsAndPassThrough()
		{
			var g = Grammar.Parse(Calc, "calc.xbnf");
			var a = GrammarAnalysis.Analyze(g);
			GrammarException.ThrowIfErrors(a.Messages);
			var src = CSharpSynthesizer.Synthesize(a);
			File.WriteAllText(Path.Combine(SynthesisTests.OutDir, "CalcParser.cs"), src);
			var p = CompiledParser.Compile(src, "CalcParser");
			T.Equal("", string.Join("\n", p.Diagnostics), "parser and evaluator compile without warnings");
			var ev = p.GetEvaluator("CalcEvaluator");
			var evaluate = ev.GetMethod("Evaluate")!;
			T.Equal(typeof(int), evaluate.ReturnType, "Evaluate returns the start production's type");
			T.Check(evaluate.IsPublic && evaluate.IsStatic, "Evaluate is public static");
			var stateParam = evaluate.GetParameters()[1];
			T.Equal(typeof(System.Collections.Generic.IDictionary<string, int>), stateParam.ParameterType, "state argument takes the state type");
			T.Check(!stateParam.HasDefaultValue, "a typed state has no default");
			var stateProp = ev.GetProperty("State", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
			T.Equal(typeof(System.Collections.Generic.IDictionary<string, int>), stateProp.PropertyType, "State has the state type");
			var m = ev.GetMethod("EvaluateProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
			T.Check(m.IsFamily && typeof(int) == m.ReturnType, "EvaluateXxx is protected and typed");
			var byNode = ev.GetMethod("Evaluate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, new[] { evaluate.GetParameters()[0].ParameterType })!;
			T.Check(byNode.IsFamily && typeof(object) == byNode.ReturnType, "Evaluate(node) is a protected instance overload");
			var lexer = new RegexLexer(a);
			var vars = new System.Collections.Generic.Dictionary<string, int> { ["x"] = 5 };
			T.Equal((object)7, p.Evaluate("CalcEvaluator", lexer.Tokenize("1 + 2 * 3"), vars), "precedence");
			T.Equal((object)9, p.Evaluate("CalcEvaluator", lexer.Tokenize("(1 + 2) * 3"), vars), "pass-through ignores terminals around one non-terminal");
			T.Equal((object)26, p.Evaluate("CalcEvaluator", lexer.Tokenize("x * x + 1"), vars), "State reaches the actions");
		}

		public static void StateIsObjectUnlessTypedOnStart()
		{
			var src = CSharpSynthesizer.Synthesize(Grammar.Parse("S= a => { return 1; }; a= \"a\";"));
			T.Check(src.Contains("Evaluate(ParseNode node, object? state = null)") && src.Contains("protected object? State { get; }"), "untyped state is object? and optional");
			var src2 = CSharpSynthesizer.Synthesize(Grammar.Parse("S<state=\"int\">= a => { return State; }; a= \"a\";"));
			T.Check(src2.Contains("Evaluate(ParseNode node, int state)") && src2.Contains("protected int State { get; }"), "typed state");
			var a = GrammarAnalysis.Analyze(Grammar.Parse("S= T => { return 1; }; T<state=\"int\">= a; a= \"a\";"));
			T.Check(a.Messages.Any(m => m.Level == ErrorLevel.Warning && m.Message.Contains("state is ignored")), "state on another production warns");
			T.Check(!a.HasErrors, "and is only a warning");
			var b = GrammarAnalysis.Analyze(Grammar.Parse("S<state=5>= a => { return 1; }; a= \"a\";"));
			T.Check(b.Messages.Any(m => m.Level == ErrorLevel.Error && m.Message.Contains("state expects")), "state must be a string");
		}

		public static void PassThroughNeedsOneNonTerminalChild()
		{
			var g = Grammar.Parse("@class \"PtParser\"; @evaluator \"PtEvaluator\"; S= T => { return Evaluate(node.Children[0]); }; T= a; a= \"a\";");
			var a = GrammarAnalysis.Analyze(g);
			var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(a), "PtParser");
			T.Throws<InvalidOperationException>(() => p.Evaluate("PtEvaluator", new RegexLexer(a).Tokenize("a")), "a node with no non-terminal child and no action");
		}

		static GrammarMessage[] _Messages(string xbnf, ErrorLevel level)
		{
			var msgs = XbnfReader.TryParse(xbnf, null, out var g).ToList();
			if (!msgs.Any(m => m.Level == ErrorLevel.Error))
				msgs.AddRange(GrammarAnalysis.Analyze(g!).Messages);
			return msgs.Where(m => m.Level == level).ToArray();
		}

		public static void ReadsUsings()
		{
			var g = Grammar.Parse("@using \"System.Text\";\n@using \"System.Globalization\"; S= a => { return 1; }; a= \"a\";");
			T.Equal(2, g.Usings.Count, "@using can be given more than once");
			T.Equal("System.Text", g.Usings[0].Namespace, "first using");
			T.Equal("System.Globalization", g.Usings[1].Namespace, "second using, in order");
			T.Equal(1, g.Usings[0].Line, "using has its location");
			T.Equal(2, g.Usings[1].Line, "second using's line");
			T.Check(!g.Directives.ContainsKey("using"), "usings aren't single-valued directives");
			var g2 = Grammar.Parse(g.ToString());
			T.Equal(string.Join(",", g.Usings.Select(u => u.Namespace)), string.Join(",", g2.Usings.Select(u => u.Namespace)), "usings round trip");
			T.Equal(2, g.Clone().Usings.Count, "clone keeps usings");
			var b = new Grammar();
			b.Using("System.Text");
			T.Equal("System.Text", b.Usings.Single().Namespace, "builder API");
		}

		public static void LinePragmasMapToTheGrammar()
		{
			// line 1: the @using, line 2: the start rule's attributes, its action's code on lines 3 to 5
			const string text = "@using \"System.Text\"; @class \"LnParser\"; @evaluator \"LnEvaluator\";\n" +
				"S<start, type=\"string\", state=\"StringBuilder\">= a a => {\n" +
				"    State.Append(node.Children[0].Value);\n" +
				"\n" +
				"    return State.ToString() + MISSING;\n" +
				"};\n" +
				"a= \"a\";";
			var a = GrammarAnalysis.Analyze(Grammar.Parse(text, "ln.xbnf"));
			var code = CSharpSynthesizer.Synthesize(a);
			T.Check(code.Contains("#line 1 \"ln.xbnf\"\nusing System.Text;\n#line default") || code.Contains("#line 1 \"ln.xbnf\"\r\nusing System.Text;\r\n#line default"), "a @using is mapped to its line");
			T.Check(code.Contains("#line 2 \"ln.xbnf\""), "declarations naming the start rule's type and state are mapped to its attributes");
			T.Check(code.Contains("#line 3 \"ln.xbnf\""), "an action is mapped from the line after its brace when nothing follows the brace");
			// the compiler reports an error in the action at its line in the grammar, blank line included
			var ex = _Throws(() => CompiledParser.Compile(code, "LnParser"));
			T.Check(null != ex && ex.Message.Contains("ln.xbnf(5,"), "an error in an action is reported at its line in the grammar");
			// and with the error fixed, it still compiles and runs
			var fixedCode = CSharpSynthesizer.Synthesize(GrammarAnalysis.Analyze(Grammar.Parse(text.Replace(" + MISSING", ""), "ln.xbnf")));
			var p = CompiledParser.Compile(fixedCode, "LnParser");
			T.Equal(0, p.Diagnostics.Count, "mapped code compiles cleanly");
			// code on the brace's line keeps the brace's line
			var same = CSharpSynthesizer.Synthesize(GrammarAnalysis.Analyze(Grammar.Parse("S<type=\"int\">= a => { return 1; };\na= \"a\";", "same.xbnf")));
			T.Check(same.Contains("#line 1 \"same.xbnf\"\n\t\treturn 1;") || same.Contains("#line 1 \"same.xbnf\"\r\n\t\treturn 1;"), "code on the brace's line is mapped to it");
			// LinePath rewrites the path, and a grammar with no file gets no directives
			var renamed = CSharpSynthesizer.Synthesize(a, new SynthesisOptions { LinePath = f => "grammars/" + f });
			T.Check(renamed.Contains("#line 3 \"grammars/ln.xbnf\"") && !renamed.Contains("#line 3 \"ln.xbnf\""), "LinePath decides the path written");
			T.Check(!CSharpSynthesizer.Synthesize(GrammarAnalysis.Analyze(Grammar.Parse(text))).Contains("#line"), "no directives without a grammar file");
		}
		static Exception? _Throws(Action action)
		{
			try { action(); return null; }
			catch (Exception ex) { return ex; }
		}

		public static void UsingErrors()
		{
			foreach (var bad in new[] { "", "System.", ".Text", "System Text", "static System.Math", "M = System.Math", "1System", "System.Text;" })
				T.Check(_Messages($"@using \"{bad}\"; S= a => {{ return 1; }}; a= \"a\";", ErrorLevel.Error).Any(m => m.Message.Contains("@using expects a namespace")), $"\"{bad}\" is not a namespace");
			T.Check(_Messages("@using 5; S= a => { return 1; }; a= \"a\";", ErrorLevel.Error).Any(m => m.Message.Contains("@using expects a namespace")), "a number is not a namespace");
			foreach (var good in new[] { "System", "System.Text", "My_Ns.@class.Sub2", "_x" })
				T.Equal(0, _Messages($"@using \"{good}\"; S= a => {{ return 1; }}; a= \"a\";", ErrorLevel.Error).Length, $"\"{good}\" is a namespace");
			var err = _Messages("S= a => { return 1; };\n@using \"a b\"; a= \"a\";", ErrorLevel.Error).Single();
			T.Equal(2, err.Line, "the error points at the @using");
		}

		public static void WarnsWhenUsingOrEvaluatorHasNoActions()
		{
			var w = _Messages("@evaluator \"E\";\n@using \"System.Text\";\n@using \"System.Linq\"; S= a; a= \"a\";", ErrorLevel.Warning);
			var ev = w.Where(m => m.Message.Contains("@evaluator")).ToArray();
			T.Equal(1, ev.Length, "@evaluator without actions warns");
			T.Check(ev[0].Message.Contains("no semantic actions"), "says why");
			T.Equal(1, ev[0].Line, "@evaluator warning has its location");
			var us = w.Where(m => m.Message.StartsWith("@using")).ToArray();
			T.Equal(2, us.Length, "each @using without actions warns");
			T.Check(us[0].Message.Contains("System.Text") && us[1].Message.Contains("System.Linq"), "names each namespace");
			T.Equal(2, us[0].Line, "@using warning has its location");
			T.Equal(0, _Messages("@evaluator \"E\"; @using \"System.Text\"; S= a => { return 1; }; a= \"a\";", ErrorLevel.Warning).Count(m => m.Message.Contains("no semantic actions")), "no warning when there are actions");
			T.Equal(0, _Messages("S= a; a= \"a\";", ErrorLevel.Warning).Count(m => m.Message.Contains("no semantic actions")), "no warning without the directives");
			// the builder API has no locations, but still warns
			var b = new Grammar();
			b.Production("S", Ebnf.Sym("a"));
			b.Terminal("a", "a");
			b.Directives["evaluator"] = "E";
			b.Using("System.Text");
			var bw = GrammarAnalysis.Analyze(b).Messages.Where(m => m.Message.Contains("no semantic actions")).ToArray();
			T.Equal(2, bw.Length, "builder grammars warn too");
		}

		public static void SynthesizesUsings()
		{
			const string actions = "S<type=\"string\">= a => { var sb = new StringBuilder(); sb.Append(node.Children[0].Value); return sb.ToString() + CultureInfo.InvariantCulture.Name; }; a= \"a\";";
			var a = GrammarAnalysis.Analyze(Grammar.Parse("@class \"UParser\"; @evaluator \"UEvaluator\"; @namespace \"U.Ns\";"
				+ " @using \"System.Text\"; @using \"System.Globalization\"; @using \"System.Text\"; @using \"System\"; @using \"Parsley.Runtime\"; @using \"System.Collections.Generic\"; " + actions));
			T.Equal(0, a.Messages.Count(m => m.Level != ErrorLevel.Message), "no errors or warnings");
			var src = CSharpSynthesizer.Synthesize(a);
			var lines = src.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
			var usingLines = lines.Where(l => l.StartsWith("using ")).ToList();
			T.Equal("using System;|using System.Collections.Generic;|using Parsley.Runtime;|using System.Text;|using System.Globalization;", string.Join("|", usingLines), "usings in order, each once, the file's own first");
			T.Check(lines.IndexOf("using System.Globalization;") < lines.IndexOf("namespace U.Ns;"), "usings come before the namespace");
			var p = CompiledParser.Compile(src, "UParser");
			T.Check(!p.Diagnostics.Any(d => d.Id == "CS0105"), "no duplicate using warnings");
			T.Equal((object)"a", p.Evaluate("UEvaluator", new RegexLexer(a).Tokenize("a")), "actions use the imported namespaces unqualified");

			// without actions, @using doesn't change the parser
			var plain = CSharpSynthesizer.Synthesize(Grammar.Parse("@using \"System.Text\"; S= a; a= \"a\";"));
			T.Check(!plain.Contains("System.Text"), "@using is ignored without actions");
			T.Check(!plain.Contains("using System;"), "parser output unchanged");
		}

		public static void ImportedUsingsAreAdded()
		{
			var dir = Path.Combine(Path.GetTempPath(), "parsley-using-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				File.WriteAllText(Path.Combine(dir, "sub.xbnf"), "@using \"System.Text\";\n@using \"System.Linq\";\n@evaluator \"SubEvaluator\";\nA= \"a\";");
				File.WriteAllText(Path.Combine(dir, "main.xbnf"), "@using \"System.Linq\";\n@import \"sub.xbnf\";\nS<start>= A;");
				var g = Grammar.ReadFrom(Path.Combine(dir, "main.xbnf"));
				T.Equal("System.Linq,System.Text,System.Linq", string.Join(",", g.Usings.Select(u => u.Namespace)), "an import's usings are added to the importer's");
				T.Check(g.Usings[1].Filename!.EndsWith("sub.xbnf"), "imported using keeps its file name");
				var w = GrammarAnalysis.Analyze(g).Messages.Where(m => m.Level == ErrorLevel.Warning && m.Message.Contains("no semantic actions")).ToArray();
				T.Check(w.Any(m => m.Message.StartsWith("@evaluator") && m.Filename!.EndsWith("sub.xbnf") && 3 == m.Line), "an imported @evaluator's warning points into the import");
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}
	}
}
