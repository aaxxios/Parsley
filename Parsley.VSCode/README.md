# Parsley XBNF for VS Code

Highlighting and language support for [Parsley](https://github.com/codewitch-honey-crisis/Parsley)'s `.xbnf`
grammars.

- **Colors**: rule definitions and references, attributes, literals, regex patterns (classes, quantifiers and
  escapes), directives (with an `@external` or `@lexer` alias) and comments. The C# in semantic actions is colored by VS Code's own C# grammar.
- **Go to definition** (F12), **peek** and **find all references** (Shift+F12) for rule names, across
  `@import`ed files. References also cover any open grammar that imports this one. They work wherever a rule is
  named: in rules, in the terminal lists of `on`, `after` and `sync`, and in semantic actions, where a symbol
  constant (`add`) and an `EvaluateXxx` call lead to their rule.
- **Rename** (F2) a rule everywhere it's named, actions included, or a local an action declares. Other C# names
  in actions can't be renamed, but find all references finds them across the grammar's actions.
- **Highlights** of every use of the rule, or the action's local, under the cursor.
- **Completion**: rule names in expressions, attribute names and values (`policy=`, `assoc=` and so on) inside
  `< >`, and directives after `@`.
- **Hover**: a rule as written, with its action shortened to `=> { … }`, the `//` comments right above it, and
  what it is: a terminal, a non-terminal, an island, a contextual terminal or one from another lexer.
  Attributes and directives get a one-line description.
- **Lookahead glyphs**: once a grammar is analyzed, a small glyph sits at each choice (an alternation, a loop or an optional)
  that needs more than one token of lookahead, so you can see what pushes the grammar off LL(1). `◇` with
  k after it, such as `◇²`, is a choice that needs k tokens, so the grammar needs the LL(k) engine. `◆` is one
  no fixed lookahead decides, so it needs ALL(\*). Hover the glyph for why, with examples of the input it
  can't tell apart. They're inlay hints, so the theme colors them (`editorInlayHint.typeForeground` for
  ALL(\*), `editorInlayHint.parameterForeground` for LL(k)), and `editor.inlayHints.enabled` turns them off.
  An edit to a rule hides its glyphs until the next analysis.
- **Diagnostics**: syntax errors, undefined names and duplicate rules as you type. The full analysis
  (conflicts, lookahead, rewritten left recursion and so on) runs when you **open** a grammar and when you
  **save** it, and stays until the next edit. A problem in an imported file shows on its `@import`.
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
