// Parses each file given and reports OK or its errors. -t prints the parse tree of each file; -k lists
// where each regular expression literal and / is, one per line, to compare with another tokenizer.
using System.Diagnostics;
using Parsley.Runtime;

// the tokenizer hands the parser lexer ids, and its id for a regular expression literal has to be the
// grammar's, counted from the first terminal (break)
if (ES5Tokenizer.RegularExpressionLiteral != ES5Parser.regular_expression_literal - ES5Parser.@break)
	throw new InvalidOperationException("regular_expression_literal isn't the last terminal in es5.xbnf");

var printTree = args.Contains("-t");
// -k: print where each regular expression literal and / or /= is, to compare with another tokenizer
if (args.Contains("-k"))
{
	foreach (var file in args.Where(a => !a.StartsWith('-')))
		foreach (var t in ES5Tokenizer.Tokenize(File.ReadAllText(file)))
			if (t.SymbolId is ES5Tokenizer.RegularExpressionLiteral or ES5Lexer.slash or ES5Lexer.div_assign)
				Console.WriteLine($"{file} {t.Position} {(t.SymbolId == ES5Tokenizer.RegularExpressionLiteral ? "regex" : "div")}");
	return 0;
}
var failed = 0;
foreach (var file in args.Where(a => !a.StartsWith('-')))
{
	var text = File.ReadAllText(file);
	var sw = Stopwatch.StartNew();
	try
	{
		var tree = ES5Parser.Parse(ES5Tokenizer.Tokenize(text));
		sw.Stop();
		Console.WriteLine($"OK    {file} ({text.Length:N0} chars, {sw.ElapsedMilliseconds} ms)");
		if (printTree) Console.WriteLine(tree);
	}
	catch (ParseException ex)
	{
		++failed;
		Console.WriteLine($"FAIL  {file}");
		foreach (var e in ex.Errors.Take(5)) Console.WriteLine($"      {e}");
	}
}
return failed;
