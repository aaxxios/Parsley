using System;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	public static class EvaluatorTests
	{
		const string Ws = " ws<hidden>= '[ \\t\\r\\n]+';";

		// + and * over integers, with State as a typed table of variables
		const string Calc = @"
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
				if (Number == t.SymbolId) return int.Parse(t.Value!);
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
				var line = src.Split('\n').First(l => l.StartsWith("public partial class") && !l.Contains(": ParserBase"));
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
	}
}
