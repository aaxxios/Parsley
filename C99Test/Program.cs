using Parsley.Runtime;

namespace C99Test;

[Parsley.Parser("c99.xbnf")]
partial class C99Parser
{

}

internal class Program
{
    static void Main(string[] args)
    {
        var file = args[0];
        try
        {
            using var reader = new StreamReader(file, true);
            var tree = C99Parser.Parse(reader);
            Console.WriteLine(tree);
        }
        catch(ParseException ex)
        {
            foreach(var error in ex.Errors)
            {
                Console.WriteLine($"ERROR: {error}");
            }
        }
    }
}
