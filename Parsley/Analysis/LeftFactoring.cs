using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	/// <summary>
	/// Left factoring, where the grammar asks for it with <c>@factor true;</c> or <c>&lt;factor&gt;</c>: an
	/// alternation whose alternatives can start with the same token gets that token in front of it, and a new
	/// alternation of what each alternative goes on with after it, so one token of lookahead can tell them
	/// apart where it couldn't before. <c>A= x B | x C</c> becomes <c>A= x ( B | C )</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The shared token is often inside the productions the alternatives start with, as in
	/// <c>S= Call | Name; Call= ident "(" ")"; Name= ident;</c>. To bring it out, factoring inlines those
	/// productions along the left edge (<see cref="InlinedProduction"/>), splitting their alternations,
	/// optionals and loops into alternatives where it has to, until each alternative starts with a token.
	/// </para>
	/// <para>
	/// None of this changes the tree. An inlined production makes the node a call to it would have, located
	/// where it would have been, and takes over the tokens factoring moved in front of the decision, so its
	/// children are what they would have been. No production or node is added: the rewritten alternations
	/// and sequences are groups, which have no nodes. A production's <c>parsed</c> hook and its place in
	/// error recovery stay with it.
	/// </para>
	/// <para>
	/// Analysis keeps factoring only where it makes every decision it made one token deep, and analyzes the grammar
	/// again without the rest (<c>exclude</c>), so where it doesn't help, the grammar is as written.
	/// </para>
	/// <para>
	/// It leaves alone what it can't do that way: operator rules and their forms; productions with an
	/// <c>entering</c> hook, which would run after the tokens moved in front of it instead of before; and
	/// productions marked <c>factor=false</c>. Where an alternative's way to the shared token goes through
	/// one of those, or through a left-recursive loop, a nullable start it can't split, or more than
	/// <see cref="MaxHoist"/> tokens, that token isn't factored, and the decision is made as it would be
	/// without factoring, by more lookahead or ALL(*).
	/// </para>
	/// </remarks>
	internal static class LeftFactoring
	{
		/// <summary>
		/// The most tokens moved in front of one decision: the runtime remembers that many to locate nodes
		/// </summary>
		public const int MaxHoist = 16;
		/// <summary>
		/// The most productions inlined to factor one alternation, nested ones included
		/// </summary>
		public const int MaxExpansions = 64;
		/// <summary>
		/// How deep the expressions factoring looks into may nest
		/// </summary>
		const int MaxDepth = 400;
		/// <summary>
		/// The most ways factoring one alternation may split its alternatives into: a sequence of alternations
		/// multiplies out, so this keeps the parser from growing out of hand
		/// </summary>
		public const int MaxVariants = 300;
		/// <summary>
		/// The most expression nodes factoring one alternation may add: past this, it's left as written
		/// </summary>
		public const int MaxGrowth = 600;

		// one way an expression can go: Tail set means it's the shared token followed by Tail; otherwise Form,
		// which doesn't start with the token
		sealed class Variant
		{
			public Expression? Form;
			public Expression? Tail;
			public static Variant Of(Expression form) => new Variant { Form = form };
			public static Variant After(Expression tail) => new Variant { Tail = tail };
		}

		sealed class Context
		{
			public Grammar G = null!;
			public bool? Global;
			public HashSet<string> Nullable = null!;
			public readonly Dictionary<string, HashSet<string>> First = new();
			public readonly HashSet<string> Done = new();
			public readonly HashSet<string> InProgress = new();
			public readonly Dictionary<string, int> TerminalOrder = new();
			public IList<GrammarMessage> Messages = null!;
			public ISet<string>? Exclude;
			public int Expansions;
			public int Variants;
			public int Depth;
		}

		static bool? _Flag(AttributeList attributes) => attributes.Get("factor") is bool b ? b : null;

		/// <summary>
		/// Factors the grammar's alternations where it asks for it. Changes the grammar.
		/// </summary>
		/// <param name="exclude">Alternations, by <see cref="Alternation.FactorKey"/>, to leave as written: ones
		/// factoring couldn't make decidable on one token</param>
		public static void Factor(Grammar g, IList<GrammarMessage> messages, ISet<string>? exclude = null)
		{
			var global = g.Factor;
			// nothing asks for it: the grammar is left as written
			if (true != global && !g.Productions.Any(p => true == _Flag(p.Attributes) || p.Expression.Descendants().Any(e => true == _Flag(e.Attributes))))
				return;
			var ctx = new Context { G = g, Global = global, Messages = messages, Nullable = LeftRecursion.ComputeNullable(g), Exclude = exclude };
			// each alternation as written, by its rule and its place in it, so a later analysis of the same grammar
			// knows it again
			foreach (var p in g.Productions)
			{
				var n = 0;
				foreach (var a in p.Expression.Descendants().OfType<Alternation>())
					a.FactorKey = $"{p.Name}#{++n}";
			}
			for (var i = 0; i < g.Terminals.Count; ++i)
				ctx.TerminalOrder[g.Terminals[i].Name] = i;
			_ComputeFirst(ctx);
			foreach (var p in g.Productions.ToList())
				_Ensure(p, ctx);
		}

		// factors a production's alternations, once. A production is factored before it's inlined anywhere, so
		// what's inlined is the factored body
		static void _Ensure(Production p, Context ctx)
		{
			if (ctx.Done.Contains(p.Name) || !ctx.InProgress.Add(p.Name)) return;
			// this can happen while an alternation that inlines it is being factored, whose budget it mustn't spend
			var expansions = ctx.Expansions;
			var variants = ctx.Variants;
			if (null == p.Operators && null == p.OperatorOwner)
				p.Expression = _Walk(p.Expression, p, _Flag(p.Attributes) ?? ctx.Global ?? false, ctx);
			ctx.Expansions = expansions;
			ctx.Variants = variants;
			ctx.InProgress.Remove(p.Name);
			ctx.Done.Add(p.Name);
		}

		// factors the alternations in an expression, innermost first. on: whether factoring is asked for here
		static Expression _Walk(Expression e, Production p, bool on, Context ctx)
		{
			on = _Flag(e.Attributes) ?? on;
			switch (e)
			{
				case Sequence seq:
					for (var i = 0; i < seq.Items.Count; ++i)
						seq.Items[i] = _Walk(seq.Items[i], p, on, ctx);
					return seq;
				case OptionalExpression o:
					o.Body = _Walk(o.Body, p, on, ctx);
					return o;
				case Repeat r:
					r.Body = _Walk(r.Body, p, on, ctx);
					return r;
				case InlinedProduction ip:
					ip.Body = _Walk(ip.Body, p, on, ctx);
					return ip;
				case Alternation a:
					for (var i = 0; i < a.Options.Count; ++i)
						a.Options[i] = _Walk(a.Options[i], p, on, ctx);
					if (!on) return a;
					if (null != a.FactorKey && true == ctx.Exclude?.Contains(a.FactorKey))
					{
						ctx.Messages.Add(new GrammarMessage(ErrorLevel.Message, $"Didn't left factor {a} in \"{p.Name}\": factored, it would still take more than one token to decide", a));
						return a;
					}
					ctx.Expansions = 0;
					ctx.Variants = 0;
					var before = a.ToString();
					// factoring builds new nodes and leaves these alone, so they're what to go back to
					var original = a.Options.ToList();
					var size = a.Descendants().Count();
					var tokens = _FactorAlternation(a, p, ctx, 0);
					if (0 < tokens.Count && size + MaxGrowth < a.Descendants().Count())
					{
						a.Options.Clear();
						a.Options.AddRange(original);
						ctx.Messages.Add(new GrammarMessage(ErrorLevel.Message, $"Didn't left factor {before} in \"{p.Name}\": it would grow by more than {MaxGrowth} nodes", a));
						return a;
					}
					if (0 < tokens.Count)
						a.Factored = true;
					if (0 < tokens.Count)
						ctx.Messages.Add(new GrammarMessage(ErrorLevel.Message, $"Left factored {before} in \"{p.Name}\" on {string.Join(", ", tokens.Select(t => $"\"{t}\""))}", a));
					// kept even when it's down to one alternative, so it still reports what was expected there
					return a;
				default:
					return e;
			}
		}

		// factors one alternation in place, and the alternations factoring makes inside it. Returns the tokens
		// factored at this level
		static List<string> _FactorAlternation(Alternation a, Production p, Context ctx, int depth)
		{
			var factored = new List<string>();
			if (depth >= MaxHoist) return factored;
			// an alternative that can match nothing is chosen by what follows the alternation, which factoring
			// would reorder against the others: those are left to lookahead
			if (a.Options.Any(o => _Nullable(o, ctx))) return factored;
			var skip = new HashSet<string>();
			while (true)
			{
				var firsts = a.Options.Select(o => _First(o, ctx)).ToList();
				// the earliest alternative's earliest declared token that another alternative can start with too
				string? t = null;
				foreach (var f in firsts)
				{
					foreach (var x in f.Where(x => !skip.Contains(x)).OrderBy(x => ctx.TerminalOrder.TryGetValue(x, out var i) ? i : int.MaxValue))
						if (1 < firsts.Count(g => g.Contains(x))) { t = x; break; }
					if (null != t) break;
				}
				if (null == t) break;
				var saved = ctx.Expansions;
				var savedVariants = ctx.Variants;
				var options = new List<Expression?>();
				var tails = new List<Expression>();
				var group = -1;
				var stuck = false;
				for (var i = 0; i < a.Options.Count; ++i)
				{
					if (!firsts[i].Contains(t))
					{
						options.Add(a.Options[i]);
						continue;
					}
					var variants = _Expose(a.Options[i], t, ctx);
					if (null == variants || ctx.Expansions > MaxExpansions)
					{
						stuck = true;
						break;
					}
					foreach (var v in variants)
					{
						if (null != v.Tail)
						{
							if (0 > group)
							{
								group = options.Count;
								options.Add(null);
							}
							tails.Add(v.Tail);
						}
						else
							options.Add(v.Form!);
					}
				}
				if (stuck || 2 > tails.Count)
				{
					// leave it as it was, for lookahead to decide
					skip.Add(t);
					ctx.Expansions = saved;
					ctx.Variants = savedVariants;
					continue;
				}
				var inner = new Alternation(tails) { FactoredTail = true, FactorKey = a.FactorKey };
				inner.CopyLocation(a);
				foreach (var attr in a.Attributes)
					if ("policy" == attr.Name || "maxk" == attr.Name || "factor" == attr.Name)
						inner.Attributes.Add(attr.Clone());
				_FactorAlternation(inner, p, ctx, depth + 1);
				var token = new SymbolRef(t);
				token.CopyLocation(a);
				var seq = new Sequence(token, inner);
				seq.CopyLocation(a);
				options[group] = seq;
				a.Options.Clear();
				a.Options.AddRange(options!);
				factored.Add(t);
			}
			return factored;
		}

		#region Exposing the shared token
		// the ways e can go, in the order its decisions would try them, each either the token t then a tail, or
		// something that doesn't start with t. Null if t can't be brought to the front of every way that starts with
		// it. The result shares no nodes with e that it doesn't use only once, and e isn't changed.
		static List<Variant>? _Expose(Expression e, string t, Context ctx)
		{
			if (MaxVariants < ctx.Variants) return null;
			// the grammar's own limits keep this shallow; this one is for grammars with errors analysis reports later
			if (MaxDepth < ++ctx.Depth)
			{
				--ctx.Depth;
				return null;
			}
			try
			{
				var result = _ExposeOne(e, t, ctx);
				if (null != result) ctx.Variants += result.Count;
				return MaxVariants < ctx.Variants ? null : result;
			}
			finally { --ctx.Depth; }
		}
		static List<Variant>? _ExposeOne(Expression e, string t, Context ctx)
		{
			switch (e)
			{
				case EmptyExpression:
					return new List<Variant> { Variant.Of(e) };
				case SymbolRef sr:
					{
						var q = ctx.G.GetProduction(sr.Name!);
						if (null == q)
							return new List<Variant> { sr.Name == t ? Variant.After(new EmptyExpression()) : Variant.Of(sr) };
						if (!_First(sr, ctx).Contains(t))
							return new List<Variant> { Variant.Of(sr) };
						if (!_Inlinable(q) || ++ctx.Expansions > MaxExpansions)
							return null;
						_Ensure(q, ctx);
						var body = q.Expression.Clone();
						_CarryRuleAttributes(body, q);
						var inlined = new InlinedProduction(q.Name, body, 0, 0, true);
						inlined.CopyLocation(sr);
						return _Expose(inlined, t, ctx);
					}
				case InlinedProduction ip:
					{
						var vs = _Expose(ip.Body, t, ctx);
						if (null == vs) return null;
						if (1 == vs.Count && ReferenceEquals(vs[0].Form, ip.Body))
							return new List<Variant> { Variant.Of(ip) };
						var adopt = _IsCollapsedTerminal(t, ctx) ? 0 : 1;
						if (ip.Back + 1 > MaxHoist) return null;
						var result = new List<Variant>();
						foreach (var v in vs)
						{
							var x = null != v.Tail
								? new InlinedProduction(ip.Production, v.Tail, ip.Back + 1, ip.Adopt + adopt)
								: new InlinedProduction(ip.Production, v.Form!, ip.Back, ip.Adopt);
							x.CopyLocation(ip);
							result.Add(null != v.Tail ? Variant.After(x) : Variant.Of(x));
						}
						return result;
					}
				case Sequence seq:
					return _ExposeItems(seq.Items, t, ctx, seq, seq);
				case Alternation alt:
					{
						if (alt.Attributes.Contains("sync")) return null;
						// alternatives that don't start with t stay as they are, in order, and a run of them stays
						// together. Nothing moves past anything else: where the input is ambiguous the earliest
						// alternative wins, wherever the decision ends up being made
						var result = new List<Variant>();
						var run = new List<Expression>();
						void flush()
						{
							if (0 == run.Count) return;
							if (1 == run.Count)
								result.Add(Variant.Of(run[0]));
							else
							{
								var g = new Alternation(run.ToList());
								g.CopyLocation(alt);
								foreach (var attr in alt.Attributes)
									if ("policy" == attr.Name || "maxk" == attr.Name || "factor" == attr.Name)
										g.Attributes.Add(attr.Clone());
								result.Add(Variant.Of(g));
							}
							run.Clear();
						}
						foreach (var o in alt.Options)
						{
							if (!_First(o, ctx).Contains(t) && !_Nullable(o, ctx))
							{
								run.Add(o);
								continue;
							}
							flush();
							var vs = _Expose(o, t, ctx);
							if (null == vs) return null;
							result.AddRange(vs);
						}
						flush();
						return result;
					}
				case OptionalExpression o:
					{
						// [ a ] is a | (nothing), in the order its policy tries them
						if (o.Attributes.Contains("sync")) return null;
						var body = _First(o.Body, ctx).Contains(t) ? _Expose(o.Body, t, ctx) : new List<Variant> { Variant.Of(o.Body) };
						if (null == body) return null;
						var empty = Variant.Of(new EmptyExpression());
						if ("lazy" == o.Attributes.GetString("policy")) body.Insert(0, empty);
						else body.Add(empty);
						return body;
					}
				case Repeat r:
					{
						// { a } is a { a } | (nothing), and { a }+ is a { a }. A left-recursive loop folds its node as
						// it goes, which only works as a loop
						if (r.LeftFold || r.Attributes.Contains("sync")) return null;
						// a loop whose body can match nothing is an error, reported later; unrolling it never ends
						if (_Nullable(r.Body, ctx)) return null;
						var again = new Repeat(r.Body.Clone(), 0);
						again.CopyLocation(r);
						foreach (var attr in r.Attributes)
							again.Attributes.Add(attr.Clone());
						var once = new Sequence(r.Body, again);
						once.CopyLocation(r);
						var body = _Expose(once, t, ctx);
						if (null == body) return null;
						if (0 == r.Min)
						{
							var empty = Variant.Of(new EmptyExpression());
							if ("lazy" == r.Attributes.GetString("policy")) body.Insert(0, empty);
							else body.Add(empty);
						}
						return body;
					}
				default:
					return null;
			}
		}

		// the ways a sequence of items can go, from the first item's
		// whole: the sequence the items are, if they're all of one, which a way that doesn't start with t can stay
		static List<Variant>? _ExposeItems(IReadOnlyList<Expression> items, string t, Context ctx, GrammarNode at, Expression? whole = null)
		{
			if (0 == items.Count) return new List<Variant> { Variant.Of(new EmptyExpression()) };
			var first = items[0];
			var rest = items.Skip(1).ToList();
			if (!_First(first, ctx).Contains(t) && !_Nullable(first, ctx))
				return new List<Variant> { Variant.Of(whole ?? _Seq(items.ToList(), at)) };
			var vs = _Expose(first, t, ctx);
			if (null == vs) return null;
			var restFirst = _FirstOfItems(rest, ctx);
			var result = new List<Variant>();
			// each way gets its own copy of what follows, so no node is in two places
			List<Expression> copy() => rest.Select(x => x.Clone()).ToList();
			foreach (var v in vs)
			{
				if (null != v.Tail)
				{
					var items2 = new List<Expression> { v.Tail };
					items2.AddRange(copy());
					result.Add(Variant.After(_Seq(items2, at)));
				}
				else if (v.Form is EmptyExpression)
				{
					// the first item matched nothing: the way goes on with the rest
					var more = _ExposeItems(copy(), t, ctx, at);
					if (null == more) return null;
					result.AddRange(more);
				}
				else
				{
					// a way that can match nothing and then go on with t can't be split here
					if (_Nullable(v.Form!, ctx) && restFirst.Contains(t)) return null;
					var items2 = new List<Expression> { v.Form! };
					items2.AddRange(copy());
					result.Add(Variant.Of(_Seq(items2, at)));
				}
			}
			return result;
		}

		// a sequence of the items, flattened, with nothing for none and the item itself for one
		static Expression _Seq(List<Expression> items, GrammarNode at)
		{
			var flat = new List<Expression>();
			foreach (var x in items)
			{
				if (x is EmptyExpression && 0 == x.Attributes.Count) continue;
				if (x is Sequence s && 0 == s.Attributes.Count) flat.AddRange(s.Items);
				else flat.Add(x);
			}
			if (0 == flat.Count) return new EmptyExpression();
			if (1 == flat.Count) return flat[0];
			var result = new Sequence(flat);
			result.CopyLocation(at);
			return result;
		}

		static bool _Inlinable(Production q) =>
			null == q.Operators && null == q.OperatorOwner && null == q.Entering && false != _Flag(q.Attributes);

		// a production's own policy and lookahead apply to the decisions in its body; inlined, they'd be the
		// decisions of the production it's inlined into, so they carry them
		static void _CarryRuleAttributes(Expression body, Production q)
		{
			foreach (var name in new[] { "policy", "maxk", "factor" })
			{
				var attr = q.Attributes.Find(name);
				if (null == attr) continue;
				foreach (var e in body.OwnDescendants())
					if ((e is Alternation || e is OptionalExpression || e is Repeat) && !e.Attributes.Contains(name))
						e.Attributes.Add(attr.Clone());
			}
		}

		static bool _IsCollapsedTerminal(string name, Context ctx) => ctx.G.FindTerminal(name)?.IsCollapsed ?? false;
		#endregion

		#region FIRST
		static bool _Nullable(Expression e, Context ctx) => LeftRecursion.IsNullable(e, ctx.Nullable);

		static void _ComputeFirst(Context ctx)
		{
			foreach (var p in ctx.G.Productions)
				ctx.First[p.Name] = new HashSet<string>();
			for (var changed = true; changed;)
			{
				changed = false;
				foreach (var p in ctx.G.Productions)
					foreach (var x in _First(p.Expression, ctx))
						if (ctx.First[p.Name].Add(x)) changed = true;
			}
		}

		// the terminals an expression can start with
		static HashSet<string> _First(Expression e, Context ctx)
		{
			var result = new HashSet<string>();
			_AddFirst(e, ctx, result);
			return result;
		}
		static HashSet<string> _FirstOfItems(IEnumerable<Expression> items, Context ctx)
		{
			var result = new HashSet<string>();
			foreach (var x in items)
			{
				_AddFirst(x, ctx, result);
				if (!_Nullable(x, ctx)) break;
			}
			return result;
		}
		static void _AddFirst(Expression e, Context ctx, HashSet<string> result)
		{
			switch (e)
			{
				case SymbolRef sr:
					if (ctx.First.TryGetValue(sr.Name!, out var f)) result.UnionWith(f);
					else result.Add(sr.Name!);
					break;
				case Sequence seq:
					foreach (var x in seq.Items)
					{
						_AddFirst(x, ctx, result);
						if (!_Nullable(x, ctx)) break;
					}
					break;
				case Alternation a:
					foreach (var o in a.Options) _AddFirst(o, ctx, result);
					break;
				case OptionalExpression o:
					_AddFirst(o.Body, ctx, result);
					break;
				case Repeat r:
					_AddFirst(r.Body, ctx, result);
					break;
				case InlinedProduction ip:
					_AddFirst(ip.Body, ctx, result);
					break;
			}
		}
		#endregion
	}
}
