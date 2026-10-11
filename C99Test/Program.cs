

using Parsley.Runtime;

namespace C99Test
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var file = args[0];
            using var reader = new StreamReader(file,true);
            var tokens = C99Lexer.Tokenize(reader);
            try
            {
                var tree = C99Parser.Parse(tokens);
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
}
