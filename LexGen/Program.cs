using System.Text;
namespace LexGen
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Options? toDispose = null;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;

                if (!Options.TryCreate(args, out var options, out var error))
                {
                    if (!error!.IsHelpRequest)
                    {
                        Console.Error.WriteLine("Error: " + error.Message);
                        Console.Error.WriteLine();
                    }
                    Options.PrintUsage();
                    return;
                }
                toDispose = options;
                var rules = LexerEmitter.ReadRules(options.Input);
                LexerEmitter.Emit(rules, options.Output, new LexerEmitOptions
                {
                    Namespace = options.Namespace,
                    ClassName = options.Class ?? CliUtility.GetFilename(options.Input) ?? "Lexer",
                    Access = options.Public ? "public" : null,
                    Unicode = options.Unicode,
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                toDispose?.Dispose();
            }
        }
    }
}
