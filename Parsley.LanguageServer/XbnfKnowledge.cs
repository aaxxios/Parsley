using System.Collections.Generic;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// The attributes and directives XBNF understands, for completion and hover. Summarized from XBNF.md.
    /// </summary>
    static class XbnfKnowledge
    {
        public static readonly Dictionary<string, string> Attributes = new Dictionary<string, string>
        {
            ["start"] = "Flag. This rule is the start symbol; otherwise the first non-terminal is.",
            ["collapsed"] = "Flag. The rule (or terminal) gets no node in the parse tree; its children go into its parent's.",
            ["hidden"] = "Flag, on a terminal. The lexer matches it but never reports it (whitespace, comments).",
            ["terminal"] = "`true` or `false`. Forces the rule to be a terminal or a non-terminal.",
            ["nowarn"] = "Flag. Suppresses the \"unreachable from the start symbol\" warning.",
            ["expected"] = "String. How the rule or alternation is named in error messages: `expected=\"a statement\"`.",
            ["policy"] = "`greedy`, `lazy` or `first`. Settles ambiguous decisions in this rule or group.",
            ["sync"] = "On a rule: terminals where error recovery may stop, as a string. On a terminal: a flag making it a strong restart point.",
            ["type"] = "String. The C# type the rule's `EvaluateXxx` returns: `type=\"double\"`.",
            ["state"] = "String, on the start rule. The C# type of the evaluator's `State` and `Evaluate`'s `state` argument.",
            ["entering"] = "Method name. Called with no arguments as the parser starts this rule.",
            ["parsed"] = "Method name. Called with the finished parse node as this rule ends.",
            ["operators"] = "Flag. The alternatives are operators, tightest first, parsed by precedence climbing.",
            ["assoc"] = "`left` or `right`. How an operator form associates.",
            ["when"] = "Method name, on a terminal. Makes a contextual terminal, read by the parser from another terminal's tokens.",
            ["matched"] = "Method name, on a terminal. Called with each token of this terminal the parser matches.",
        };

        public static readonly Dictionary<string, string[]> AttributeValues = new Dictionary<string, string[]>
        {
            ["policy"] = new[] { "greedy", "lazy", "first" },
            ["assoc"] = new[] { "left", "right" },
            ["terminal"] = new[] { "true", "false" },
            ["start"] = new[] { "true", "false" },
            ["collapsed"] = new[] { "true", "false" },
            ["hidden"] = new[] { "true", "false" },
            ["nowarn"] = new[] { "true", "false" },
            ["operators"] = new[] { "true", "false" },
        };

        public static readonly Dictionary<string, string> Directives = new Dictionary<string, string>
        {
            ["import"] = "`@import \"file.xbnf\";` Reads another file's rules in, relative to this file.",
            ["namespace"] = "`@namespace \"My.Parsers\";` The synthesized parser's namespace. Ignored by Parsley.Generator.",
            ["class"] = "`@class \"MyParser\";` The synthesized parser's class name. Ignored by Parsley.Generator.",
            ["evaluator"] = "`@evaluator \"MyEvaluator\";` The synthesized evaluator's class name. Ignored by Parsley.Generator.",
            ["using"] = "`@using \"System.Text\";` A namespace for semantic actions to use.",
            ["maxk"] = "`@maxk 2;` The most tokens a fixed-lookahead decision looks at; 4 if not given.",
        };
    }
}
