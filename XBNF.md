# XBNF in Parsley

XBNF is the grammar format Parsley reads. One file holds both halves of a language: the **terminals**, which go to a lexer generator, and the **non-terminals**, which Parsley turns into a parser. A rule can also carry a **semantic action**, C# that evaluates its parse node, and Parsley then synthesizes an evaluator class alongside the parser (see [Semantic actions](#semantic-actions)). This document covers the XBNF that Parsley's reader (`XbnfReader`, used by `Grammar.Parse` and `Grammar.ReadFrom`) accepts.

```xbnf
// based on the spec at json.org
Json<start>= Object | Array;
Object= "{" [ Field { "," Field } ] "}";
Field= string ":" Value;
Array= "[" [ Value { "," Value } ] "]";
Value<collapsed>= string | number | Object | Array | Boolean | null;
Boolean= true | false;
number= '\-?(0|[1-9][0-9]*)(\.[0-9]+)?([Ee][\+\-]?[0-9]+)?';
string= '"([^\n"\\]|\\([btrnf"\\/]|(u[0-9A-Fa-f]{4})))*"';
true= "true";
false= "false";
null= "null";
lbracket<collapsed>= "[";
rbracket<collapsed>= "]";
lbrace<collapsed>= "{";
rbrace<collapsed>= "}";
colon<collapsed>= ":";
comma<collapsed>= ",";
whitespace<hidden>= '[\n\r\t ]+';
```

## File structure

A file is a sequence of **rules** and **directives**, in any order, each ending with `;`.

```xbnf
@directive "argument";
Name= expression;
Name<attribute, attribute=value>= expression;
Name<type="double">= expression => { /* C# */ };
```

- **Comments** are `// to end of line` or `/* block */`. They can go anywhere whitespace can.
- **Whitespace and line breaks** are insignificant. A rule can span lines.
- **Names** start with a letter or `_`, then letters, digits or `_`. They are case-sensitive.
- **Rule order** matters in three places: the first non-terminal is the start symbol unless one is marked `start`; terminals are numbered in the order they're declared; and the lexer usually breaks ties between equal-length matches by declaration order.

The syntax of XBNF itself, written in XBNF:

```xbnf
File<start>= { Directive | Rule };
Directive= "@" name literal ";";
Rule= name [ Attributes ] "=" Expression [ "=>" Action ] ";";
Action= "{" … "}";                           // C#, read up to its matching brace
Expression= Sequence { "|" Sequence };
Sequence= { Primary [ Attributes ] };        // an empty sequence matches nothing
Primary= name | literal | pattern
       | "(" Expression ")"                  // group
       | "[" Expression "]"                  // optional
       | "{" Expression "}" [ "+" ];         // zero or more, or one or more
Attributes= "<" [ Attribute { "," Attribute } ] ">";
Attribute= name [ "=" Value ];
Value= literal | number | name;              // true, false, or a bare word read as a string
```

## Expressions

| Syntax | Meaning |
| --- | --- |
| `Name` | A reference to another rule, terminal or non-terminal |
| `"text"` | A literal: matches exactly this text |
| `'regex'` | A pattern: a regular expression for the lexer |
| `a b c` | Sequence: each in order |
| `a \| b \| c` | Alternation: any one of them |
| `( a b )` | Grouping |
| `[ a ]` | Optional: zero or one |
| `{ a }` | Repeat: zero or more |
| `{ a }+` | Repeat: one or more. The `+` must come right after the `}`, with no space. |
| `a \|` or `( )` | An empty alternative, which matches nothing |

Sequence binds tighter than alternation, so `a b | c` means `( a b ) | c`.

### Literals

`"…"` is literal text. These escapes are recognized: `\n` `\r` `\t` `\f` `\v` `\b` `\0` `\\` `\"` `\'` `\/` and `\uXXXX`. A literal can't span lines.

### Patterns

`'…'` is a regular expression, passed to the lexer generator as written. Parsley doesn't validate it, so any syntax your lexer generator supports works. The only change is that `\'` becomes `'`, because inside single quotes it can only mean a quote character. (Some engines read `\'` as an end-of-input anchor, so leaving it in could change the meaning.) Every other backslash is kept, so `\\'` is still an escaped backslash followed by the closing quote. A pattern can't span lines.

## Terminals and non-terminals

Every rule is one or the other, decided after the whole grammar, imports included, has been read:

- **By default, a rule is a terminal only if it references no symbols.** Its right side is made only of literals and patterns, possibly combined with operators: `true= "true";`, `sign= "+" | "-";`, `ws= ' +';`.
- **Any rule that references another rule is a non-terminal**, even if everything it references is a terminal. So `Boolean= true | false;` is a non-terminal.
- **`<terminal>` forces a terminal.** Such a rule may reference other terminals, and their regexes are inlined. It's an error if it references a non-terminal or itself.
- **`<terminal=false>` forces a non-terminal**, even for a rule with no references.
- **A rule marked `start` is never made a terminal automatically**, only with `<terminal>`. If every rule in the file would be a terminal, the first one is kept as a non-terminal so the grammar has something to parse.

### The regex each terminal gets

Each terminal carries its definition as written and a regular expression for the lexer generator (`TerminalDeclaration.Definition` and `.Regex`):

| Definition | Regex |
| --- | --- |
| A single pattern | Passed through as written, without the quotes |
| A single literal | The text with regex metacharacters escaped: ``\ . * + ? \| ( ) [ ] { } ^ $ / "``. Control characters become `\n`, `\t` and so on, or `\xHH`. |
| Anything else | Built from the constructs: sequence becomes concatenation, `\|` stays `\|`, `[ ]` becomes `?`, `{ }` becomes `*`, `{ }+` becomes `+`, and an empty alternative makes the rest optional. Referenced terminals are inlined. Parentheses are added where precedence needs them. |

For example, with `letter= '[a-z]';` and `digit= '[0-9]';`:

```xbnf
Ident<terminal>= letter { letter | digit | "_" };   // [a-z]([a-z]|[0-9]|_)*
```

### Literals used inline

A literal or pattern written directly inside a non-terminal, like `"{"` in `Object= "{" … "}";`, refers to a terminal:

- **If a terminal with the identical literal or pattern is declared**, the inline use refers to it. In JSON, `"{"` refers to `lbrace`.
- **Otherwise Parsley declares an implicit terminal** for it, after all the declared ones. Its name comes from the text:
  - words become `<word>Keyword`: `"if"` gives `ifKeyword`
  - punctuation is spelled out and joined with `_`: `"("` gives `lparen`, `"=="` gives `eq_eq`
  - anything else becomes `implicit`, `implicit2` and so on

Implicit terminals come last, so they lose ties in a lexer that prefers earlier declarations. To control a literal's priority, declare it explicitly where you want it, for example `ifKeyword= "if";` above your identifier pattern. Inline uses then refer to that declaration.

## Attributes

Attributes go in `<…>` after a rule's name, or after any expression or group. They are separated by commas, and each is a bare name (meaning `true`) or `name=value`.

Values can be:
- a `"string"`
- a number
- `true` or `false`
- a bare word, read as a string: `policy=lazy` is the same as `policy="lazy"`

`null` isn't allowed. If an attribute appears twice, the last one wins, with a warning. Unknown attributes are kept but ignored, with a message.

### On non-terminal rules

| Attribute | Value | Effect |
| --- | --- | --- |
| `start` | flag | This rule is the start symbol. Otherwise the first non-terminal is. If several are marked, the first wins, with a warning. |
| `collapsed` | flag | The rule gets no node in the parse tree; its children go straight into its parent's node. The start rule can't be collapsed. |
| `terminal` | `true` / `false` | Forces the rule to be a terminal or a non-terminal (see above). |
| `nowarn` | flag | Suppresses the "unreachable from the start symbol" warning. |
| `expected` | string | How the rule is named in error messages when none of its alternatives match: `expected="a statement"` gives "Expected a statement but found …". By default the message lists every token the rule can start with. |
| `policy` | `greedy`, `lazy`, `first` | How to settle decisions in this rule that are ambiguous (see below). A group's own `policy` takes precedence. |
| `sync` | string | Extra terminals where error recovery may stop while this rule is being parsed. The value is terminal names or literal texts separated by spaces or commas: `sync="semi rbrace"`. |
| `type` | string | The C# type the rule's `EvaluateXxx` method returns, written verbatim: `type="double"`. Without it, the method returns `object`. Quote it, since a type like `List<int>` can't be a bare word. See [Semantic actions](#semantic-actions). |
| `state` | string | On the start rule only: the C# type of the evaluator's `State` and of the `state` argument to `Evaluate`, written verbatim: `state="MyContext"`. Without it, both are `object?`. On any other rule it's ignored, with a warning. |

### On terminal rules

| Attribute | Value | Effect |
| --- | --- | --- |
| `hidden` | flag | The lexer matches it but never reports it, so the parser never sees it (whitespace, comments). A hidden terminal can't be referenced from a non-terminal. |
| `collapsed` | flag | The parser checks it as usual but leaves it out of the parse tree (punctuation). |
| `sync` | flag | A strong restart point for error recovery anywhere in the grammar, typically `;` or `}`. |
| `terminal` | `true` / `false` | Forces terminal or non-terminal (see above). |
| `expected`, `blockEnd` | string | Accepted for compatibility with older grammars. They currently have no effect. |

### On groups and expressions

Any primary expression can take attributes, which apply to that one occurrence only. They matter on a decision point: an alternation, `[ … ]` or `{ … }`.

| Attribute | Value | Effect |
| --- | --- | --- |
| `policy` | `greedy`, `lazy`, `first` | Settles this decision if it's ambiguous. |
| `expected` | string | Names this alternation in error messages. |
| `sync` | string | Like the rule attribute: adds terminals where recovery may stop, for the enclosing rule. |

```xbnf
S= ( A | B )<policy=first>;
Stmt= "if" Expr "then" Stmt [ "else" Stmt ]<policy=greedy> | other;
Block= "{" { Stmt }<sync="semi"> "}";
```

## Directives

| Directive | Effect |
| --- | --- |
| `@import "file.xbnf";` | Reads another file's rules in at this point, with the path relative to the importing file. A file is imported only once, so import cycles are harmless. Imported rules keep their own file name in messages. An imported file's directives don't override the importer's. |
| `@namespace "My.Parsers";` | The namespace of the synthesized parser. Without it, the parser has no namespace. |
| `@class "MyParser";` | The class name of the synthesized parser. By default it's the file name in PascalCase plus `Parser`, so `json.xbnf` gives `JsonParser`. |
| `@evaluator "MyEvaluator";` | The class name of the synthesized evaluator, used as written, with `@` added in front of a C# keyword. By default it's the file name in PascalCase plus `Evaluator`, so `json.xbnf` gives `JsonEvaluator`. Only matters when the grammar has [semantic actions](#semantic-actions). |

Other directives are kept, with a warning that they're unknown.

## Semantic actions

A non-terminal rule can end with `=>` and a block of C#, before the `;`. The block is a **semantic action**: the body of a method that turns that rule's node in the parse tree into a value.

```xbnf
@evaluator "ExprEvaluator";

Expr<start, type="double">= Expr add Term | Expr sub Term | Term => {
    if (1 == node.Children.Count)
        return EvaluateTerm(node.Children[0]);
    var left = EvaluateExpr(node.Children[0]);
    var right = EvaluateTerm(node.Children[2]);
    return Add == node.Children[1].SymbolId ? left + right : left - right;
};
```

Actions run over the finished tree, not during the parse. The parser can look several tokens ahead before it commits to a branch, and it rebuilds nodes as it folds left recursion into loops, so the tree is the only reliable place to evaluate. Use the synthesized classes like this:

```csharp
var tree = ExprParser.Parse(tokens);
double value = ExprEvaluator.Evaluate(tree);
```

### The evaluator class

When the start rule has an action, Parsley synthesizes an evaluator class in the same file, after the parser. If the start rule has no action, no evaluator is synthesized, and an action on any other rule is an error. The class is `partial`, so you can add members, such as helpers your actions call, in another file. It has:

| Member | What it is |
| --- | --- |
| `public static T Evaluate(ParseNode node, S state)` | Evaluates a tree from the parser, starting at its root. It creates an evaluator and calls the start rule's method. `T` is the start rule's `type`, or `object`. `S` is the start rule's `state` type, and then `state` must be passed. Without a `state` type it's `object? state = null`. |
| `protected T EvaluateXxx(ParseNode node)` | One for each non-terminal that gets a node in the tree, so not for collapsed rules. `Xxx` is the rule name in PascalCase, and `T` is the rule's `type`, or `object`. The body is the rule's action. |
| `protected object Evaluate(ParseNode node)` | Calls the right `EvaluateXxx` for the node's symbol. Use it in an action when a child can be one of several non-terminals, as it can below a collapsed rule like `Value<collapsed>= Object \| Array;`. Inside the class, `Evaluate(child)` binds to this overload, not to the static one. |
| `protected S State` | The `state` passed to `Evaluate`, typed by the start rule's `state` attribute, or `object?`. |
| `protected const int Xxx` | The same symbol constants the parser has, so actions can test `node.Children[0].SymbolId == Number`. |

### Writing an action

- The node is in `node`. Its `Children` are what the rule matched, in order. Terminals carry their text in `Value`, and non-terminals have their own children.
- Collapsed rules and collapsed terminals have no node, and their children sit directly in the parent. In `Power= Primary [ pow Unary ];` with `Primary<collapsed>= number | lparen Expr rparen;`, the first child of a `Power` node is either a `number` token or an `Expr` node, since `lparen` and `rparen` are collapsed too.
- Left recursion folds to the left. `E= E "+" T | T;` gives `E(E(T) "+" T)` for `1 + 2`. A node made by folding has three children here, and the innermost one has just the `T`.
- Return a value of the rule's `type`. The code is copied into the method as written, and Parsley doesn't insert casts, so each `return` needs a value C# converts to that type on its own. When you call `Evaluate(child)`, cast its result yourself.
- Call other rules' `EvaluateXxx` methods directly when you know which rule a child is.
- Anything the actions need from outside the tree, such as variables, options or a symbol table, goes in through `state` and is read back as `State`. Give the start rule a `state` type and `State` needs no cast:

  ```xbnf
  Calc<start, type="int", state="System.Collections.Generic.IDictionary<string, int>">= … => { … };
  Var<type="int">= name => { return State[node.Children[0].Value!]; };
  ```

  ```csharp
  int value = CalcEvaluator.Evaluate(tree, new Dictionary<string, int> { ["x"] = 5 });
  ```
- The generated file has `using System;`, `using System.Collections.Generic;` and `using Parsley.Runtime;`. Usings only apply to the file they're in, so write other namespaces out in full: `System.Globalization.CultureInfo.InvariantCulture`.
- The action is read up to its matching `}`. Braces inside strings, character literals, comments and interpolation holes don't count, including verbatim, interpolated and raw strings. The code is re-indented to fit its method. The exception is a string literal that spans lines, since re-indenting would change it, so that code is copied exactly.
- An action can't be on a terminal or a collapsed rule, since neither has a node of its own.

### Rules without an action

A rule with no action still gets an `EvaluateXxx` method. Its node evaluates to its one non-terminal child, ignoring terminals, so chains like `Item= Wrapped | Word;` need no code. A node with no non-terminal children, or with more than one, throws `InvalidOperationException` when it's evaluated. Watch for rules like `Unary= sub Unary | Power;`: without an action, the `-` would be ignored and the node would evaluate to its `Unary` child.

## How rules become a parser

None of this needs to be written in the grammar, but it explains the messages Parsley gives and the code it produces.

### Lookahead

Each decision point (each `|`, `[ ]` and `{ }`) uses the fewest tokens of lookahead that tell its branches apart, up to 4 by default. Most decisions need one token. Ones that need more are noted in the synthesized code.

### Ambiguity

A decision whose branches still overlap after 4 tokens either needs more lookahead or is really ambiguous. With the engine chosen automatically (the default), Parsley leaves it to ALL(\*), which looks as far ahead as it takes at parse time, with a message saying so. If the input really is ambiguous there, ALL(\*) settles it the way the defaults below do: the first alternative wins, and a loop or optional keeps matching.

A `policy` settles the decision when the grammar is analyzed instead, on every engine, which keeps a grammar like the dangling `else` LL(1):

- **`greedy`:** a loop keeps going and an optional is taken. It's what makes `else` bind to the nearest `if`.
- **`lazy`:** a loop stops and an optional is skipped.
- **`first`:** the earliest alternative wins. On a loop or optional it acts like `greedy`.

With the LL(1) or LL(k) engine forced (`-e ll1`, `-e llk`), a loop or optional with no `policy` is settled greedily with a warning, and an alternation with no `policy` is an error.

A rule that can't match any finite input (every way through it leads back into itself, as in `A= a A;`) gets a warning, since parsing it can never succeed.

### Left recursion

Left recursion is rewritten automatically:

- **Direct left recursion becomes a loop.** `E= E "+" T | T;` is parsed as `T { "+" T }`. The tree still comes out left-associative: `1 - 2 - 3` gives `E(E(1 - 2) - 3)`.
- **Indirect left recursion** (`A= B x; B= A y | z;`) is made direct by inlining, with a warning that the inlined rule loses its own node on that path.
- **Left recursion through a nullable prefix** (`A= N A x;` where `N` can match nothing) is an error.
- **A rule that derives exactly itself** (`A= A;`) is an error.

### Errors in other places

| Problem | Level |
| --- | --- |
| A name defined twice | Error |
| A reference to an undefined name | Error |
| A hidden terminal referenced from a non-terminal | Error |
| A loop whose body can match nothing (`{ [ a ] }`) | Error |
| A rule with a code body (`Name<virtual> { … }`) | Error: virtual productions aren't supported |
| A semantic action on a rule other than the start rule, when the start rule has none | Error |
| A semantic action on a terminal or a collapsed rule | Error |
| `type` on a collapsed rule | Warning: it has no node, so the type is ignored |
| `state` on a rule other than the start rule | Warning: it's ignored |
| `type` or `state` that isn't a string, such as `type=5` | Error |
| The evaluator and the parser named the same | Error when synthesizing |
| A non-terminal unreachable from the start symbol | Warning, unless `nowarn` |

Messages carry the file, line and column of the rule or expression they're about.

## Symbol ids

The synthesized parser numbers symbols in this order:

1. non-terminals, in rule order
2. terminals, in declaration order, including hidden ones, followed by implicit terminals
3. `#EOS` (end of input) and `#ERROR` (unrecognized input)

A lexer reports terminals numbered from 0 in that same terminal order. The parser adds the number of non-terminals as each token arrives. A negative id, or one past the last terminal, is treated as `#ERROR`.