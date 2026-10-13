using Parsley.Runtime;
namespace BamcTest;

internal class Program
{
    static void Main(string[] args)
    {
        // BamcParser lexes through a LexerController: BamcLexer for the script, and JsonLexer from just after
        // add-headers to the } that closes its object, which JsonParser parses as an island
        using var reader = new StreamReader(args[0], true);
        try
        {
            var tree = BamcParser.Parse(reader);
            Console.WriteLine(tree);
        }
        catch (ParseException e)
        {
            foreach (var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
        }
    }
}
