using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
    /// <summary>
    /// How an alternative of an operator rule uses the rule itself
    /// </summary>
    public enum OperatorKind
    {
        /// <summary>
        /// Neither starts nor ends with the rule: a number, a name, a parenthesized expression
        /// </summary>
        Atom,
        /// <summary>
        /// Ends with the rule but doesn't start with it: <c>sub Expr</c>
        /// </summary>
        Prefix,
        /// <summary>
        /// Starts and ends with the rule: <c>Expr add Expr</c>, <c>Expr "?" Expr ":" Expr</c>
        /// </summary>
        Infix,
        /// <summary>
        /// Starts with the rule but doesn't end with it: <c>Expr "++"</c>, <c>Expr lbracket Expr rbracket</c>
        /// </summary>
        Postfix
    }

    /// <summary>
    /// One alternative of an operator rule, as precedence climbing parses it
    /// </summary>
    public sealed class OperatorForm
    {
        internal OperatorForm(OperatorRule rule, OperatorKind kind, int level, bool right, Production? production, Expression option, IReadOnlyList<Expression> op, SymbolRef? operand, string text)
        {
            Rule = rule;
            Kind = kind;
            Level = level;
            IsRightAssociative = right;
            Production = production;
            Option = option;
            Operator = op;
            Operand = operand;
            Text = text;
        }
        public OperatorRule Rule { get; }
        public OperatorKind Kind { get; }
        /// <summary>
        /// The precedence: higher binds tighter. The first alternative of the rule has the highest. 0 for an atom.
        /// </summary>
        public int Level { get; }
        /// <summary>
        /// For an infix form: <c>a op b op c</c> groups as <c>a op (b op c)</c>
        /// </summary>
        public bool IsRightAssociative { get; }
        /// <summary>
        /// The form's own production, when the rule's alternative is a reference to one (<c>Sum= Expr add Expr;</c>),
        /// else null and its nodes are the rule's
        /// </summary>
        public Production? Production { get; }
        /// <summary>
        /// The non-terminal of the nodes an application of the form makes
        /// </summary>
        public string Symbol => Production?.Name ?? Rule.Production.Name;
        /// <summary>
        /// The form in the analyzed grammar: the alternative of <see cref="OperatorRule.Primary"/> or of the
        /// loop's body that a decision chooses. For prefix and infix forms, its last item is <see cref="Operand"/>.
        /// </summary>
        public Expression Option { get; }
        /// <summary>
        /// What lies between the operands: before the operand of a prefix form, after the left operand of a
        /// postfix form. For an atom, the whole form.
        /// </summary>
        public IReadOnlyList<Expression> Operator { get; }
        /// <summary>
        /// The trailing operand of a prefix or infix form, which is parsed at this form's level, or null
        /// </summary>
        public SymbolRef? Operand { get; }
        /// <summary>
        /// The form as written in the grammar
        /// </summary>
        public string Text { get; }
        public override string ToString() => Text;
    }

    /// <summary>
    /// A production marked <c>operators</c>: its alternatives are operator forms of itself, listed tightest
    /// first, and it's parsed by precedence climbing. For analysis it is rewritten without precedence as
    /// <c>Rule= ( atoms | prefix forms ) { infix and postfix tails }</c>, which accepts the same input;
    /// precedence only decides how the tree is shaped, and whether a call keeps applying operators or leaves
    /// them to its caller.
    /// </summary>
    public sealed class OperatorRule
    {
        readonly List<OperatorForm> _forms = new();
        internal OperatorRule(Production production, string text)
        {
            Production = production;
            Text = text;
        }
        public Production Production { get; }
        /// <summary>
        /// The rule as written
        /// </summary>
        public string Text { get; }
        /// <summary>
        /// The atoms and prefix forms: an alternation, or the single one
        /// </summary>
        public Expression Primary { get; internal set; } = null!;
        /// <summary>
        /// The loop over the infix and postfix forms, or null if there are none
        /// </summary>
        public Repeat? Loop { get; internal set; }
        /// <summary>
        /// The forms, in the order they're written
        /// </summary>
        public IReadOnlyList<OperatorForm> Forms => _forms;
        internal void Add(OperatorForm f) => _forms.Add(f);
        public override string ToString() => Text;
    }

    /// <summary>
    /// Recognizes operator rules and rewrites them for analysis
    /// </summary>
    internal static class OperatorRules
    {
        public static void Rewrite(Grammar g, IList<GrammarMessage> messages)
        {
            var rules = g.Productions.Where(p => p.Attributes.IsSet("operators")).ToList();
            // assoc only means something on an operator form
            var forms = new HashSet<string>(rules.Select(p => p.Name));
            foreach (var p in rules)
                foreach (var o in LeftRecursion.Options(p.Expression))
                    if (o is SymbolRef sr && null != sr.Name) forms.Add(sr.Name);
            foreach (var p in g.Productions)
            {
                if (forms.Contains(p.Name)) continue;
                var stray = p.Attributes.Find("assoc") ?? p.Expression.Descendants().Select(e => e.Attributes.Find("assoc")).FirstOrDefault(a => null != a);
                if (null != stray)
                    messages.Add(new GrammarMessage(ErrorLevel.Warning, $"\"{p.Name}\" isn't an operator rule or one of its forms, so assoc is ignored", stray));
            }
            if (0 == rules.Count) return;
            // every place each name is referenced, to make sure an operator form's own production is used only as one
            var refs = new Dictionary<string, List<SymbolRef>>();
            foreach (var p in g.Productions)
                foreach (var sr in p.Expression.Descendants().OfType<SymbolRef>())
                    if (null != sr.Name)
                    {
                        if (!refs.TryGetValue(sr.Name, out var list)) refs.Add(sr.Name, list = new List<SymbolRef>());
                        list.Add(sr);
                    }
            var claimed = new Dictionary<string, string>();
            var hasEvaluator = null != g.GetProduction(g.StartSymbol!)?.Action;
            var rewritten = new List<(Production P, OperatorRule Rule, Expression Expression, List<Production> Own)>();
            foreach (var p in rules)
            {
                var errors = messages.Count(m => ErrorLevel.Error == m.Level);
                var r = _Read(g, p, refs, claimed, hasEvaluator, messages, out var expression, out var own);
                if (null != r && errors == messages.Count(m => ErrorLevel.Error == m.Level))
                    rewritten.Add((p, r, expression!, own));
            }
            if (messages.Any(m => ErrorLevel.Error == m.Level)) return;
            foreach (var (p, r, expression, own) in rewritten)
            {
                p.Written = p.Expression;
                p.Expression = expression;
                p.Operators = r;
                foreach (var q in own)
                {
                    // its nodes are made by the operator rule; it has no input of its own to parse
                    var empty = new EmptyExpression();
                    empty.CopyLocation(q.Expression);
                    q.Written = q.Expression;
                    q.Expression = empty;
                    q.OperatorOwner = r;
                }
                var levels = r.Forms.Where(f => OperatorKind.Atom != f.Kind).Select(f => f.Level).Distinct().Count();
                messages.Add(new GrammarMessage(ErrorLevel.Message, $"\"{p.Name}\" is an operator rule with {levels} level{(1 == levels ? "" : "s")} of precedence, parsed by precedence climbing", p));
            }
            // precedence climbing can't parse a rule that leads back to itself before reading anything
            var nullable = LeftRecursion.ComputeNullable(g);
            foreach (var (p, _, _, _) in rewritten)
            {
                var path = LeftRecursion.PathBackTo(g, p.Name, nullable);
                if (null != path)
                    messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" leads back to itself before reading any input ({string.Join(" → ", path)}), which precedence climbing can't parse. Write that alternative as an operator form of \"{p.Name}\", one that starts with \"{p.Name}\", instead", p));
            }
        }

        static OperatorKind _Shape(string rule, List<Expression> items)
        {
            if (0 == items.Count) return OperatorKind.Atom;
            var starts = items[0] is SymbolRef s && s.Name == rule;
            var ends = items[items.Count - 1] is SymbolRef e && e.Name == rule;
            if (starts && ends) return OperatorKind.Infix;
            if (starts) return OperatorKind.Postfix;
            if (ends) return OperatorKind.Prefix;
            return OperatorKind.Atom;
        }

        static string _Describe(OperatorKind k) => k switch
        {
            OperatorKind.Prefix => "a prefix form",
            OperatorKind.Infix => "an infix form",
            OperatorKind.Postfix => "a postfix form",
            _ => "an atom"
        };

        static OperatorRule? _Read(Grammar g, Production p, Dictionary<string, List<SymbolRef>> refs, Dictionary<string, string> claimed, bool hasEvaluator, IList<GrammarMessage> messages, out Expression? expression, out List<Production> own)
        {
            expression = null;
            own = new List<Production>();
            if (p.IsCollapsed)
            {
                messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" is an operator rule, so it can't be collapsed: every operator it applies makes a node", p));
                return null;
            }
            var rule = new OperatorRule(p, XbnfWriter.Write(p, false));
            var options = LeftRecursion.Options(p.Expression);
            var primary = new List<Expression>();
            var tails = new List<Expression>();
            var nullable = LeftRecursion.ComputeNullable(g);
            for (var i = 0; i < options.Count; ++i)
            {
                var option = options[i];
                // the first alternative binds tightest
                var level = options.Count - i;
                Production? q = null;
                var body = option;
                var text = option.ToString();
                if (option is SymbolRef sr && sr.Name != p.Name && g.GetProduction(sr.Name!) is Production candidate)
                {
                    if (OperatorKind.Atom != _Shape(p.Name, LeftRecursion.Items(candidate.Expression)))
                    {
                        q = candidate;
                        body = candidate.Expression;
                        text = XbnfWriter.Write(candidate, false).TrimEnd(';');
                    }
                    else if (1 < LeftRecursion.Options(candidate.Expression).Count && LeftRecursion.Options(candidate.Expression).All(o => OperatorKind.Atom != _Shape(p.Name, LeftRecursion.Items(o))))
                    {
                        messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{candidate.Name}\" has alternatives that are operator forms of \"{p.Name}\". A production for an operator form must be exactly one form; to give operators the same precedence, group them: {p.Name} ( a | b ) {p.Name}", candidate));
                        continue;
                    }
                }
                var items = LeftRecursion.Items(body);
                var kind = _Shape(p.Name, items);
                if (null != q)
                {
                    if (claimed.TryGetValue(q.Name, out var other))
                        messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{q.Name}\" is already an operator form of \"{other}\", so it can't be used again here", option));
                    claimed[q.Name] = p.Name;
                    if (refs.TryGetValue(q.Name, out var uses))
                        foreach (var use in uses.Where(u => !ReferenceEquals(u, option)))
                            messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{q.Name}\" is an operator form of \"{p.Name}\", so it can only be used as an alternative of \"{p.Name}\"", use));
                    if (q.IsCollapsed)
                        messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{q.Name}\" is an operator form of \"{p.Name}\", so it can't be collapsed: each time its operator is applied it makes a node", q));
                    if (q.Attributes.IsSet("operators"))
                        messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{q.Name}\" is an operator form of \"{p.Name}\", so it can't be an operator rule itself", q));
                    foreach (var hook in new[] { "entering", "parsed" })
                        if (null != q.Attributes.Get(hook))
                            messages.Add(new GrammarMessage(ErrorLevel.Warning, $"\"{q.Name}\" is an operator form of \"{p.Name}\", which parses it, so its {hook} is ignored. Use \"{p.Name}\"'s instead", q.Attributes.Find(hook)));
                    if (null == q.Action && hasEvaluator && null != p.Action)
                        messages.Add(new GrammarMessage(ErrorLevel.Message, $"\"{q.Name}\" has no semantic action, so \"{p.Name}\"'s action evaluates its nodes", q));
                    own.Add(q);
                }
                // associativity, from the form's production or any element of the form
                var assoc = new List<GrammarAttribute>();
                if (null != q) assoc.AddRange(q.Attributes.Where(a => "assoc" == a.Name));
                assoc.AddRange(option.Attributes.Where(a => "assoc" == a.Name));
                foreach (var e in body.Descendants())
                    if (!ReferenceEquals(e, option) || null != q)
                        assoc.AddRange(e.Attributes.Where(a => "assoc" == a.Name));
                var values = assoc.Select(a => a.Value as string).Where(v => "left" == v || "right" == v).Distinct().ToList();
                if (1 < values.Count)
                    messages.Add(new GrammarMessage(ErrorLevel.Error, $"{text} is marked both assoc=left and assoc=right", assoc[0]));
                var right = 1 == values.Count && "right" == values[0];
                if (0 < assoc.Count && OperatorKind.Infix != kind)
                    messages.Add(new GrammarMessage(ErrorLevel.Warning, $"{text} is {_Describe(kind)}, so assoc is ignored: only infix forms associate", assoc[0]));
                Expression newOption;
                List<Expression> op;
                SymbolRef? operand = null;
                switch (kind)
                {
                    case OperatorKind.Atom:
                        if (LeftRecursion.IsNullable(body, nullable))
                        {
                            messages.Add(new GrammarMessage(ErrorLevel.Error, $"In the operator rule \"{p.Name}\", the atom {text} can match nothing, which would let an operand be missing", option));
                            continue;
                        }
                        newOption = body.Clone();
                        op = new List<Expression> { newOption };
                        primary.Add(newOption);
                        break;
                    case OperatorKind.Prefix:
                    case OperatorKind.Infix:
                        {
                            if (OperatorKind.Infix == kind && 1 == items.Count)
                            {
                                messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" derives itself directly ({p.Name}= {p.Name}), which makes the grammar ambiguous", option));
                                continue;
                            }
                            var from = OperatorKind.Infix == kind ? 1 : 0;
                            var middle = items.Skip(from).Take(items.Count - 1 - from).ToList();
                            if (OperatorKind.Prefix == kind && middle.All(m => LeftRecursion.IsNullable(m, nullable)))
                            {
                                messages.Add(new GrammarMessage(ErrorLevel.Error, $"In the operator rule \"{p.Name}\", the operator of the prefix form {text} can match nothing, so the form would lead straight back to \"{p.Name}\"", option));
                                continue;
                            }
                            op = middle.Select(m => m.Clone()).ToList();
                            operand = new SymbolRef(p.Name);
                            operand.CopyLocation(items[items.Count - 1]);
                            foreach (var a in items[items.Count - 1].Attributes) operand.Attributes.Add(a.Clone());
                            newOption = LeftRecursion.MakeSeq(op.Append(operand).ToList(), option);
                            (OperatorKind.Prefix == kind ? primary : tails).Add(newOption);
                            break;
                        }
                    default:
                        {
                            var middle = items.Skip(1).ToList();
                            if (middle.All(m => LeftRecursion.IsNullable(m, nullable)))
                            {
                                messages.Add(new GrammarMessage(ErrorLevel.Error, $"In the operator rule \"{p.Name}\", the operator of the postfix form {text} can match nothing, so it could be applied forever", option));
                                continue;
                            }
                            op = middle.Select(m => m.Clone()).ToList();
                            // the option is the key the form is found by, so it can't be the operator itself: with one operator
                            // item MakeSeq hands back that item, and emitting the operator would find this form again, forever
                            if (1 == op.Count)
                            {
                                newOption = new Sequence(op);
                                newOption.CopyLocation(option);
                            }
                            else
                                newOption = LeftRecursion.MakeSeq(op, option);
                            tails.Add(newOption);
                            break;
                        }
                }
                rule.Add(new OperatorForm(rule, kind, OperatorKind.Atom == kind ? 0 : level, right, q, newOption, op, operand, text));
            }
            if (!primary.Any(o => OperatorKind.Atom == rule.Forms.First(f => ReferenceEquals(f.Option, o)).Kind))
            {
                // an atom that was rejected has already been reported
                if (rule.Forms.Count < options.Count) return null;
                messages.Add(new GrammarMessage(ErrorLevel.Error, $"The operator rule \"{p.Name}\" has no atom, an alternative that neither starts nor ends with \"{p.Name}\", so it has nothing to apply its operators to", p));
                return null;
            }
            if (rule.Forms.All(f => OperatorKind.Atom == f.Kind))
                messages.Add(new GrammarMessage(ErrorLevel.Warning, $"\"{p.Name}\" is marked operators, but none of its alternatives is an operator form: one that starts or ends with \"{p.Name}\"", p));
            var primaryExpr = LeftRecursion.MakeAlt(primary, p.Expression);
            // attributes on the rule's alternation as a whole, such as expected, apply to the choice of operand
            if (p.Expression is Alternation && primaryExpr is Alternation)
                foreach (var a in p.Expression.Attributes) primaryExpr.Attributes.Add(a.Clone());
            rule.Primary = primaryExpr;
            if (0 == tails.Count)
            {
                expression = primaryExpr;
                return rule;
            }
            var loop = new Repeat(LeftRecursion.MakeAlt(tails, p.Expression), 0);
            loop.CopyLocation(p.Expression);
            rule.Loop = loop;
            var seq = new Sequence(primaryExpr, loop);
            seq.CopyLocation(p.Expression);
            expression = seq;
            return rule;
        }
    }
}