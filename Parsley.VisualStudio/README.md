# Parsley XBNF for Visual Studio

Highlighting and language support for `.xbnf` grammars in Visual Studio 2022 (17.8 and later) and Visual Studio 2026.

- **Colors**, from the TextMate grammar in `Grammars/`: rule definitions and references, attributes, literals,
  regex patterns (with their classes, quantifiers and escapes), directives, comments, and the C# in semantic
  actions.
- **Go to definition** (F12) and **find all references** (Shift+F12) for rule names, across `@import`ed files.
  References also cover any open grammar that imports this one.
- **Completion**: rule names in expressions, attribute names and their values (`policy=`, `assoc=` and so on)
  inside `< >`, and directives after `@`.
- **Hover**: a rule as written, with its action shortened to `=> { … }`, and the `//` comments right above it.
  Attributes and directives get a one-line description.
- **Diagnostics**: syntax errors, undefined names and duplicate rules as you type. The full analysis
  (conflicts, lookahead, rewritten left recursion and so on) runs when you **save**, in the background, and
  its results stay until the next edit. A problem in an imported file shows on the `@import`.

## How it's put together

| Piece | |
|---|---|
| `Grammars/xbnf.tmLanguage.json` | The TextMate grammar. It isn't tied to Visual Studio, so a VS Code extension can use it as is. |
| `Grammars/xbnf.language-configuration.json` | Comment toggling, bracket matching and auto-closing pairs. |
| `xbnf.pkgdef` | Registers the grammar folder and language configuration with Visual Studio's TextMate support. |
| `XbnfContentDefinition.cs` | The `xbnf` content type for `.xbnf` files, based on the code-remote type, so both TextMate and the language client apply. |
| `XbnfLanguageClient.cs` | Starts `Parsley.LanguageServer` from the extension's `server` folder. |
| `../Parsley.LanguageServer` | The language server, which links the Parsley sources so its errors are the reader's and the analysis's own. |

Building this project builds the language server and packs its output into the VSIX's `server` folder. Press F5
to try it in the experimental instance of Visual Studio.

The server is framework-dependent, so it needs the **.NET 8 runtime** (or later) on the machine. If Visual
Studio reports that the server didn't start, installing the .NET 8 runtime is the first thing to try.

## Not done yet

- C# IntelliSense inside semantic actions. They're colored as C#, but nothing more.
- A VS Code extension. The grammar, language configuration and server are ready for one; it needs a small
  `package.json` and a few lines to start the server.
