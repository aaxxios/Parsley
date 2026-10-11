using Parsley.Runtime;
using Expr;
namespace ExprTest;

internal class Program
{
    static void Main(string[] args)
    {
        var text = "1+(2^4)*3";
        var lexer = ExprLexer.Tokenize(text);
        try
        {
            var tree = ExprParser.Parse(lexer);
            Console.WriteLine(tree);
            Console.WriteLine($"{text} = {ExprEvaluator.Evaluate(tree)}");
        }
        catch (ParseException e)
        {
            foreach (var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
        }
    }
}
