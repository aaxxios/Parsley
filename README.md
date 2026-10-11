# Parsley

A parser generator utilizing code synthesis. It looks at what each grammar actually needs and synthesizes the lightest parser that can do it: LL(1), LL(k), or ALL(\*).

## Use

```
parsley

Usage: parsley [<grammar-file>] [--lexer <lexer-file>] [--output <code-file>] [--shared] [--base] [--engine <engine>]

    <grammar-file>            The path to the XBNF grammar input file. Defaults to <stdin>.
    -l, --lexer <lexer-file>  The lexer output file to generate.
    -o, --output <code-file>  The path to the ouput code file. Defaults to <stdout>.
    -s, --shared              Generate the shared runtime: ParsleyRuntime.cs and the parser base class for the grammar's engine.
    -b, --base                Generate only the parser base class for the grammar's engine.
    -e, --engine <engine>     auto (the default), ll1, llk or allstar.
```

- The grammar file is in XBNF, a superset of EBNF with some extra convenience operators and attributes and semantic actions (potentially C# code in the grammar to create an evaluator)
- The lexer file is generated if specified and contains 1 rule per line, `<name>` `<pattern>` (separated by a space) and hidden terminal names are preceded with `.` as in `.whitespace`,
- The output code file is a C# class that derives from the base class for its engine (`LL1ParserBase`, `LLkParserBase` or `AllStarParserBase`) and has a `Parse()` method returning a `ParseNode`. It may also generate an evaluator class with an `Evaluate()` method if semantic actions are present.
- The shared runtime file is `ParsleyRuntime.cs`, which contains `Token`, `LookAheadEnumerator`, `ParseNode`, `ParseError`, `ParserTables` and the abstract `ParserBase` that holds what every engine shares (tree building, error reporting and recovery). Each engine's base class is its own file: `LL1ParserBase.cs`, `LLkParserBase.cs` or `AllStarParserBase.cs`.
- `-s` writes `ParsleyRuntime.cs` and the engine's base class file. `-b` writes only the base class file, for a project that already has `ParsleyRuntime.cs` from another parser. A project with parsers on different engines needs `-s` once, then `-b` for each engine it adds. Both write next to the grammar file, or in the current directory if the grammar comes from stdin.

## Engines

With `-e auto` (the default), Parsley picks the lightest engine the grammar allows:

| Engine | Chosen when | Base class | What it costs |
| --- | --- | --- | --- |
| LL(1) | Every decision is made by the current token | `LL1ParserBase` | Nothing: tokens are read straight from the lexer, no lookahead buffer |
| LL(k) | Every decision is settled within 4 tokens | `LLkParserBase` | A small lookahead buffer, and a synthesized predictor method per decision that needs more than one token |
| ALL(\*) | Some decision can't be settled by any fixed number of tokens | `AllStarParserBase` | The grammar's transition network as a table, and adaptive prediction at parse time for decisions that need more than one token |

All three produce the same tree and the same errors for any grammar they can all parse, and evaluators work the same way on every engine. In an ALL(\*) parser, one-token decisions are still plain `switch` and `if` tests; only the decisions that need more lookahead call `AdaptivePredict`. That simulates the grammar over as much input as it takes. It ignores the calling context first, and caches the result per decision, so after warm-up most predictions are a few dictionary lookups. It retries with the parser's real call stack only when that context-free pass is inconclusive.

A decision that is really ambiguous, where the same input fits two branches all the way through, comes out the way the default policies say: the first alternative wins, and a loop or optional keeps matching. A `<policy>` on the decision settles it when the grammar is analyzed instead, on every engine, so a construct like the dangling `else` can stay LL(1). See [Ambiguity](XBNF.md#ambiguity).

`-e ll1`, `-e llk` and `-e allstar` force an engine. LL(1) and LL(k) then settle what they can't decide the way Parsley always did: by policy, with an error for an alternation that has none. Forcing ALL(\*) on an LL(k) grammar makes every multi-token decision adaptive.

In code, `GrammarAnalysis.Analyze(grammar, ParserEngine.Auto)` makes the choice and reports it in `analysis.Engine`. `CSharpSynthesizer.RuntimeSource`, `BaseSource(engine)` and `BaseFileName(engine)` give you the runtime files.

The documentation for XBNF is [here](XBNF.md).

The synthesized class plus `ParsleyRuntime.cs` and its base class file compile on their own (C# 12, .NET 8), with no reference to Parsley:

```csharp
try 
{
    var tree = JsonParser.Parse(tokens);   // IEnumerable<Token> straight from the lexer
    Console.WriteLine(tree);
}
catch(Parsley.Runtime.ParseException ex) {
    foreach (var e in ex.Errors) Console.WriteLine(e);
}
```

Token ids: the lexer numbers terminals from 0 in the grammar's terminal order (hidden and implicit ones
included, implicit ones last), and the parser adds `ParserTables.TerminalStart` (the number of non-terminals)
as each token arrives. A negative id, or one past the last terminal, is reported as unrecognized input.

Terminals: a rule that references no symbols (only `"literals"` and `'patterns'`) is a terminal; `<terminal>`
forces one (it may then reference other terminals, which are inlined) and `<terminal=false>` forces a
non-terminal. Each `TerminalDeclaration` keeps its `Definition` expression and a `Regex` for your lexer
generator: patterns pass through unvalidated with the quotes removed (`\'` becomes `'`), literals are escaped,
and other definitions are built from their constructs.

## Build and test

Open `Parsley.sln` in Visual Studio, or run `dotnet build Parsley.sln`.

You need dotnet 8 installed to execute the build steps due to luthor being compiled for that framework.

The luthor binary comes from https://www.nuget.org/packages/Luthor-tool (https://github.com/codewitch-honey-crisis/Luthor) and is used to generate a lexer. Parsley does not generate lexer code by itself, but will generate a specification for a lexer to consume. The LexGen project links against Luthor to create parsley-luthor which can consume the Parsley lexer spec directly and produce a Lexer class that is compatible with Parsley using Luthor's algorithms.

I shipped the Luthor binary with this project in order to avoid requiring you to install the dotnet tool yourself. You're more than welcome to replace it, and even rebuild it from the source at the link if you have trust issues.