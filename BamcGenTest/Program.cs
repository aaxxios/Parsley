using Parsley.Runtime;
namespace BamcGenTest;

// bamc.xbnf embeds json.xbnf, so this one class gets both grammars: BamcParser.Parser and BamcParser.Lexer for
// bamc, and BamcParser.Json.Parser and BamcParser.Json.Lexer for the islands. Tokenize lexes through a
// LexerController that switches between the two lexers.
[Parsley.Parser("bamc.xbnf")]
internal partial class BamcParser
{
}

internal class Program
{
    static int Main(string[] args)
    {
        var files = 0 < args.Length ? args : new[] { Path.Combine(AppContext.BaseDirectory, "examples", "example.bamc"), Path.Combine(AppContext.BaseDirectory, "examples", "broken.bamc") };
        foreach (var file in files)
        {
            Console.WriteLine(file);
            using var reader = new StreamReader(file, true);
            if (BamcParser.TryParse(BamcParser.Tokenize(reader), out var tree, out var errors))
            {
                Console.WriteLine(tree);
                // an island's node holds the tree json.xbnf parsed, in its symbol ids, which BamcParser.Json has
                foreach (var headers in _Find(tree, BamcParser.Headers))
                {
                    var obj = headers.Children[0];
                    Console.WriteLine($"Headers: {obj.Symbol} with {obj.Children.Count(c => BamcParser.Json.Field == c.SymbolId)} fields");
                }
            }
            else
                foreach (var error in errors) Console.WriteLine($"ERROR: {error}");
        }
        return 0;
    }

    static IEnumerable<ParseNode> _Find(ParseNode node, int symbolId)
    {
        if (node.SymbolId == symbolId) yield return node;
        // an island's tree uses the other grammar's ids, so the search doesn't go into it
        if (BamcParser.Headers == node.SymbolId) yield break;
        foreach (var c in node.Children)
            foreach (var n in _Find(c, symbolId))
                yield return n;
    }
}
