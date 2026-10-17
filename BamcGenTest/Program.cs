using System.Text;
using System.Globalization;
using Parsley.Runtime;
namespace BamcTest;

internal class Program
{
    static int Main(string[] args)
    {
        var files = 0 < args.Length ? args : new[] { Path.Combine(AppContext.BaseDirectory,  Path.Combine(AppContext.BaseDirectory, "examples", "example.bamc"))};
        foreach (var file in files)
        {
            Console.WriteLine(file);
            using var reader = new StreamReader(file, true);
            if (BamcParser.TryParse(BamcParser.Tokenize(reader), out var tree, out var errors))
            {                
                var commands = BamcParser.Evaluate(tree);
                Console.WriteLine();
            }
            else
                foreach (var error in errors) Console.WriteLine($"ERROR: {error}");
        }
        return 0;
    }

   
}
