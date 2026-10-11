using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsley.Runtime;

namespace Parsley.Tests
{
	/// <summary>
	/// Contextual terminals (when) and parse-time hooks (entering, parsed, matched)
	/// </summary>
	public static class HookTests
	{
		// a scrap of C: whether "t * x;" declares x or multiplies depends on whether t was typedef'd
		const string Mini = @"
			@class ""MiniParser"";
			Program<entering=""Begin"">= { Item };
			Item<collapsed>= Typedef | Declaration | Expression | Paren;
			Typedef<parsed=""OnTypedef"">= ""typedef"" type identifier "";"";
			Declaration= type [ ""*"" ] identifier "";"";
			type<collapsed>= ""int"" | typedef_name;
			Expression= identifier { ""*"" identifier } "";"";
			// a cast or a parenthesized name: two tokens to tell, so ALL(*) looks at the typedef name ahead
			Paren= ""("" type "")"" identifier "";"" | ""("" identifier "")"" "";"";
			typedef= ""typedef"";
			int= ""int"";
			identifier<matched=""OnIdentifier"">= '[a-z_]+';
			typedef_name<when=""IsTypedefName"">= identifier;
			ws<hidden>= '[ \t\r\n]+';";

		// the other half of the partial class, as a user would write it
		const string MiniHooks = @"
			using System.Collections.Generic;
			using System.Linq;
			using Parsley.Runtime;
			public partial class MiniParser
			{
				public static List<string> Log = new();
				readonly HashSet<string> _types = new();
				partial void Begin() => Log.Add(""begin"");
				private partial bool IsTypedefName(Token token, Token previous) => _types.Contains(token.Value);
				partial void OnIdentifier(Token token) => Log.Add(""id "" + token.Value);
				partial void OnTypedef(ParseNode node)
				{
					var name = node.Children.Last(c => ""identifier"" == c.Symbol).Value!;
					_types.Add(name);
					Log.Add(""typedef "" + name);
				}
			}";

		static ParseHooks _InterpreterHooks(List<string> log)
		{
			var types = new HashSet<string>();
			return new ParseHooks
			{
				Entering = m => log.Add("begin"),
				When = (m, token, previous) => types.Contains(token.Value),
				Matched = (m, token) => log.Add("id " + token.Value),
				Parsed = (m, node) =>
				{
					var name = node.Children.Last(c => "identifier" == c.Symbol).Value!;
					types.Add(name);
					log.Add("typedef " + name);
				}
			};
		}

		public static void ContextualTerminalIsReadByTheParser()
		{
			var a = GrammarAnalysis.Analyze(Grammar.Parse(Mini, "mini.xbnf"));
			GrammarException.ThrowIfErrors(a.Messages);
			var t = a.Grammar.GetTerminal("typedef_name")!;
			T.Check(t.IsContextual && "identifier" == t.ContextualBase && "IsTypedefName" == t.When, "typedef_name is a contextual terminal read from identifier");
			T.Equal(null, t.Regex, "with no regex: the lexer never produces it");
			T.Equal(a.Symbols.EndOfInput - 1, a.Symbols.GetId("typedef_name"), "after every lexed terminal, so lexer ids don't count it");
			T.Check(!new RegexLexer(a).Tokenize("t").Any(x => x.SymbolId + a.Symbols.NonTerminalCount == a.Symbols.GetId("typedef_name")), "the lexer reports identifier");
			var g2 = Grammar.Parse(Grammar.Parse(Mini, "mini.xbnf").ToString());
			T.Check(g2.GetTerminal("typedef_name")?.IsContextual ?? false, "survives writing and reading back");
		}

		public static void HooksDriveTheParse()
		{
			const string text = "typedef int t; t * x; y * z; (t) w; (y);";
			foreach (var engine in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
			{
				var a = GrammarAnalysis.Analyze(Grammar.Parse(Mini, "mini.xbnf"), engine);
				if (ParserEngine.LL1 == engine)
				{
					// Paren needs two tokens; LL(1) settles it by policy... so check the others only
					T.Check(a.HasErrors, "LL(1) can't do the Paren alternatives");
					continue;
				}
				GrammarException.ThrowIfErrors(a.Messages);
				var src = CSharpSynthesizer.Synthesize(a);
				File.WriteAllText(Path.Combine(SynthesisTests.OutDir, $"MiniParser.{engine}.cs"), src);
				T.Check(src.Contains("private partial bool IsTypedefName(Token token, Token previous);") && src.Contains("partial void OnTypedef(ParseNode node);")
					&& src.Contains("partial void Begin();") && src.Contains("partial void OnIdentifier(Token token);"), $"{engine}: partial methods declared");
				T.Check(src.Contains("protected override int Classify(Token token, Token previous)") && src.Contains("contextualTerminals: [TypedefName]"), $"{engine}: classification wired up");
				var p = CompiledParser.Compile(src, "MiniParser", MiniHooks);
				T.Equal("", string.Join("\n", p.Diagnostics), $"{engine}: compiles cleanly with the user's half");
				var log = (List<string>)p.GetEvaluator("MiniParser").GetField("Log")!.GetValue(null)!;
				log.Clear();
				var tokens = new RegexLexer(a).Tokenize(text).ToList();
				var (tree, errors) = p.Parse(tokens);
				T.Equal(0, errors.Count, $"{engine}: parses: " + string.Join("; ", errors));
				var lines = tree.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
				T.Check(lines.Contains("  Declaration") && lines.Contains("    typedef_name \"t\""), $"{engine}: t * x declares x, t read as a type");
				T.Equal(1, lines.Count(l => l == "  Expression"), $"{engine}: y * z multiplies");
				T.Equal(2, lines.Count(l => l == "  Paren"), $"{engine}: both parentheses parse");
				T.Equal("begin|id t|typedef t|id x|id y|id z|id w|id y", string.Join("|", log), $"{engine}: hooks ran in order, and a typedef name isn't an identifier match");
				var ilog = new List<string>();
				var interp = new GrammarInterpreter(a, tokens, _InterpreterHooks(ilog));
				var itree = interp.Parse();
				T.Equal(tree, itree.ToString(), $"{engine}: the interpreter builds the same tree");
				T.Equal(string.Join("|", log), string.Join("|", ilog), $"{engine}: and calls the same hooks");
			}
		}

		public static void HooksAreOptionalButPredicatesAreNot()
		{
			// a parsed or entering or matched hook nobody implements simply does nothing; a when predicate must exist
			var a = GrammarAnalysis.Analyze(Grammar.Parse(Mini, "mini.xbnf"), ParserEngine.LLk);
			var src = CSharpSynthesizer.Synthesize(a);
			T.Throws<InvalidOperationException>(() => CompiledParser.Compile(src, "MiniParser"), "without IsTypedefName the parser doesn't compile");
			var noWhen = Mini.Replace("typedef_name<when=\"IsTypedefName\">= identifier;", "").Replace(" | typedef_name", "");
			var b = GrammarAnalysis.Analyze(Grammar.Parse(noWhen, "mini.xbnf"), ParserEngine.LLk);
			GrammarException.ThrowIfErrors(b.Messages);
			var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(b), "MiniParser");
			T.Equal("", string.Join("\n", p.Diagnostics), "unimplemented void hooks compile cleanly");
			T.Equal(0, p.Parse(new RegexLexer(b).Tokenize("int x; y * z;").ToList()).Errors.Count, "and parse");
		}

		public static void HookErrors()
		{
			static string[] errors(string xbnf)
			{
				var msgs = XbnfReader.TryParse(xbnf, null, out var g).ToList();
				if (!msgs.Any(m => m.Level == ErrorLevel.Error)) msgs.AddRange(GrammarAnalysis.Analyze(g!).Messages);
				return msgs.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message).ToArray();
			}
			const string tail = " id= '[a-z]+'; ws<hidden>= ' +';";
			T.Check(errors("S= tn; tn<when=\"IsT\">= id id;" + tail).Any(m => m.Contains("exactly one terminal")), "when needs exactly one terminal");
			T.Check(errors("S= tn; tn<when=\"IsT\">= ws;" + tail).Any(m => m.Contains("hidden")), "not a hidden one");
			T.Check(errors("S= tn; tn<when=\"is t\">= id;" + tail).Any(m => m.Contains("name of a C# method")), "when names a method");
			T.Check(errors("S<parsed=5>= id;" + tail).Any(m => m.Contains("name of a C# method")), "parsed names a method");
			T.Check(errors("S<matched=\"M\">= id;" + tail).Any(m => m.Contains("matched goes on a terminal")), "matched is for terminals");
			T.Check(errors("S= x; x<terminal>= tn \"a\"; tn<when=\"IsT\">= id;" + tail).Any(m => m.Contains("contextual and has no regex")), "a contextual terminal can't be inlined into another");
			T.Equal(0, errors("S<entering=\"A\", parsed=\"B\">= tn | id; tn<when=\"IsT\">= id;" + tail).Length, "all fine together");
		}
	}
}
