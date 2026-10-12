# XBNF in Parsley

XBNF is the grammar format Parsley reads. One file holds both halves of a language: the **terminals**, which go to a lexer generator, and the **non-terminals**, which Parsley turns into a parser. A rule can also carry a **semantic action**, C# that evaluates its parse node, and Parsley then synthesizes an evaluator class alongside the parser (see [Semantic actions](#semantic-actions)). Operators can be written in one rule, tightest first, instead of one rule per level of precedence (see [Operator rules](#operator-rules)). This document covers the XBNF that Parsley's reader (`XbnfReader`, used by `Grammar.Parse` and `Grammar.ReadFrom`) accepts.

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
Directive= "@" name ( literal | number ) ";";
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
| `entering` | method name | A method the parser calls, with no arguments, as it starts this rule. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `parsed` | method name | A method the parser calls with the finished parse node as this rule ends. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `operators` | flag | The rule's alternatives are operators, listed tightest first, and it's parsed by precedence climbing. See [Operator rules](#operator-rules). |
| `assoc` | `left`, `right` | On a production that is an operator form of an operator rule: how the form associates. See [Operator rules](#operator-rules). |

### On terminal rules

| Attribute | Value | Effect |
| --- | --- | --- |
| `hidden` | flag | The lexer matches it but never reports it, so the parser never sees it (whitespace, comments). A hidden terminal can't be referenced from a non-terminal. |
| `collapsed` | flag | The parser checks it as usual but leaves it out of the parse tree (punctuation). |
| `sync` | flag | A strong restart point for error recovery anywhere in the grammar, typically `;` or `}`. |
| `terminal` | `true` / `false` | Forces terminal or non-terminal (see above). |
| `when` | method name | Makes a contextual terminal, read by the parser from another terminal's tokens. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `matched` | method name | A method the parser calls with each token of this terminal it matches. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `expected`, `blockEnd` | string | Accepted for compatibility with older grammars. They currently have no effect. |

### On groups and expressions

Any primary expression can take attributes, which apply to that one occurrence only. They matter on a decision point: an alternation, `[ … ]` or `{ … }`.

| Attribute | Value | Effect |
| --- | --- | --- |
| `policy` | `greedy`, `lazy`, `first` | Settles this decision if it's ambiguous. |
| `expected` | string | Names this alternation in error messages. |
| `sync` | string | Like the rule attribute: adds terminals where recovery may stop, for the enclosing rule. |
| `assoc` | `left`, `right` | On any element of an infix form in an operator rule: how the form associates. See [Operator rules](#operator-rules). |

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
| `@maxk 2;` | The most tokens a decision with fixed lookahead looks at; 4 if not given. A decision that needs more is predicted by ALL(\*) at parse time, or settled by its `policy`. A lower limit gives smaller predictors at the cost of more ALL(\*); a higher one, the reverse. It's part of the grammar so the same grammar always synthesizes the same parser. |
| `@evaluator "MyEvaluator";` | The class name of the synthesized evaluator, used as written, with `@` added in front of a C# keyword. By default it's the file name in PascalCase plus `Evaluator`, so `json.xbnf` gives `JsonEvaluator`. Only matters when the grammar has [semantic actions](#semantic-actions); without any, it's ignored with a warning. |
| `@using "System.Text";` | Adds `using System.Text;` to the top of the synthesized file, so [semantic actions](#semantic-actions) can use the namespace's types without qualifying them. Give it once per namespace; it can appear any number of times. The argument must be a namespace name, such as `System.Text` or `My.@class`: not `static` or an alias. A namespace the file already imports, or one given twice, is written once, without a message. Usings from an `@import`ed file are added to the importer's. Only matters when the grammar has semantic actions; without any, it's ignored with a warning and the file is unchanged. |

Other directives are kept, with a warning that they're unknown. Every directive except `@using` takes one value, and if one is given more than once, the last wins.

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
- The generated file has `using System;`, `using System.Collections.Generic;` and `using Parsley.Runtime;`. For other namespaces, add an [`@using`](#directives) for each, such as `@using "System.Globalization";`, or write them out in full: `System.Globalization.CultureInfo.InvariantCulture`. A using in another file of your project doesn't help, since usings only apply to the file they're in (unless it's a `global using`).
- The action is read up to its matching `}`. Braces inside strings, character literals, comments and interpolation holes don't count, including verbatim, interpolated and raw strings. The code is re-indented to fit its method. The exception is a string literal that spans lines, since re-indenting would change it, so that code is copied exactly.
- An action can't be on a terminal or a collapsed rule, since neither has a node of its own.

### Rules without an action

A rule with no action still gets an `EvaluateXxx` method. Its node evaluates to its one non-terminal child, ignoring terminals, so chains like `Item= Wrapped | Word;` need no code. The exception is an operator form's production, which is evaluated by its operator rule's action (see [Evaluating operator rules](#evaluating-operator-rules)). A node with no non-terminal children, or with more than one, throws `InvalidOperationException` when it's evaluated. Watch for rules like `Unary= sub Unary | Power;`: without an action, the `-` would be ignored and the node would evaluate to its `Unary` child.

## Operator rules

Operators with precedence are usually written as a tower of rules, one per level, as in `Expr.xbnf`: `Expr` for `+` and `-`, `Term` for `*` and `/`, and so on down. A rule marked `operators` writes them all in one rule instead. Its alternatives are listed tightest first, and Parsley parses the rule by precedence climbing.

```xbnf
Expr<start, type="double", operators, expected="an expression">=
      Expr pow<assoc=right> Expr        // tightest
    | sub Expr                          // unary minus
    | Expr ( mul | div | mod ) Expr
    | Expr ( add | sub ) Expr           // loosest
    | number
    | lparen Expr rparen
    => { /* see ExprOperators.xbnf */ };
```

This is the same language as `Expr.xbnf`, with the same precedence: `-2^2` is `-(2^2)`, `2^-1` works, and `10 - 4 - 3` is `(10 - 4) - 3`. `ExprOperators.xbnf`, next to it, is the whole grammar with its action. The tower of rules still works as it always has; `operators` is opt-in, one rule at a time.

### Forms

Each alternative is a **form**, classified by whether it starts or ends with the rule's own name. Whatever lies between is its **operator**, and that can be any expression:

| Starts with the rule | Ends with it | Form | Examples |
| --- | --- | --- | --- |
| yes | yes | infix | `Expr add Expr`, `Expr question Expr colon Expr` |
| no | yes | prefix | `sub Expr`, `lparen name rparen Expr` (a cast) |
| yes | no | postfix | `Expr "++"`, `Expr lbracket Expr rbracket`, `Expr lparen [ Expr { comma Expr } ] rparen` |
| no | no | atom | `number`, `name`, `lparen Expr rparen` |

- **The rule inside an operator**, like the middle of a ternary or the index of `a[i]`, is an ordinary operand. It's parsed from the loosest level, as it is between parentheses, so `a ? b = c : d` works.
- **A group gives several operators one level:** `Expr ( add | sub ) Expr`.
- **An infix form with nothing between its operands**, `Expr Expr`, is juxtaposition, as in function application `f x`.
- **The rule needs at least one atom**, since that's what operators apply to.

### Precedence and associativity

- **Earlier alternatives bind tighter.** Atoms have no level, so they can go anywhere; listing them last reads best.
- **Infix forms are left associative** unless marked `assoc=right`: `a = b = c` is `a = (b = c)`. `assoc` goes on any element of the form, such as its operator (`Expr pow<assoc=right> Expr`) or its last operand (`Expr question Expr colon Expr<assoc=right>`), or on a [form's production](#forms-with-their-own-productions).
- **A prefix form's operand takes the operators that bind tighter than it.** With `^` above unary minus, `-2^2` is `-(2^2)`. With `*` below it, `-2 * 3` is `(-2) * 3`.
- **A prefix form can be any operand**, wherever it's listed: `2^-1` is `2^(-1)`, even though unary minus is looser than `^`.
- **A postfix form applies to everything to its left that binds tighter.** With indexing listed above `!`, `!a[0]` is `!(a[0])`.

### The tree

Every operator applied makes a node, with its children in the order they were read, and so does every atom. Operands are therefore always nodes:

| Form | Children |
| --- | --- |
| atom | what the atom matched |
| prefix | the operator, then the operand |
| infix | the left operand, the operator, then the right operand |
| postfix | the operand, then the operator |

As everywhere, collapsed terminals are left out. With the grammar above, where `lparen` and `rparen` are collapsed:

```
1 + 2 * 3      Expr(Expr(1) + Expr(Expr(2) * Expr(3)))
-2^2           Expr(- Expr(Expr(2) ^ Expr(2)))
(1 + 2) * 3    Expr(Expr(Expr(Expr(1) + Expr(2))) * Expr(3))
7              Expr(7)
```

So an action sees one of a few shapes, and can tell them apart by the number of children, or by the symbol of the operator.

### Forms with their own productions

An alternative can be a reference to a production whose right side is a single form. Its nodes are then that production's, and it can have an action of its own:

```xbnf
Expr<start, type="double", operators>=
      Power                    // tightest
    | Negate
    | Expr ( mul | div | mod ) Expr
    | Sum                      // loosest
    | number
    | lparen Expr rparen
    => { /* atoms, *, / and %, and Sum, which has no action */ };
Power<assoc=right>= Expr pow Expr => { return Math.Pow(EvaluateExpr(node.Children[0]), EvaluateExpr(node.Children[2])); };
Negate= sub Expr => { return -EvaluateExpr(node.Children[1]); };
Sum= Expr ( add | sub ) Expr;
```

- **Precedence still comes from where it's listed** in the operator rule.
- **`assoc` can go on the production** as well as on an element of its form.
- **The operator rule parses it.** It gets no parse method of its own, and its `entering` and `parsed` are ignored, with a warning; the operator rule's hooks run instead.
- **It can only be used as an alternative of its operator rule**, and only once.
- **It must be exactly one form.** To give several operators one level, group them: `Sum= Expr ( add | sub ) Expr;`, not `Sum= Expr add Expr | Expr sub Expr;`.
- **A reference to a production that isn't a form**, like `| Literal` with `Literal= number | string;`, is an atom like any other.
- **The root of the tree can be one of its nodes:** `1 + 2` gives a `Sum` node, not an `Expr` node.

### Evaluating operator rules

- **`EvaluateExpr` forwards** the nodes of forms whose productions have an action to their own `EvaluateXxx`, so `EvaluateExpr(child)` works on any operand, whatever form it is.
- **A form's production with no action is evaluated by the operator rule's action**, with a message saying so. In the grammar above, `Sum` nodes reach `Expr`'s action, which tells `+` from `-` by `node.Children[1].SymbolId`.
- **A form's production with no `type` returns `object`**, like any rule. Since every operand is evaluated through `EvaluateExpr`, the operator rule's `type` is the type of every operand's value.
- **When forms return different types**, such as arithmetic returning `int` and comparisons returning `bool`, leave the operator rule's `type` off (so `object`), or use a type of your own that holds either. Each form's production can still declare its exact type:

  ```xbnf
  E<start, operators>= Product | Less | number => { return int.Parse(node.Children[0].Value!); };
  Product<type="int">= E star E => { return (int)EvaluateE(node.Children[0]) * (int)EvaluateE(node.Children[2]); };
  Less<type="bool">= E lt E => { return (int)EvaluateE(node.Children[0]) < (int)EvaluateE(node.Children[2]); };
  ```
- **When the two types are written differently**, the forwarding casts, as in `return (double)EvaluateNegate(node);`, and the C# compiler decides whether that's allowed. `object` to `double` compiles and unboxes at run time, so it throws if the action actually returned something else, such as a boxed `int`. `bool` to `int` doesn't compile.

### How an operator rule is parsed

The rule gets one method, `ParseExpr(int minLevel = 0)`; in an ALL(\*) parser the return state comes first. `minLevel` is the loosest operator the call may apply:

1. It reads an operand: an atom, or a prefix form, whose own operand is read by calling `ParseExpr` with the form's level.
2. Then, while the next token starts an infix or postfix form: if the form's level is below `minLevel`, it returns, leaving the operator to the call waiting for this operand. Otherwise it makes the form's node around what it has so far and reads the operator. For an infix form, it reads the right operand by calling `ParseExpr` with the form's level plus one, or the form's level itself if the form is right associative.

Choosing among atoms and prefix forms, and among infix and postfix forms, are ordinary decisions. They use as much lookahead as they need, and ALL(\*) where no fixed amount will do: telling the cast `(a) b` from the parenthesized `(a) + b` takes four tokens.

Whether to apply another operator only ever looks at one token. Without precedence, the rule would be ambiguous there, since an operand's call could take the operator or leave it to its caller. Precedence is what settles that, at parse time, so there's no policy to give and no warning. A token that could start an operator is always taken as one, even where it could also follow the expression in the rest of the grammar.

Other things to know:

- **`entering` and `parsed` on the operator rule** run on every call, including the calls for operands, so `1 + 2` enters the rule twice: once for `2` and once for the whole.
- **`expected` on the operator rule** names it when no operand is found: "Expected an expression but found …".
- **Messages** say how many levels the rule has. `-r` shows it as `operators` with its levels, and shows each form's production as an operator form of it.

## Parse-time hooks and contextual terminals

Some languages can't be parsed from their tokens alone. In C, `T * x;` declares `x` if `T` names a type and multiplies otherwise, so the parser has to know which names are types, and that depends on the declarations it has already parsed. Two features handle this: hooks, which call your code as parsing goes, and contextual terminals, which let your code decide what a token is.

### Hooks

A hook names a method in the parser's partial class. Parsley declares it as a partial method, and you implement it in another part of the class:

| Attribute | On | Declared as | Called |
| --- | --- | --- | --- |
| `entering="M"` | non-terminal | `partial void M();` | as the rule starts, before any of it is read |
| `parsed="M"` | non-terminal | `partial void M(ParseNode node);` | as the rule ends, with its finished node. For a collapsed rule, the node its children went into. |
| `matched="M"` | terminal | `partial void M(Token token);` | each time the parser matches one of its tokens, after adding it to the tree |

A hook you don't implement does nothing, as C# partial methods do. Several rules can name the same method. Hooks run during parsing, so unlike [semantic actions](#semantic-actions), which evaluate a finished tree, they can affect the rest of the parse. If error recovery gives up partway through, some `parsed` hooks may not run for rules that were started.

### Contextual terminals

```xbnf
identifier= '[A-Za-z_][A-Za-z0-9_]*';
typedef_name<when="IsTypedefName">= identifier;
```

A rule with `when` whose whole definition is one terminal is a **contextual terminal**. The lexer never produces it: it isn't written to the lexer file, and it comes after every other terminal so lexer ids don't count it. Instead, each time the parser looks at an `identifier` token, it asks your method:

```csharp
private partial bool IsTypedefName(Token token, Token previous);
```

`previous` is the token before, as the lexer produced it (its SymbolId is -1 at the start of the input). When the method returns true, the parser treats the token as `typedef_name`. It asks every time it looks at the token, including when it looks ahead to choose between alternatives, so the answer reflects everything parsed up to that point. Unlike the other hooks, this method must be implemented, or the parser won't compile.

The parser may look several tokens ahead before it gets to them, and the hooks that change your state haven't run for those tokens yet. Basing the answer on the token and the one before it, rather than on where the parse has got to, keeps it the same at both times.

`c99.xbnf` and `C99Parser.Typedefs.cs` in `Parsley.Tests/c99` are a full example: they track C's scopes and typedef declarations to tell typedef names from identifiers.

The interpreter, `GrammarInterpreter`, takes a `ParseHooks` object with delegates in place of the methods.

## How rules become a parser

None of this needs to be written in the grammar, but it explains the messages Parsley gives and the code it produces.

### Lookahead

Each decision point (each `|`, `[ ]` and `{ }`) uses the fewest tokens of lookahead that tell its branches apart, up to 4 by default. Most decisions need one token. Ones that need more are noted in the synthesized code.

### Ambiguity

A decision whose branches still overlap after 4 tokens (or the `@maxk` limit) either needs more lookahead or is really ambiguous. With the engine chosen automatically (the default), Parsley leaves it to ALL(\*), which looks as far ahead as it takes at parse time, with a message saying so. If the input really is ambiguous there, ALL(\*) settles it the way the defaults below do: the first alternative wins, and a loop or optional keeps matching.

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
- **An [operator rule](#operator-rules)** isn't rewritten as a loop. Its operator forms start or end with the rule on purpose, and precedence climbing parses them.

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
| [Operator rules](#operator-rules): | |
| An operator rule that's collapsed | Error |
| An operator rule with no atom | Error |
| An atom, or the operator of a prefix or postfix form, that can match nothing | Error |
| An alternative that leads back to the operator rule before reading anything, other than by starting with it (`E<operators>= E "+" E \| P; P= E "." name \| name;`) | Error: write it as an operator form instead |
| A form's production used anywhere but as an alternative of its operator rule, or used twice | Error |
| A form's production with more than one alternative, all of them forms | Error: group the operators instead, as in `E ( a \| b ) E` |
| A form's production that's collapsed or is an operator rule itself | Error |
| `assoc` that isn't `left` or `right`, or both on one form | Error |
| `assoc` on a form that isn't infix, or outside an operator rule | Warning: it's ignored |
| `entering` or `parsed` on a form's production | Warning: it's ignored |
| An operator rule with no operator forms | Warning |
| A form's production with no action, when its operator rule has one | Message: the operator rule's action evaluates it |

Messages carry the file, line and column of the rule or expression they're about.

## Symbol ids

The synthesized parser numbers symbols in this order:

1. non-terminals, in rule order
2. terminals, in declaration order, including hidden ones, followed by implicit terminals
3. `#EOS` (end of input) and `#ERROR` (unrecognized input)

A lexer reports terminals numbered from 0 in that same terminal order. The parser adds the number of non-terminals as each token arrives. A negative id, or one past the last terminal, is treated as `#ERROR`.