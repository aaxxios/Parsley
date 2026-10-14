
using Parsley.Runtime;
namespace JsonTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var tree = JsonParser.Parse("{\"name\": \"John\", \"age\": 30, \"isStudent\": false, \"emails\": [ \"john@mydomain.com\", \"support@widgetco.com\" ] }");

                Console.WriteLine(tree);
                var json = JsonParser.Evaluate(tree);
                Console.WriteLine();
                JsonParser.WriteTo(json, Console.Out);
            }
            catch (ParseException e)
            {
                foreach(var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
            }
        }
    }
}
