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

`build.sh` expects `../bin/parsley.dll` and `../bin/parsley-luthor.dll`, so put this folder at the repo root like `C99Test`. Or set `PARSLEY` and `LUTHOR` to those files.

## Not checked

The standard's static semantics (section 16) aren't checked. Those belong to a later pass:
- `return` outside a function
- `break` or `continue` outside a loop, or with an unknown label
- duplicate labels
- a second `default` in a switch
- strict mode's rules

As every engine allows, a function can be declared wherever a statement can.
