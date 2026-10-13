# ES5 for Parsley

An ECMAScript 5.1 parser built with Parsley. It covers the full ES5 grammar, plus automatic semicolon insertion and regular expression literals.

| File | What it is |
| --- | --- |
| `es5.xbnf` | The grammar (Annex A of ECMA-262 5.1) |
| `ES5Tokenizer.cs` | Sits between the lexer and the parser. It decides whether `/` is division or a regex literal, and inserts `;` where a line break ends a `return`, `break`, `continue`, `throw`, or a line starts with `++`/`--` |
| `Es5Parser.Hooks.cs` | Checks the parser makes as it goes: the rest of semicolon insertion, assignment targets, for-in heads, get/set accessors, and no expression statement starting with `function` |
| `build.sh` | Regenerates `Es5Parser.cs`, `ES5Lexer.cs` and `Es5RegexLexer.cs` from the grammar |
| `Program.cs`, `Es5Test.csproj` | A test driver: `dotnet run -- file.js [-t]` |

Use it like this:

```csharp
var tree = Es5Parser.Parse(ES5Tokenizer.Tokenize(text));
```

Tokens have to come from `ES5Tokenizer`, not from `ES5Lexer` directly.

## Regular expressions without a wrapper

XBNF can say where another lexer takes over by itself (see [Terminals from other lexers](../XBNF.md#terminals-from-other-lexers)):

```xbnf
@lexer Regex "ES5RegexLexing.ES5RegexLexer";
regular_expression_literal<lexer=Regex, on="slash div_assign", when="RegexCanStart">= '...';
```

With that, the synthesized `Tokenize` runs a lexer controller that asks `RegexCanStart(token, previous)` at each `/` and `/=`, and the parser gets a compound grammar's `Tokenize` and `Parse(string)`. It decides from the token before alone, as many JavaScript tokenizers do, so `if (x) /re/.test(s)` reads as division. `ES5Tokenizer` stays because it does better than that: it tracks whether a `)` closed an `if`, `while`, `for` or `with` condition and whether a `}` closed a block, and it inserts the semicolons the restricted productions need. A wrapper given the controller's `Tokenize(TextReader, position, line, column, tabWidth)` shape (see [Naming the lexers](../XBNF.md#naming-the-lexers)) can be named with `@lexer` and run under the controller like any other lexer.

`build.sh` expects `../bin/parsley.dll` and `../bin/parsley-luthor.dll`, so put this folder at the repo root like `C99Test`. Or set `PARSLEY` and `LUTHOR` to those files.

## Not checked

The standard's static semantics (section 16) aren't checked. Those belong to a later pass:
- `return` outside a function
- `break` or `continue` outside a loop, or with an unknown label
- duplicate labels
- a second `default` in a switch
- strict mode's rules

As every engine allows, a function can be declared wherever a statement can.
