using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// A decision that needs more than one token of lookahead, where it is in the grammar: the parse engine it
    /// asks for, shown in the editor as a glyph with the details in a tooltip
    /// </summary>
    sealed class DecisionHint
    {
        // hollow for a fixed lookahead, solid for adaptive prediction. The editor's theme colors them.
        public const string LLkGlyph = "◇";
        public const string AllStarGlyph = "◆";
        static readonly string[] s_superscripts = { "⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹" };

        DecisionHint(string rule, string ruleText, int offsetInRule, bool adaptive, int depth, string tooltip)
        {
            Rule = rule; RuleText = ruleText; OffsetInRule = offsetInRule; IsAllStar = adaptive; Depth = depth; Tooltip = tooltip;
        }
        // where it is, relative to the start of its rule, and the rule's text when it was analyzed: an edit
        // elsewhere moves the rule but leaves the hint in place, and an edit to the rule hides the hint
        // until the next analysis
        public string Rule { get; }
        public string RuleText { get; }
        public int OffsetInRule { get; }
        /// <summary>
        /// True when no fixed lookahead decides it, so it needs ALL(*)
        /// </summary>
        public bool IsAllStar { get; }
        /// <summary>
        /// The k it needs, when it's not ALL(*)
        /// </summary>
        public int Depth { get; }
        /// <summary>
        /// Markdown saying what it needs and why
        /// </summary>
        public string Tooltip { get; }
        /// <summary>
        /// The glyph, with k after it in superscript for LL(k)
        /// </summary>
        public string Label => IsAllStar ? AllStarGlyph : LLkGlyph + string.Concat(Depth.ToString().Select(c => s_superscripts[c - '0']));
        public string Engine => IsAllStar ? "ALL(*)" : $"LL({Depth})";

        /// <summary>
        /// The hints for the decisions an analysis of the document found in the document itself, not in what it imports
        /// </summary>
        public static List<DecisionHint> From(GrammarAnalysis a, Document doc)
        {
            var result = new List<DecisionHint>();
            var defs = doc.Scan.Definitions.Where(t => t.RuleEnd > t.Start).ToList();
            foreach (var d in a.Decisions)
            {
                var allStar = d.IsAdaptive && 0 == d.Depth;
                if (!allStar && 1 >= d.Depth) continue;
                var n = d.Node;
                if ((0 >= n.Line && 0 > n.Position) || (null != n.Filename && !Documents.PathComparer.Equals(System.IO.Path.GetFullPath(n.Filename), doc.Path))) continue;
                var offset = doc.ToOffset(n);
                var def = defs.FirstOrDefault(t => t.Start <= offset && offset < t.RuleEnd);
                if (null == def) continue;
                result.Add(new DecisionHint(def.Text, doc.Text.Substring(def.Start, def.RuleEnd - def.Start), offset - def.Start, allStar, d.Depth, _Tooltip(a, d, allStar)));
            }
            return result;
        }

        /// <summary>
        /// Where the hint is in the document now, or -1 when its rule has changed or gone since it was analyzed
        /// </summary>
        public int OffsetIn(Document doc)
        {
            foreach (var t in doc.Scan.Definitions)
            {
                if (t.Text != Rule || t.RuleEnd - t.Start != RuleText.Length) continue;
                if (string.CompareOrdinal(doc.Text, t.Start, RuleText, 0, RuleText.Length) == 0) return t.Start + OffsetInRule;
            }
            return -1;
        }

        public JsonObject ToInlayHint(Document doc, int offset)
        {
            var (line, character) = doc.ToPosition(offset);
            return new JsonObject
            {
                ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["label"] = Label,
                // themes can color the two kinds apart: Type for ALL(*), Parameter for LL(k)
                ["kind"] = IsAllStar ? 1 : 2,
                ["tooltip"] = new JsonObject { ["kind"] = "markdown", ["value"] = Tooltip },
                ["paddingRight"] = true,
            };
        }

        static string _Tooltip(GrammarAnalysis a, Decision d, bool allStar)
        {
            var what = d.Kind switch
            {
                DecisionKind.Loop => "whether to go around this loop again",
                DecisionKind.Optional => "whether to take this optional part",
                _ => "between these alternatives",
            };
            var choose = d.Kind == DecisionKind.Alternation ? "choose " + what : "decide " + what;
            string tooltip;
            if (allStar)
            {
                var examples = string.Join(", ", d.Ambiguities.Take(3).Select(s => "`" + s.ToString(a.Symbols) + "`"));
                var more = d.AmbiguityCount > 3 ? $" and {d.AmbiguityCount - 3} more" : "";
                // the heading names the engine, and an ALL(*) decision makes the whole grammar ALL(*), so the engine
                // isn't said again
                tooltip = $"**ALL(\\*)**: no fixed lookahead can {choose}. Within {a.MaxK} tokens, it can't tell on {examples}{more}, "
                    + "so it's predicted adaptively at parse time.";
            }
            else
            {
                tooltip = $"**LL({d.Depth})**: it takes {d.Depth} tokens of lookahead to {choose}.";
                if (null != d.AppliedPolicy)
                    tooltip += $" Where {d.Depth} tokens still aren't enough, `<policy=\"{d.AppliedPolicy}\">` settles it.";
                // the engine only when it isn't the LL(k) the heading implies
                if (d.IsAdaptive)
                    tooltip += " The grammar needs ALL(\\*) elsewhere, so this decision is predicted adaptively too.";
                else if (ParserEngine.AllStar == a.Engine)
                    tooltip += " The grammar's engine is ALL(\\*).";
            }
            return tooltip + $"\n\n*{d.Production.Name}, decision {d.Index}*";
        }
    }
}
