# Parsley XBNF for VS Code

Highlighting and language support for [Parsley](https://github.com/codewitch-honey-crisis/Parsley)'s `.xbnf`
grammars.

- **Colors**: rule definitions and references, attributes, literals, regex patterns (classes, quantifiers and
  escapes), directives (with an `@external` or `@lexer` alias) and comments. The C# in semantic actions is colored by VS Code's own C# grammar.
- **Go to definition** (F12), **peek** and **find all references** (Shift+F12) for rule names, across
  `@import`ed files. References also cover any open grammar that imports this one.
- **Highlights** of every use of the rule under the cursor.
- **Completion**: rule names in expressions, attribute names and values (`policy=`, `assoc=` and so on) inside
  `< >`, and directives after `@`.
- **Hover**: a rule as written, with its action shortened to `=> { … }`, the `//` comments right above it, and
  what it is: a terminal, a non-terminal, an island, a contextual terminal or one from another lexer.
  Attributes and directives get a one-line description.
- **Diagnostics**: syntax errors, undefined names and duplicate rules as you type. The full analysis
  (conflicts, lookahead, rewritten left recursion and so on) runs when you **save** and stays until the next
  edit. A problem in an imported file shows on its `@import`.
- **Comment toggling**, bracket matching and auto-closing pairs.

## Requirements

The language features come from a .NET language server packed in the extension, which needs the **.NET 8
runtime** or later. Highlighting works without it.

| Setting | |
|---|---|
| `parsley.xbnf.dotnetPath` | The `dotnet` that runs the server, if it isn't on the `PATH` |
| `parsley.xbnf.serverPath` | A `Parsley.LanguageServer.dll` to use instead of the packed one |
| `parsley.xbnf.trace.server` | `messages` or `verbose` to log the protocol traffic |

**Parsley: Restart XBNF Language Server** restarts it.
