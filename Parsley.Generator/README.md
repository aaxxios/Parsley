# Parsley.Generator

An incremental source generator that synthesizes a Parsley parser, its lexer and, when the grammar has semantic
actions, its evaluator into a partial class. It's built from the same sources as `parsley` and `parsley-luthor`
(linked from `../Parsley` and `../LexGen`), so it produces the same parsers the tools do.

```csharp
using Parsley;

[Parsley("Expr.xbnf")]
public partial class ExprCalc { }

var tree = ExprCalc.Parse("1+(2^4)*3");
Console.WriteLine(ExprCalc.Evaluate(tree)); // 49
```

## The attribute

`[Parsley(grammarFile, Unicode = ..., Lexer = ..., Engine = ...)]`

| Argument | |
|---|---|
| `grammarFile` | The `.xbnf` grammar, relative to the source file the attribute is in. If no file is there, the one AdditionalFile whose path ends with it is used. |
| `Unicode` | Use Unicode character classes in the lexer (`parsley-luthor -u`). Default `false`. |
| `Lexer` | Generate the lexer. Default `true`. When `false`, no lexer, `Tokenize`, or text `Parse` overloads are generated: feed `Parse`/`TryParse` tokens from a lexer of your own. |
| `Engine` | `ParserEngine.Auto` (the lightest the grammar allows), `LL1`, `LLk` or `AllStar` (`parsley -e`). Default `Auto`. |

The attribute and `ParserEngine` are generated into the project (namespace `Parsley`, internal, and not kept in
metadata). The class, and any type it's nested in, must be `partial`.

## What's generated

Into the partial class:

- The parser's symbol constants, public, named as the symbols are written (`Expr`, `number`, `add`, ..., `EndOfInput`,
  `ErrorSymbol`, `SymbolCount`). See XBNF.md's [Symbol constants](../XBNF.md#symbol-constants).
- Public static entry points:
  - `Tokenize(string)` / `Tokenize(TextReader)`, with the lexer's optional position, line, column and tab width (unless `Lexer = false`)
  - `Parse(IEnumerable<Token>)`, `Parse(string)`, `Parse(TextReader)` (the last two unless `Lexer = false`), and `TryParse(IEnumerable<Token>, out ParseNode, out IReadOnlyList<ParseError>)`
  - `Evaluate(ParseNode, state)`, when the grammar has semantic actions, returning the start production's type
  - `TryParseXxx(IEnumerable<Token>, out ParseNode, out IReadOnlyList<ParseError>)` for each `shared` production, and
    `CreateLexerMode(TokenizeFunc)`, for other grammars to embed this one as islands. A grammar with shared
    productions and no `start` gets no `Parse` or `TryParse` of its own.
- Private nested classes: `Parser`, `Evaluator` (with semantic actions) and `Lexer` (unless `Lexer = false`).
- For a grammar with islands, for each `@external` grammar: an internal nested class named after its alias, holding
  that grammar's `Parser`, `Lexer` and `Evaluator`, its symbol constants, and its own `@external` grammars' classes
  in turn. So `[Parser("bamc.xbnf")]` also gets json.xbnf's parser and lexer, as `BamcParser.Json`, and
  `BamcParser.Json.Object` is the id of json.xbnf's `Object`, for looking at an island's tree. `Tokenize` lexes
  through a `LexerController`, which switches to an island's lexer and back. See XBNF.md's
  [Islands and other lexers](../XBNF.md#islands-and-other-lexers).
- A lexer for each `lexer=` alias, named after it (`RegexLexer`). A `when` on such a terminal is a static partial
  method on the nested parser, as a contextual terminal's is an instance one.

A lexer `@lexer` names, for the grammar, an `@external` alias or a `lexer=` alias, is used instead of generating one.

The grammar's `@class`, `@namespace` and `@evaluator` directives are ignored: the class decides the names and the
namespace. `@using` still applies to semantic actions. A constant that would clash with a member of the class, with
the generated members, or with the class an `@external` grammar's constants are in, gets a number after it and a
warning, as the tools do with clashes inside the parser: json.xbnf's `Json` rule is `BamcParser.Json.Json2`, since
the class is named `Json` after its alias. See XBNF.md's [Symbol constants](../XBNF.md#symbol-constants).

Grammar hooks (`when`, `matched`, `entering`, `parsed`) are partial methods on the nested parser, so implement them
in another part of it:

```csharp
partial class C99
{
    partial class Parser
    {
        private partial bool IsTypedefName(Token token, Token previous) { ... }
    }
}
```

## The runtime

The runtime the parsers and lexers share is generated once per project, for only the engines its parsers use:
`ParsleyRuntime`, the `LL1ParserBase`, `LLkParserBase` or `AllStarParserBase` it needs (namespace
`Parsley.Runtime`, public) and, when at least one parser has a lexer, the lexer runtime, `LuthorRuntime` and the
types it uses (also in `Parsley.Runtime`, internal). Generated lexers call it there.

If the project references `Parsley.Runtime.dll`, none of it is generated, and the parsers and lexers use the dll's,
which has all of it, the lexer runtime public.
Reference the dll when more than one project in an app uses the generator. Each generated copy of
`Parsley.Runtime` is public, so a project that references two of them would see each type twice.

## Grammar files

The generator reads grammars, and the files they `@import` or declare with `@external`, from the project's
AdditionalFiles, never from disk.
The package's build targets make every `.xbnf` in the project an AdditionalFile. Set
`<ParsleyIncludeXbnf>false</ParsleyIncludeXbnf>` to list them yourself.

From a project reference in this solution, rather than the package, reference it as an analyzer and import the
targets:

```xml
<ItemGroup>
  <ProjectReference Include="..\Parsley.Generator\Parsley.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<Import Project="..\Parsley.Generator\build\Parsley.Generator.targets" />
```

The lexer is always built from the grammar's terminals, as `parsley -l` writes it. A grammar that pairs with a
hand-written `.lex`, like the ES5 test's, gets the grammar-derived lexer instead, unless `Lexer = false`, which
leaves you to supply the tokens.

The generated code needs C# 12, as the tools' output does.

## Diagnostics

| Id | |
|---|---|
| PARSLEY001 | The grammar file isn't among the AdditionalFiles |
| PARSLEY002 | More than one AdditionalFile ends with the given path |
| PARSLEY003 | The class, or a type it's nested in, isn't partial |
| PARSLEY004 | An error in the grammar, reported at its place in the `.xbnf` |
| PARSLEY005 | A warning about the grammar |
| PARSLEY006 | A message about the grammar (info), such as left recursion that was rewritten |
| PARSLEY007 | Generation failed for another reason |
| PARSLEY008 | The attribute has no grammar file |