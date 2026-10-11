using Cli;

using System;
using System.IO;
namespace Parsley;

[CliArgs]
internal partial class Options
{
    [CmdArg(0, Description = "The path to the XBNF grammar input file.", ValueName = "grammar-file", Required = false)]
    public TextReader Input { get; set; } = Console.In;
    [CmdArg(ShortName = 'l', Description = "The lexer output file to generate.", ValueName = "lexer-file", Required = false)]
    public TextWriter? Lexer = null;
    [CmdArg(ShortName = 'o', Description = "The path to the ouput code file.", ValueName = "code-file", Required = false)]
    public TextWriter Output { get; set; } = Console.Out;
    [CmdArg(ShortName = 's', Description = "Generate the shared runtime: ParsleyRuntime.cs and the parser base class for the grammar's engine.", ValueName = "shared", Required = false)]
    public bool Shared = false;
    [CmdArg(ShortName = 'b', Description = "Generate only the parser base class for the grammar's engine (LL1ParserBase.cs, LLkParserBase.cs or AllStarParserBase.cs), for a project that already has ParsleyRuntime.cs.", ValueName = "base", Required = false)]
    public bool Base = false;
    [CmdArg(ShortName = 'e', Description = "The parsing engine: auto (the lightest the grammar allows), ll1, llk or allstar. Defaults to auto.", ValueName = "engine", Required = false)]
    public ParserEngine Engine = ParserEngine.Auto;
    [CmdArg(ShortName = 'r', Description = "Write a report on the grammar to <stdout> instead of a parser: the engine it needs and why, its decisions and lookahead, and the size of the synthesized code.", ValueName = "report", Required = false)]
    public bool Report = false;

}
