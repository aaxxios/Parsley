using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parsley.Runtime;

namespace Parsley.Tests
{
	/// <summary>
	/// Operator rules: precedence and associativity from the order of alternatives, parsed by precedence climbing
	/// </summary>
	public static class OperatorTests
	{
		const string Ws = " ws<hidden>= '[ \\t\\r\\n]+';";

		// arithmetic with one action for most forms, Sum falling back to it, and Negate with an action of its own
		internal const string Arithmetic = @"
			@class ""OpParser"";
			@evaluator ""OpEvaluator"";
			Expr<start, type=""double"", operators, expected=""an expression"">=
				  Expr pow<assoc=right> Expr
				| Negate
				| Expr ( mul | div | mod ) Expr
				| Sum
				| number
				| lparen Expr rparen
				=> {
				var c = node.Children;
				if (1 == c.Count)
					return number == c[0].SymbolId
						? double.Parse(c[0].Value!, System.Globalization.CultureInfo.InvariantCulture)
						: EvaluateExpr(c[0]);
				var left = EvaluateExpr(c[0]);
				var right = EvaluateExpr(c[2]);
				switch (c[1].SymbolId)
				{
					case add: return left + right;
					case sub: return left - right;
					case mul: return left * right;
					case div: return left / right;
					case mod: return left % right;
					default: return Math.Pow(left, right);
				}
			};
			Negate= sub Expr => { return -EvaluateExpr(node.Children[1]); };
			Sum= Expr ( add | sub ) Expr;
			number= '[0-9]+(\.[0-9]+)?';
			add= ""+""; sub= ""-""; mul= ""*""; div= ""/""; mod= ""%""; pow= ""^"";
			lparen<collapsed>= ""(""; rparen<collapsed>= "")"";" + Ws;

		// C-like: postfix calls, indexing and members, prefix not, a ternary and right associative assignment
		internal const string CLike = @"
			@class ""CLikeParser"";
			E<start, operators>=
				  E lbracket E rbracket
				| E lparen [ E { comma E } ] rparen
				| E dot name
				| bang E
				| E ( star | slash ) E
				| E ( plus | minus ) E
				| E question E colon E <assoc=right>
				| E eq<assoc=right> E
				| name | number | lparen E rparen;
			name= '[a-z]+'; number= '[0-9]+';
			lbracket= ""[""; rbracket= ""]""; lparen= ""(""; rparen= "")""; comma= "",""; dot= ""."";
			bang= ""!""; star= ""*""; slash= ""/""; plus= ""+""; minus= ""-""; question= ""?""; colon= "":""; eq= ""="";" + Ws;

		// a cast and a parenthesized expression start the same way, so telling them apart takes four tokens
		internal const string Casts = @"
			@class ""CastParser"";
			S<start>= { E semi };
			E<operators>=
				  Cast
				| E plus E
				| name
				| lparen E rparen;
			Cast= lparen name rparen E;
			name= '[a-z]+'; plus= ""+""; semi= "";""; lparen= ""(""; rparen= "")"";" + Ws;

		static string _Shape(ParseNode n)
		{
			if (n.IsTerminal) return n.Value ?? "";
			return n.Symbol + "(" + string.Join(" ", n.Children.Select(_Shape)) + ")";
		}

		static GrammarAnalysis _Analyze(string xbnf, ParserEngine engine = ParserEngine.Auto)
		{
			var a = GrammarAnalysis.Analyze(Grammar.Parse(xbnf), engine);
			GrammarException.ThrowIfErrors(a.Messages);
			return a;
		}

		static string _Interpret(GrammarAnalysis a, string text, out IReadOnlyList<ParseError> errors)
		{
			var p = new GrammarInterpreter(a, new RegexLexer(a).Tokenize(text));
			var tree = p.Parse();
			errors = p.Errors;
			return _Shape(tree);
		}

		static string _Interpret(GrammarAnalysis a, string text)
		{
			var shape = _Interpret(a, text, out var errors);
			T.Equal("", string.Join("; ", errors), $"\"{text}\" parses without errors");
			return shape;
		}

		static string[] _Errors(string xbnf) => _Messages(xbnf, ErrorLevel.Error);
		static string[] _Messages(string xbnf, ErrorLevel level)
		{
			var msgs = XbnfParser.TryParse(xbnf, null, out var g).ToList();
			if (!msgs.Any(m => m.Level == ErrorLevel.Error))
				msgs.AddRange(GrammarAnalysis.Analyze(g!).Messages);
			return msgs.Where(m => m.Level == level).Select(m => m.Message).ToArray();
		}

		public static void ClassifiesForms()
		{
			var a = _Analyze(Arithmetic);
			var rule = a.Grammar.GetProduction("Expr")!.Operators;
			if (null == rule)
			{
				T.Check(false, "Expr is an operator rule");
				return;
			}
			T.Equal("Infix Prefix Infix Infix Atom Atom", string.Join(" ", rule.Forms.Select(f => f.Kind)), "each alternative's kind");
			T.Equal("6 5 4 3 0 0", string.Join(" ", rule.Forms.Select(f => f.Level)), "the first alternative binds tightest");
			T.Equal("True False False False False False", string.Join(" ", rule.Forms.Select(f => f.IsRightAssociative)), "assoc=right on the operator");
			T.Equal("Expr Negate Expr Sum Expr Expr", string.Join(" ", rule.Forms.Select(f => f.Symbol)), "a form's production names its nodes");
			T.Check(a.Grammar.GetProduction("Sum")!.OperatorOwner == rule, "Sum belongs to Expr");
			T.Check(a.Messages.Any(m => m.Level == ErrorLevel.Message && m.Message.Contains("\"Sum\" has no semantic action, so \"Expr\"'s action")), "Sum falling back to Expr's action is mentioned");
			T.Check(a.Messages.Any(m => m.Message.Contains("operator rule with 4 levels")), "says how it's parsed");
			T.Equal(ParserEngine.LL1, a.Engine, "arithmetic stays LL(1)");
			T.Check(!a.Messages.Any(m => m.Level == ErrorLevel.Warning), "no warnings");
		}

		public static void PrecedenceAndAssociativity()
		{
			var a = _Analyze(Arithmetic);
			T.Equal("Sum(Expr(1) + Expr(Expr(2) * Expr(3)))", _Interpret(a, "1 + 2 * 3"), "* binds tighter than +");
			T.Equal("Sum(Sum(Expr(10) - Expr(4)) - Expr(3))", _Interpret(a, "10 - 4 - 3"), "left associative");
			T.Equal("Expr(Expr(2) ^ Expr(Expr(3) ^ Expr(2)))", _Interpret(a, "2 ^ 3 ^ 2"), "right associative");
			T.Equal("Negate(- Expr(Expr(2) ^ Expr(2)))", _Interpret(a, "-2^2"), "^ binds tighter than unary minus");
			T.Equal("Expr(Negate(- Expr(2)) * Expr(3))", _Interpret(a, "-2 * 3"), "unary minus binds tighter than *");
			T.Equal("Expr(Expr(2) ^ Negate(- Expr(1)))", _Interpret(a, "2^-1"), "a prefix form as the right operand");
			T.Equal("Expr(Expr(Expr(2) ^ Negate(- Expr(1))) * Expr(3))", _Interpret(a, "2^-1*3"), "the prefix operand stops at a looser operator");
			T.Equal("Expr(Expr(Sum(Expr(1) + Expr(2))) * Expr(3))", _Interpret(a, "(1 + 2) * 3"), "parentheses are an atom");
			T.Equal("Expr(7)", _Interpret(a, "7"), "an atom alone is wrapped in a node");
			T.Equal("Negate(- Negate(- Expr(2)))", _Interpret(a, "--2"), "prefix forms nest");
		}

		public static void EnginesAgreeWithTheInterpreter()
		{
			string[][] inputs =
			{
				new[] { Arithmetic, "1 + 2 * 3", "10 - 4 - 3", "2 ^ 3 ^ 2", "-2^2", "2^-1*3", "(1 + 2) * 3 % 4", "--2 - -3", "1 +", "1 2", "* 3", "(1 + 2" },
				new[] { CLike, "a = b = c", "a ? b : c ? d : e", "!a[0]", "f(1, 2).x", "a + b ? c : d", "a ? b = c : d", "x = !f(a)[b + c] * d", "f()", "a[", "a ? b" },
				new[] { Casts, "(a) b;", "(a) + b;", "(a + b);", "(a) (b) c + d;", "((a)) b;", "(a)(b);" }
			};
			foreach (var set in inputs)
			{
				foreach (var engine in new[] { ParserEngine.Auto, ParserEngine.LLk, ParserEngine.AllStar })
				{
					var a = GrammarAnalysis.Analyze(Grammar.Parse(set[0]), engine);
					if (a.HasErrors)
					{
						// Casts needs more than one token, so it has no LL(1) parser; the others must all build
						T.Check(false, $"{a.Grammar.ClassName} on {engine}: {string.Join("; ", a.Messages.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message))}");
						continue;
					}
					var src = CSharpSynthesizer.Synthesize(a);
					File.WriteAllText(Path.Combine(SynthesisTests.OutDir, $"{a.Grammar.ClassName}.{a.Engine}.cs"), src);
					var p = CompiledParser.Compile(src, a.Grammar.ClassName!);
					T.Equal("", string.Join("\n", p.Diagnostics), $"{a.Grammar.ClassName} on {a.Engine} compiles without warnings");
					var lexer = new RegexLexer(a);
					foreach (var text in set.Skip(1))
					{
						var interp = new GrammarInterpreter(a, lexer.Tokenize(text));
						var expected = interp.Parse().ToString();
						var expectedErrors = string.Join("; ", interp.Errors);
						var (tree, errors) = p.Parse(lexer.Tokenize(text));
						T.Equal(expected, tree, $"{a.Grammar.ClassName} on {a.Engine}: same tree as the interpreter for \"{text}\"");
						T.Equal(expectedErrors, string.Join("; ", errors), $"{a.Grammar.ClassName} on {a.Engine}: same errors as the interpreter for \"{text}\"");
					}
				}
			}
		}

		public static void Evaluates()
		{
			foreach (var engine in new[] { ParserEngine.Auto, ParserEngine.LLk, ParserEngine.AllStar })
			{
				var a = _Analyze(Arithmetic, engine);
				var src = CSharpSynthesizer.Synthesize(a);
				var p = CompiledParser.Compile(src, "OpParser");
				T.Equal("", string.Join("\n", p.Diagnostics), $"parser and evaluator on {a.Engine} compile without warnings");
				var lexer = new RegexLexer(a);
				var cases = new (string Text, double Value)[]
				{
					("1 + 2 * 3", 7), ("10 - 4 - 3", 3), ("2 ^ 3 ^ 2", 512), ("-2^2", -4), ("-2 * 3", -6), ("2^-1", 0.5),
					("2^-1*3", 1.5), ("(1 + 2) * 3", 9), ("7 % 4 + 1", 4), ("2 * 3 ^ 2", 18), ("--2", 2), ("-(1 + 2)", -3),
					("8 / 2 / 2", 2), ("1.5", 1.5)
				};
				foreach (var (text, value) in cases)
					T.Equal((object)value, p.Evaluate("OpEvaluator", lexer.Tokenize(text)), $"{text} on {a.Engine}");
			}
		}

		public static void EvaluatorForwardsToFormsWithActions()
		{
			var src = CSharpSynthesizer.Synthesize(_Analyze(Arithmetic));
			T.Check(src.Contains("case Negate:") && src.Contains("return (double)EvaluateNegate(node);"), "Expr forwards Negate nodes, casting object to double");
			T.Check(src.Contains("protected object EvaluateSum(ParseNode node)") && src.Contains("return (object)EvaluateExpr(node);"), "Sum, with no action and no type, is evaluated by Expr's action");
			T.Check(!src.Contains("ParseNegate") && !src.Contains("ParseSum"), "an operator form's production has no parse method");
			T.Check(src.Contains("ParseNode ParseExpr(int minLevel = 0)"), "the operator rule's method takes the level it starts at");
			T.Check(src.Contains("result = parser.Run(() => parser.ParseExpr());"), "and the start rule is called with the default");
			var all = CSharpSynthesizer.Synthesize(_Analyze(Arithmetic, ParserEngine.AllStar));
			T.Check(all.Contains("ParseNode ParseExpr(int returnState, int minLevel = 0)"), "ALL(*) passes the return state too");
		}

		public static void TypedFormsCast()
		{
			// a relational operator changes the type, so the rule is object and each form says what it returns
			const string xbnf = @"
				@class ""RelParser""; @evaluator ""RelEvaluator"";
				E<start, operators>= Product | Sum | Less | number => { return int.Parse(node.Children[0].Value!); };
				Product<type=""int"">= E star E => { return (int)EvaluateE(node.Children[0]) * (int)EvaluateE(node.Children[2]); };
				Sum<type=""int"">= E plus E => { return (int)EvaluateE(node.Children[0]) + (int)EvaluateE(node.Children[2]); };
				Less<type=""bool"">= E lt E => { return (int)EvaluateE(node.Children[0]) < (int)EvaluateE(node.Children[2]); };
				number= '[0-9]+'; star= ""*""; plus= ""+""; lt= ""<"";" + Ws;
			var a = _Analyze(xbnf);
			var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(a), "RelParser");
			T.Equal("", string.Join("\n", p.Diagnostics), "compiles without warnings");
			var lexer = new RegexLexer(a);
			T.Equal((object)true, p.Evaluate("RelEvaluator", lexer.Tokenize("1 + 2 * 3 < 8")), "forms with their own types");
			T.Equal((object)14, p.Evaluate("RelEvaluator", lexer.Tokenize("2 * 3 + 8")), "an int through object");
			// a form whose type can't convert to the rule's doesn't compile, and the compiler says so
			var bad = CSharpSynthesizer.Synthesize(_Analyze(xbnf.Replace("E<start, operators>", "E<start, type=\"int\", operators>")));
			T.Throws<InvalidOperationException>(() => CompiledParser.Compile(bad, "RelParser"), "bool can't be cast to int");
		}

		public static void CLikeShapes()
		{
			var a = _Analyze(CLike);
			T.Equal(ParserEngine.LL1, a.Engine, "LL(1)");
			T.Equal("E(E(a) = E(E(b) = E(c)))", _Interpret(a, "a = b = c"), "assignment is right associative");
			T.Equal("E(E(a) ? E(b) : E(E(c) ? E(d) : E(e)))", _Interpret(a, "a ? b : c ? d : e"), "so is the ternary");
			T.Equal("E(E(E(a) + E(b)) ? E(c) : E(d))", _Interpret(a, "a + b ? c : d"), "the condition takes tighter operators");
			T.Equal("E(E(a) ? E(E(b) = E(c)) : E(d))", _Interpret(a, "a ? b = c : d"), "the middle operand is parsed from the loosest level");
			T.Equal("E(! E(E(a) [ E(0) ]))", _Interpret(a, "!a[0]"), "postfix binds tighter than prefix");
			T.Equal("E(E(E(f) ( E(1) , E(2) )) . x)", _Interpret(a, "f(1, 2).x"), "postfix forms chain left to right");
			T.Equal("E(E(f) ( ))", _Interpret(a, "f()"), "an optional part of an operator");
			var j = _Analyze("A<start, operators>= A A | neg A | name; neg= \"-\"; name= '[a-z]+';" + Ws);
			T.Equal("A(A(A(f) A(x)) A(y))", _Interpret(j, "f x y"), "juxtaposition is left associative");
			T.Equal("A(A(f) A(- A(x)))", _Interpret(j, "f -x"), "and its right operand can be a prefix form");
		}

		public static void LookaheadInsideOperatorRules()
		{
			var a = _Analyze(Casts);
			T.Equal(ParserEngine.LLk, a.Engine, "a cast needs more than one token");
			T.Equal(4, a.Depth, "four, to see what follows the )");
			T.Equal("S(Cast(( a ) E(b)) ;)", _Interpret(a, "(a) b;"), "a cast");
			T.Equal("S(E(E(( E(a) )) + E(b)) ;)", _Interpret(a, "(a) + b;"), "a parenthesized operand");
			T.Equal("S(E(Cast(( a ) E(b)) + E(c)) ;)", _Interpret(a, "(a) b + c;"), "a cast binds tighter than +, its first alternative");
		}

		public static void Hooks()
		{
			const string xbnf = "E<start, operators, entering=\"In\", parsed=\"Out\">= E plus E | n; n= '[0-9]+'; plus= \"+\";" + Ws;
			var a = _Analyze(xbnf);
			var log = new List<string>();
			var hooks = new ParseHooks { Entering = m => log.Add(m), Parsed = (m, node) => log.Add(m + " " + _Shape(node)) };
			var p = new GrammarInterpreter(a, new RegexLexer(a).Tokenize("1 + 2"), hooks);
			p.Parse();
			T.Equal("In; In; Out E(2); Out E(E(1) + E(2))", string.Join("; ", log), "entering and parsed run once per call, operands included");
		}

		public static void Errors()
		{
			const string tail = " n= '[0-9]+'; plus= \"+\"; minus= \"-\"; dot= \".\"; name= '[a-z]+';";
			T.Check(_Errors("S= E; E<operators, collapsed>= E plus E | n;" + tail).Any(m => m.Contains("can't be collapsed")), "an operator rule can't be collapsed");
			T.Check(_Errors("S= E Sum; E<operators>= Sum | n; Sum= E plus E;" + tail).Any(m => m.Contains("can only be used as an alternative of \"E\"")), "a form's production can't be used elsewhere");
			T.Check(_Errors("E<operators>= S | S | n; S= E plus E;" + tail).Any(m => m.Contains("already an operator form")), "or twice");
			T.Check(_Errors("E<operators>= Sum | n; Sum<collapsed>= E plus E;" + tail).Any(m => m.Contains("\"Sum\" is an operator form of \"E\", so it can't be collapsed")), "a form's production can't be collapsed");
			T.Check(_Errors("E<operators>= E plus<assoc=middle> E | n;" + tail).Any(m => m.Contains("assoc expects")), "assoc is left or right");
			T.Check(_Errors("E<operators>= E plus<assoc=left> E<assoc=right> | n;" + tail).Any(m => m.Contains("both assoc=left and assoc=right")), "a form can't be both");
			T.Check(_Errors("E<operators>= E E | P; P= E dot name | name;" + tail).Any(m => m.Contains("leads back to itself")), "an atom with an operator form among its alternatives leads back too");
			T.Check(_Errors("E<operators>= Sum | n; Sum= E plus E | E minus E;" + tail).Any(m => m.Contains("must be exactly one form")), "a form's production is one form");
			T.Check(_Errors("E<operators>= [ minus ] E | n;" + tail).Any(m => m.Contains("prefix form") && m.Contains("can match nothing")), "a prefix operator can't be empty");
			T.Check(_Errors("E<operators>= E [ dot ] | n;" + tail).Any(m => m.Contains("postfix form") && m.Contains("can match nothing")), "a postfix operator can't be empty");
			T.Check(_Errors("E<operators>= E plus E | [ n ];" + tail).Any(m => m.Contains("the atom") && m.Contains("can match nothing")), "an atom can't be empty");
			T.Check(_Errors("E<operators>= E plus E | minus E;" + tail).Any(m => m.Contains("has no atom")), "a rule needs an atom");
			T.Check(_Errors("E<operators>= E plus E | P; P= E dot name | name;" + tail).Any(m => m.Contains("leads back to itself") && m.Contains("E → P → E")), "an atom can't lead back to the rule at its start");
			T.Check(_Errors("E<operators>= E | n;" + tail).Any(m => m.Contains("derives itself")), "an alternative that is just the rule");
			T.Check(_Messages("E<operators>= minus<assoc=right> E | n;" + tail, ErrorLevel.Warning).Any(m => m.Contains("only infix forms associate")), "assoc on a prefix form is ignored");
			T.Check(_Messages("E= n<assoc=left>;" + tail, ErrorLevel.Warning).Any(m => m.Contains("assoc is ignored")), "assoc outside an operator rule is ignored");
			T.Check(_Messages("E<operators>= n | name;" + tail, ErrorLevel.Warning).Any(m => m.Contains("none of its alternatives is an operator form")), "operators with no operator forms");
			T.Check(_Messages("E<operators>= S | n; S<parsed=\"P\">= E plus E;" + tail, ErrorLevel.Warning).Any(m => m.Contains("its parsed is ignored")), "hooks on a form's production are ignored");
			T.Equal(0, _Errors("E<operators>= E E | n;" + tail).Length, "juxtaposition: an infix form with no operator");
		}

		public static void ErrorMessagesNameTheOperand()
		{
			var a = _Analyze(Arithmetic);
			_Interpret(a, "1 + * 2", out var errors);
			T.Equal("Expected an expression but found \"*\" at line 1, column 5", string.Join("; ", errors), "expected names the operand");
		}

		public static void Report()
		{
			var report = GrammarReport.Create(_Analyze(Arithmetic));
			T.Check(report.Contains("LL(1)*  yes"), "the operator loop isn't reported as settled by a default policy");
			T.Check(report.Contains("ALL(*)  yes        every decision on one token; nothing adaptive"), "and the grammar re-analyzed for other engines is the same grammar");
			T.Check(report.Contains("1 operator rule, 2 operator forms"), "operator rules and forms are counted");
			T.Check(report.Contains("Negate  operator form of Expr, action"), "a form's production is listed as one");
			T.Check(report.Contains("1 production method,"), "and has no parse method");
			T.Check(!report.Contains("unreachable") && !report.Contains("nullable"), "nor is it unreachable or nullable");
		}

		public static void AnalyzedGrammarsAnalyzeAgain()
		{
			var a = _Analyze(Arithmetic);
			var again = GrammarAnalysis.Analyze(a.Grammar.Clone());
			T.Equal("", string.Join("; ", again.Messages.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message)), "no errors the second time");
			T.Equal(_Interpret(a, "-2^2 + 1 * 3"), _Interpret(again, "-2^2 + 1 * 3"), "the same parser");
			T.Equal("Sum= Expr ( add | sub ) Expr;", a.Grammar.GetProduction("Sum")!.ToString(), "an analyzed grammar writes its forms as written");
		}

		public static void SampleGrammar()
		{
			var path = Path.Combine(T.Dir, "ExprOperators.xbnf");
			var a = GrammarAnalysis.Analyze(Grammar.ReadFrom(path));
			GrammarException.ThrowIfErrors(a.Messages);
			var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(a), "ExprParser");
			T.Equal("", string.Join("\n", p.Diagnostics), "the sample compiles without warnings");
			var lexer = new RegexLexer(a);
			T.Equal((object)(-4.0), p.Evaluate("ExprEvaluator", lexer.Tokenize("-2^2")), "-2^2");
			T.Equal((object)1.5, p.Evaluate("ExprEvaluator", lexer.Tokenize("2^-1*3")), "2^-1*3");
			T.Equal((object)3.0, p.Evaluate("ExprEvaluator", lexer.Tokenize("10 - 4 - 3")), "10 - 4 - 3");
			// the trees XBNF.md shows
			T.Equal("Expr(Expr(1) + Expr(Expr(2) * Expr(3)))", _Interpret(a, "1 + 2 * 3"), "1 + 2 * 3");
			T.Equal("Expr(- Expr(Expr(2) ^ Expr(2)))", _Interpret(a, "-2^2"), "-2^2");
			T.Equal("Expr(Expr(Expr(Expr(1) + Expr(2))) * Expr(3))", _Interpret(a, "(1 + 2) * 3"), "(1 + 2) * 3");
			T.Equal("Expr(7)", _Interpret(a, "7"), "7");
		}
	}
}
