using System;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	/// <summary>
	/// The C99 grammar end to end: analysis at the default lookahead, synthesis, compiling with the typedef
	/// hooks in C99Parser.Typedefs.cs, and parsing C that depends on telling typedef names apart
	/// </summary>
	public static class C99Tests
	{
		static string Dir => Path.Combine(T.Dir, "c99");

		public static void ParsesCWithTypedefTracking()
		{
			var started = DateTime.UtcNow;
			var a = GrammarAnalysis.Analyze(Grammar.ReadFrom(Path.Combine(Dir, "c99.xbnf")));
			T.Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "analysis at k=4 finishes promptly");
			T.Equal("", string.Join("; ", a.Messages.Where(m => m.Level != ErrorLevel.Message)), "no errors or warnings");
			T.Equal(ParserEngine.AllStar, a.Engine, "C needs ALL(*)");
			var src = CSharpSynthesizer.Synthesize(a);
			File.WriteAllText(Path.Combine(SynthesisTests.OutDir, "C99Parser.cs"), src);
			var p = CompiledParser.Compile(src, "C99Parser", File.ReadAllText(Path.Combine(Dir, "C99Parser.Typedefs.cs")));
			T.Equal("", string.Join("\n", p.Diagnostics), "compiles cleanly with its hooks");
			var lexer = new RegexLexer(a);
			foreach (var file in new[] { "sample.c", "typedefs.c" })
			{
				var (tree, errors) = p.Parse(lexer.Tokenize(File.ReadAllText(Path.Combine(Dir, file))).ToList());
				T.Equal("", string.Join("; ", errors), file + " parses");
				if ("typedefs.c" != file) continue;
				// each statement and declaration, as its kind and the text of its tokens
				var lines = tree.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => 0 < l.Trim().Length).ToList();
				string text(int i)
				{
					var depth = lines[i].Length - lines[i].TrimStart().Length;
					var words = new System.Collections.Generic.List<string>();
					for (var j = i + 1; j < lines.Count && lines[j].Length - lines[j].TrimStart().Length > depth; ++j)
					{
						var q = lines[j].IndexOf('"');
						if (-1 < q) words.Add(lines[j].Substring(q + 1, lines[j].Length - q - 2));
					}
					return string.Join(" ", words);
				}
				var items = Enumerable.Range(0, lines.Count).Where(i => lines[i].Trim() is "declaration" or "expression_statement")
					.Select(i => lines[i].Trim() + ": " + text(i)).ToList();
				T.Check(items.Contains("declaration: long T = 2"), "an inner declaration hides the typedef T");
				T.Check(items.Contains("expression_statement: T * a"), "so T * a multiplies there");
				T.Check(items.Contains("declaration: T * b = & a"), "while outside it declares");
				T.Check(items.Contains("declaration: T c = ( T ) * b + sizeof ( T ) + sizeof ( a )"), "and T is a type again after the block");
				T.Check(tree.Contains("typedef_name \"node\"") && tree.Contains("identifier \"node\""), "node is a type, and a struct tag");
				T.Check(tree.Contains("identifier \"size_t_field\""), "a member declared after a typedef name");
				T.Check(lines.Count(l => l.Trim() == "typedef_name \"T\"") >= 8, "T read as a type wherever it is one");
			}
		}
	}
}
