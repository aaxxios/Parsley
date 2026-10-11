using Parsley.Runtime;
namespace BamcTest;

internal class Program
{
    static void Main(string[] args)
    {
        using var reader = new StreamReader(args[0], true);
        var lexer = BamcLexer.Tokenize(reader);
        try
        {
            var tree = BamcParser.Parse(lexer);
            Console.WriteLine(tree);
        }
        catch (ParseException e)
        {
            foreach (var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
        }
    }
}
