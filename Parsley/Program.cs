using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Parsley
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
                if(options.Report)
                {
                    if (options.Base)
                    {
                        throw new ArgumentException("The --report option and --base option are mutually exclusive.");
                    }
                    if (options.Output != Console.Out)
                    {
                        throw new ArgumentException("The --report option and --output option are mutually exclusive.");
                    }
                    if (options.Lexer != null)
                    {
                        throw new ArgumentException("The --report option and --lexer option are mutually exclusive.");
                    }
                    if (options.Shared)
                    {
                        throw new ArgumentException("The --report option and --shared option are mutually exclusive.");
                    }
                }
                var grammar = Grammar.ReadFrom(options.Input, CliUtility.GetFilename(options.Input));
                var analysis = GrammarAnalysis.Analyze(grammar, options.Engine);
                foreach (var m in analysis.Messages) Console.Error.WriteLine(m);
                if (!options.Report)
                {
                    options.Output.Write(CSharpSynthesizer.Synthesize(analysis));
                } else
                {
                    if(analysis.Messages.Count>0)
                    {
                        Console.Error.WriteLine();
                    }
                    GrammarReport.Write(analysis, Console.Out);
                    return;
                }
                // -s writes everything shared; -b only the base class, for a project that already has the rest
                if(options.Shared || options.Base)
                {
                    var dir = "";
                    var fn = CliUtility.GetFilename(options.Input);
                    if(fn!=null)
                    {
                        dir = Path.GetDirectoryName(fn) ?? dir;
                    }
                    string inDir(string name) => dir.Length>0?Path.Combine(dir, name):name;
                    if (options.Shared)
                        File.WriteAllText(inDir(CSharpSynthesizer.RuntimeFileName), CSharpSynthesizer.RuntimeSource);
                    File.WriteAllText(inDir(CSharpSynthesizer.BaseFileName(analysis.Engine)), CSharpSynthesizer.BaseSource(analysis.Engine));
                }
                
                if (options.Lexer != null)
                {
                    for (var i = 0; i < grammar.Terminals.Count; i++)
                    {
                        var term = grammar.Terminals[i];
                        var isHidden = (bool)(term.Attributes.FirstOrDefault(p => p.Name.Equals("hidden", StringComparison.Ordinal) && p.Value is bool b && b)?.Value ?? false);
                        if (isHidden) options.Lexer.Write('.');
                        options.Lexer.Write(term.Name);
                        options.Lexer.Write(' ');
                        options.Lexer.WriteLine(Grammar.EscapeLiteralOrPattern(term.Literal, term.Pattern));
                    }
                }
            }
            catch(ArgumentException argex)
            {
                Console.Error.WriteLine("Error: " + argex.Message);
                Console.Error.WriteLine();
                Options.PrintUsage();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return;
            }
            finally
            {
                toDispose?.Dispose();
            }
        }
    }
}
