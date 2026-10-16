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
                if (options.Report)
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
                // symbol constants that can't have their symbols' names, which only synthesis decides
                var naming = analysis.HasErrors ? Array.Empty<GrammarMessage>() : CSharpSynthesizer.CheckNames(analysis);
                foreach (var m in naming) Console.Error.WriteLine(m);
                if (options.Report)
                {
                    if (analysis.Messages.Count + naming.Count > 0)
                    {
                        Console.Error.WriteLine();
                    }
                    GrammarReport.Write(analysis, Console.Out);
                    return;
                }
                // #line directives name the grammar files relative to the code file, so the code is the same on any
                // machine; to stdout, relative to the current directory, where the output is usually redirected
                var outFile = CliUtility.GetFilename(options.Output);
                var outDir = Path.GetFullPath(null != outFile ? Path.GetDirectoryName(Path.GetFullPath(outFile)) ?? "." : ".");
                // -n: no #line directives at all; the synthesizer writes none for an empty path
                options.Output.Write(CSharpSynthesizer.Synthesize(analysis, new SynthesisOptions
                {
                    LinePath = options.NoLines ? _ => "" : file => Path.GetRelativePath(outDir, Path.GetFullPath(file))
                }));
                // -s writes everything shared; -b only the base class, for a project that already has the rest
                if (options.Shared || options.Base)
                {
                    var dir = "";
                    var fn = CliUtility.GetFilename(options.Input);
                    if (fn != null)
                    {
                        dir = Path.GetDirectoryName(fn) ?? dir;
                    }
                    string inDir(string name) => dir.Length > 0 ? Path.Combine(dir, name) : name;
                    if (options.Shared)
                        File.WriteAllText(inDir(CSharpSynthesizer.RuntimeFileName), CSharpSynthesizer.RuntimeSource);
                    File.WriteAllText(inDir(CSharpSynthesizer.BaseFileName(analysis.Engine)), CSharpSynthesizer.BaseSource(analysis.Engine));
                }

                if (options.Lexer != null)
                {
                    // from the analyzed grammar: its terminals are in the order the parser numbers them, and each
                    // has its regex, including those built from alternatives or other terminals
                    void write(TextWriter w, IEnumerable<TerminalDeclaration> terms)
                    {
                        foreach (var term in terms)
                        {
                            if (term.IsHidden) w.Write('.');
                            w.Write(term.Name);
                            w.Write(' ');
                            w.WriteLine(term.Regex);
                        }
                    }
                    // the lexer never produces a contextual terminal, which the parser reads from another one, an
                    // island, which the lexer controller makes, or a terminal another lexer produces
                    write(options.Lexer, analysis.Grammar.Terminals.Where(t => t.IsLexed));
                    // each lexer terminals name with lexer= gets a file of its own beside this one: es5.lex, es5.Regex.lex
                    var groups = analysis.Grammar.LexerGroups;
                    if (0 < groups.Count)
                    {
                        var lexFile = CliUtility.GetFilename(options.Lexer);
                        if (null == lexFile)
                            throw new ArgumentException("The grammar has terminals from other lexers, which are written to files beside the lexer file, so --lexer must name a file.");
                        foreach (var alias in groups)
                        {
                            var path = Path.Combine(Path.GetDirectoryName(lexFile) ?? "", Path.GetFileNameWithoutExtension(lexFile) + "." + alias + Path.GetExtension(lexFile));
                            using var w = new StreamWriter(path);
                            write(w, analysis.Grammar.Terminals.Where(t => t.Lexer == alias && !t.IsIsland));
                        }
                    }
                }
            }
            catch (ArgumentException argex)
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