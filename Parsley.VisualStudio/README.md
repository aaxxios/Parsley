# Parsley XBNF for Visual Studio

Highlighting and language support for `.xbnf` grammars in Visual Studio 2022 (17.8 and later) and Visual Studio 2026.

- **Colors**, from the TextMate grammar in `Grammars/`: rule definitions and references, attributes, literals,
  regex patterns (with their classes, quantifiers and escapes), directives (with an `@external` or `@lexer` alias), comments, and the C# in semantic
  actions.
- **Go to definition** (F12) and **find all references** (Shift+F12) for rule names, across `@import`ed files.
  References also cover any open grammar that imports this one. They work wherever a rule is named: in rules,
  in the terminal lists of `on`, `after` and `sync`, and in semantic actions, where a symbol constant (`add`) and
  an `EvaluateXxx` call lead to their rule.
- **Rename** (Ctrl+R, Ctrl+R) a rule everywhere it's named, actions included, or a local an action declares.
- **Go to definition in semantic actions** for C#: a method, type, property or field an action uses goes to where
  it's declared in the solution, or to its metadata when it comes from a reference, and a local goes to its
  declaration in the action. This comes from Visual Studio's C# workspace, like completion and quick info in
  actions, so it needs the grammar generated once. A rule's name or symbol constant still goes to the rule.
- **Completion**: rule names in expressions, attribute names and their values (`policy=`, `assoc=` and so on)
  inside `< >`, and directives after `@`.
- **Hover**: a rule as written, with its action shortened to `=> { … }`, the `//` comments right above it, and
  what it is: a terminal, a non-terminal, an island, a contextual terminal or one from another lexer.
  Attributes and directives get a one-line description.
- **Lookahead glyphs**: once a grammar is analyzed, a small glyph sits at each choice (an alternation, a loop or
  an optional) that needs more than one token of lookahead, so you can see what pushes the grammar off LL(1).
  `◇` with k after it, such as `◇²`, is a choice that needs k tokens, so the grammar needs the LL(k) engine.
  `◆` is one no fixed lookahead decides, so it needs ALL(\*). Hover the glyph, or the start of the choice, for
  why. Their colors are **XBNF Lookahead Glyph: LL(k)** and **XBNF Lookahead Glyph: ALL(\*)** in Tools >
  Options > Environment > Fonts and Colors. An edit to a rule hides its glyphs until the next analysis.
- **Diagnostics**: syntax errors, undefined names and duplicate rules as you type. The full analysis
  (conflicts, lookahead, rewritten left recursion and so on) runs when you **open** a grammar and when you
  **save** it, in the background, and its results stay until the next edit. A problem in an imported file shows on the `@import`.
- **C# in semantic actions**: completion and quick info, from Visual Studio's own C# support. They work in a
  grammar whose code is in a project in the open solution, whether the source generator makes it
  (`[Parsley.Parser("grammar.xbnf")]`) or the command line tool writes it into a file the project compiles. The
  action's current text is used, so it doesn't wait for the grammar to be regenerated, and an action on a rule
  that's new since then still sees the evaluator's members (`node`, `State`, the `Evaluate` methods and the symbol
  constants). The grammar has to have been generated once with at least one action, so there's an evaluator to
  put the code in.

## How it's put together

| Piece | |
|---|---|
| `Grammars/xbnf.tmLanguage.json` | The TextMate grammar. It isn't tied to Visual Studio, so a VS Code extension can use it as is. |
| `Grammars/xbnf.language-configuration.json` | Comment toggling, bracket matching and auto-closing pairs. |
| `xbnf.pkgdef` | Registers the grammar folder and language configuration with Visual Studio's TextMate support. |
| `XbnfContentDefinition.cs` | The `xbnf` content type for `.xbnf` files, based on the code-remote type, so both TextMate and the language client apply. |
| `XbnfLanguageClient.cs` | Starts `Parsley.LanguageServer` from the extension's `server` folder, and connects the custom messages the glyphs come through. |
| `DecisionHintService.cs` | The server's `parsley/decisionHints` request and `parsley/decisionHintsChanged` notification. Visual Studio's language client doesn't show inlay hints, as VS Code's does, so the extension asks for the glyphs itself. |
| `DecisionGlyphs.cs` | Draws the glyphs in the editor, with their tooltips and their Fonts and Colors entries. |
| `ActionCode.cs` | Finds the C# generated from a grammar in Visual Studio's workspace, by its `#line` directives, and copies it with an action's current text in place of the generated one. With the source generator, the copy is of the project without it, with its output as ordinary files. The copy is kept until the grammar's rules (not its actions) or the project's other files change. |
| `ActionIntellisense.cs` | Completion and quick info in actions: Roslyn's, over that copy, mapped back into the grammar. |
| `../Parsley.LanguageServer` | The language server, which links the Parsley sources so its errors are the reader's and the analysis's own. |

Building this project builds the language server and packs its output into the VSIX's `server` folder. Press F5
to try it in the experimental instance of Visual Studio.

The server is framework-dependent, so it needs the **.NET 8 runtime** (or later) on the machine. If Visual
Studio reports that the server didn't start, installing the .NET 8 runtime is the first thing to try.

## Not done yet

- In semantic actions: signature help and C# errors as you type. Roslyn's signature help
  isn't public; errors could follow the same path as completion. Compile errors already point into the
  grammar through the `#line` directives.
- A VS Code extension. The grammar, language configuration and server are ready for one; it needs a small
  `package.json` and a few lines to start the server.
