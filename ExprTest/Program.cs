namespace ExprTest;

[Parsley.Parser("Expr.xbnf")]
internal partial class ExprParser
{

}
internal class Program
{
    static void Main(string[] args)
    {
        var text = "1+(2^4)*pi";
        try
        {
            var tree = ExprParser.Parse(text);
            Console.WriteLine(tree);
            Console.WriteLine($"{text} = {ExprParser.Evaluate(tree,new Dictionary<string, double>() { ["pi"] = 3.14})}");
        }
        catch (Parsley.Runtime.ParseException e)
        {
            foreach (var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
        }
    }
}
