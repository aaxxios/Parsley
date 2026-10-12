using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// A human readable account of a grammar: which engine it needs and why, what would make it lighter,
	/// how its decisions break down, and what the synthesized parser will cost
	/// </summary>
	public static class GrammarReport
	{
		/// <summary>
		/// The report as text
		/// </summary>
		public static string Create(GrammarAnalysis analysis)
		{
			var sw = new StringWriter();
			Write(analysis, sw);
			return sw.ToString();
		}

		public static void Write(GrammarAnalysis analysis, TextWriter writer) => new _Writer(analysis, writer).Write();

		sealed class _Writer
		{
			readonly GrammarAnalysis _a;
			readonly TextWriter _w;
			readonly SymbolTable _s;
			readonly Grammar _g;
			const int ExpressionWidth = 56;

			public _Writer(GrammarAnalysis a, TextWriter w)
			{
				_a = a;
				_w = w;
				_s = a.Symbols;
				_g = a.Grammar;
			}

			public void Write()
			{
				_Overview();
				if (_a.HasErrors || null == _s)
				{
					_w.WriteLine();
					var n = _a.Messages.Count(m => m.Level == ErrorLevel.Error);
					_w.WriteLine($"The grammar has {_Plural(n, "error")}, so it can't be analyzed further. They're listed above.");
					return;
				}
				_w.WriteLine();
				_Engine();
				_w.WriteLine();
				_OtherEngines();
				_w.WriteLine();
				_Symbols();
				_w.WriteLine();
				_Decisions();
				_w.WriteLine();
				_Productions();
				_w.WriteLine();
				_GeneratedCode();
				_Messages();
			}

			#region Formatting
			static string _Plural(int n, string what, string? plural = null) => $"{n} {(1 == n ? what : plural ?? what + "s")}";
			static string _Clip(string s, int width)
			{
				s = s.Replace("\r", "").Replace("\n", " ");
				return s.Length <= width ? s : s.Substring(0, width - 3) + "...";
			}
			static string _Tokens(int n) => 0 == n ? "adaptive" : 1 == n ? "1 token" : $"{n} tokens";
			void _Heading(string text)
			{
				_w.WriteLine(text);
				_w.WriteLine(new string('-', text.Length));
			}
			void _Field(string name, string value) => _w.WriteLine($"  {name.PadRight(18)}{value}");
			// columns padded to their widest cell, the last column left ragged
			void _Table(string[] header, IEnumerable<string[]> rows)
			{
				var all = new List<string[]> { header };
				all.AddRange(rows);
				var widths = new int[header.Length];
				foreach (var r in all)
					for (var i = 0; i < r.Length; ++i)
						widths[i] = Math.Max(widths[i], r[i].Length);
				for (var j = 0; j < all.Count; ++j)
				{
					var r = all[j];
					var sb = new StringBuilder("  ");
					for (var i = 0; i < r.Length; ++i)
					{
						if (i == r.Length - 1) sb.Append(r[i]);
						else sb.Append(r[i].PadRight(widths[i] + 2));
					}
					_w.WriteLine(sb.ToString().TrimEnd());
					if (0 == j)
						_w.WriteLine("  " + string.Join("  ", widths.Select(x => new string('-', x))));
				}
			}
			string _TokenList(IEnumerable<int> ids, int max = 6)
			{
				var list = ids.Distinct().OrderBy(x => x).Select(_s.Display).ToList();
				if (0 == list.Count) return "(nothing)";
				return list.Count <= max ? string.Join(" ", list) : string.Join(" ", list.Take(max)) + $" +{list.Count - max} more";
			}
			string _Conflicts(Decision d, int max = 3)
			{
				var c = d.Ambiguities;
				var n = Math.Max(d.AmbiguityCount, c.Count);
				return string.Join(", ", c.Take(max).Select(x => $"[{x.ToString(_s)}]")) + (max < n ? $" +{n - max} more" : "");
			}
			static string _Kind(Decision d) => d.Kind switch
			{
				DecisionKind.Alternation => "alternation",
				DecisionKind.Optional => "optional",
				_ => "loop"
			};
			// a policy written in the grammar, on the decision or its production, rather than a default applied by LL(1)/LL(k)
			static bool _ExplicitPolicy(Decision d) => null != d.Node.Attributes.GetString("policy") || null != d.Production.Attributes.GetString("policy");
			#endregion

			void _Overview()
			{
				_Heading("Grammar " + (null != _g.Filename ? Path.GetFileName(_g.Filename) : "(in memory)"));
				_Field("Start symbol", _g.StartSymbol ?? "(none)");
				_Field("Parser class", (_g.ClassName ?? "(from start symbol)") + (null != _g.Namespace ? $" in {_g.Namespace}" : ""));
				var actions = _g.Productions.Count(p => null != p.Action);
				_Field("Evaluator", 0 == actions ? "none (no semantic actions)" : $"{_g.EvaluatorClassName}, semantic actions on {_Plural(actions, "production")}");
				if (0 < _g.Usings.Count)
					_Field("Usings", string.Join(", ", _g.Usings.Select(u => u.Namespace).Distinct()) + (0 == actions ? " (ignored: no semantic actions)" : ""));
			}

			void _Engine()
			{
				var how = ParserEngine.Auto == _a.RequestedEngine ? "chosen automatically" : "forced";
				_Heading($"Engine: {_a.Engine.DisplayName()} ({how})");
				var decisions = _a.Decisions;
				var deep = decisions.Where(d => !d.IsAdaptive && 1 < d.Depth).ToList();
				var unbounded = decisions.Where(d => d.IsAdaptive && 0 == d.Depth).ToList();
				_Field("Base class", _a.Engine.BaseClassName());
				switch (_a.Engine)
				{
					case ParserEngine.LL1:
						_Field("Why", 0 == decisions.Count ? "the grammar has no decisions" : "every decision is made by the current token");
						break;
					case ParserEngine.LLk:
						if (1 == _a.Depth)
							_Field("Why", "forced; every decision is made by the current token, so LL(1) would do");
						else
						{
							var deepest = deep.OrderByDescending(d => d.Depth).First();
							_Field("Why", $"{_Plural(deep.Count, "decision needs", "decisions need")} more than one token; the deepest needs {deepest.Depth} ({deepest.Production.Name} #{deepest.Index})");
						}
						break;
					case ParserEngine.AllStar:
						if (0 < unbounded.Count)
							_Field("Why", $"{_Plural(unbounded.Count, "decision isn't", "decisions aren't")} settled by any fixed lookahead (up to {_a.MaxK} tokens)");
						else
							_Field("Why", "forced; " + (0 == _a.AdaptiveDecisions.Count ? "every decision is made by the current token" : $"decisions needing up to {_a.Depth} tokens are predicted adaptively"));
						_Field("Adaptive", $"{_Plural(_a.AdaptiveDecisions.Count, "decision")} predicted at parse time; the rest are plain tests");
						break;
				}
				_Field("Fixed lookahead", $"up to {_Tokens(Math.Max(1, _a.Depth))}");
				var limit = ParserEngine.LL1 == _a.RequestedEngine ? "1 (LL(1) forced)" : null != _g.MaxK ? $"{_a.MaxK} (@maxk)" : $"{_a.MaxK} (the default; @maxk changes it)";
				_Field("Lookahead limit", limit);
				// what stands between this grammar and a lighter engine
				if (ParserEngine.AllStar == _a.Engine && 0 < unbounded.Count)
				{
					_w.WriteLine();
					_w.WriteLine("  Needs ALL(*) because of:");
					foreach (var d in unbounded)
						_w.WriteLine($"    {d.Production.Name} #{d.Index} {_Kind(d)} {_Clip(d.Node.ToString(), ExpressionWidth)}\n      overlaps within {_a.MaxK} tokens on {_Conflicts(d)}");
					_w.WriteLine("  If those inputs are really ambiguous (a dangling else, say), a <policy> on the decision");
					_w.WriteLine("  settles it at synthesis time. Otherwise only restructuring the rules so they no longer");
					_w.WriteLine("  overlap (often by left factoring) will drop ALL(*).");
				}
				else if (ParserEngine.LLk == _a.Engine && 0 < deep.Count)
				{
					_w.WriteLine();
					_w.WriteLine("  Needs LL(k) because of:");
					foreach (var d in deep.OrderByDescending(d => d.Depth))
						_w.WriteLine($"    {d.Production.Name} #{d.Index} {_Kind(d)} needs {_Tokens(d.Depth)}: {_Clip(d.Node.ToString(), ExpressionWidth)}");
					_w.WriteLine("  Left factoring these (moving the shared start out of the alternatives) would make them LL(1).");
				}
			}

			void _OtherEngines()
			{
				_Heading("Engines compared");
				var rows = new List<string[]>();
				foreach (var e in new[] { ParserEngine.LL1, ParserEngine.LLk, ParserEngine.AllStar })
				{
					var x = e == _a.Engine && _a.RequestedEngine == e ? _a : GrammarAnalysis.Analyze(_g, e, ParserEngine.LL1 == _a.RequestedEngine ? null : _a.MaxK);
					string verdict, detail;
					var errors = x.Messages.Count(m => m.Level == ErrorLevel.Error);
					// an alternation with no policy is an error, counted above, even though it was given one to carry on
					// an operator rule's loop is settled by precedence at parse time, not by a policy
					var defaulted = x.Decisions.Count(d => null != d.AppliedPolicy && !_ExplicitPolicy(d) && d.Kind != DecisionKind.Alternation && !x.IsOperatorLoop(d.Node));
					var policies = x.Decisions.Count(d => null != d.AppliedPolicy && _ExplicitPolicy(d));
					if (0 < errors)
					{
						verdict = "no";
						detail = $"{_Plural(errors, "alternation")} can't be decided";
						if (0 < defaulted) detail += $"; {_Plural(defaulted, "loop or optional", "loops or optionals")} would be settled greedily";
					}
					else if (0 < defaulted)
					{
						verdict = "partly";
						detail = $"{_Plural(defaulted, "loop or optional", "loops or optionals")} settled greedily by default, which may reject valid input";
					}
					else
					{
						verdict = "yes";
						detail = e switch
						{
							ParserEngine.LL1 => "every decision on one token",
							ParserEngine.LLk => $"k = {Math.Max(1, x.Depth)}",
							_ => 0 == x.AdaptiveDecisions.Count ? "every decision on one token; nothing adaptive" : $"{_Plural(x.AdaptiveDecisions.Count, "adaptive decision")}"
						};
					}
					if (0 < policies) detail += $" ({_Plural(policies, "decision")} settled by your <policy>)";
					var mark = e == _a.Engine ? "*" : "";
					rows.Add(new[] { e.DisplayName() + mark, verdict, detail });
				}
				_Table(new[] { "Engine", "Parses it", "Detail" }, rows);
				_w.WriteLine("  * the engine in use");
			}

			void _Symbols()
			{
				_Heading("Symbols");
				var prods = _g.Productions;
				var recursive = _Recursive();
				var reachable = _Reachable();
				var flags = new List<string>();
				void add(int n, string what) { if (0 < n) flags.Add($"{n} {what}"); }
				add(prods.Count(p => p.IsCollapsed), "collapsed");
				add(prods.Count(p => null == p.OperatorOwner && _a.IsNullable(p.Expression)), "nullable");
				add(recursive.Count, "recursive");
				add(prods.Count(p => p.Expression.Descendants().OfType<Repeat>().Any(r => r.LeftFold)), "left recursion rewritten");
				var rules = prods.Count(p => null != p.Operators);
				add(rules, 1 == rules ? "operator rule" : "operator rules");
				var forms = prods.Count(p => null != p.OperatorOwner);
				add(forms, 1 == forms ? "operator form" : "operator forms");
				add(prods.Count(p => !reachable.Contains(p.Name)), "unreachable");
				_Field("Productions", prods.Count.ToString() + (0 < flags.Count ? $" ({string.Join(", ", flags)})" : ""));
				var terms = _g.Terminals;
				flags.Clear();
				add(terms.Count(t => null != t.Literal), "literal");
				add(terms.Count(t => null == t.Literal && !t.IsContextual), "pattern");
				add(terms.Count(t => t.IsContextual), "contextual");
				add(terms.Count(t => t.IsHidden), "hidden");
				add(terms.Count(t => t.IsCollapsed), "collapsed");
				add(terms.Count(t => t.IsImplicit), "implicit");
				add(terms.Count(t => t.IsSync), "sync");
				_Field("Terminals", terms.Count.ToString() + (0 < flags.Count ? $" ({string.Join(", ", flags)})" : ""));
				_Field("Symbol ids", $"{_s.Count} (with #EOS and #ERROR)");
				var contextual = terms.Where(t => t.IsContextual).ToList();
				if (0 < contextual.Count)
					_Field("Contextual", string.Join(", ", contextual.Select(t => $"{t.Name}, read from {t.ContextualBase} when {t.When} says so")));
				var hooks = prods.Count(p => null != p.Entering) + prods.Count(p => null != p.Parsed) + terms.Count(t => null != t.Matched);
				if (0 < hooks)
					_Field("Parse-time hooks", $"{_Plural(hooks, "call")} into your code: {prods.Count(p => null != p.Entering)} entering, {prods.Count(p => null != p.Parsed)} parsed, {terms.Count(t => null != t.Matched)} matched");
				var inlined = _a.Messages.Count(m => m.Message.StartsWith("Inlined "));
				if (0 < inlined) _Field("Inlined", $"{_Plural(inlined, "rule")} to remove indirect left recursion");
			}

			void _Decisions()
			{
				var ds = _a.Decisions;
				_Heading("Decisions");
				if (0 == ds.Count)
				{
					_w.WriteLine("  None: the grammar has no alternatives, optionals or loops.");
					return;
				}
				_Field("Total", $"{ds.Count}: {_Plural(ds.Count(d => d.Kind == DecisionKind.Alternation), "alternation")}, {_Plural(ds.Count(d => d.Kind == DecisionKind.Optional), "optional")}, {_Plural(ds.Count(d => d.Kind == DecisionKind.Loop), "loop")}");
				var byDepth = ds.Where(d => 0 < d.Depth).GroupBy(d => d.Depth).OrderBy(g => g.Key).Select(g => $"{_Tokens(g.Key)}: {g.Count()}").ToList();
				var unbounded = ds.Count(d => 0 == d.Depth);
				if (0 < unbounded) byDepth.Add($"unbounded: {unbounded}");
				_Field("By lookahead", string.Join(", ", byDepth));
				if (0 < _a.AdaptiveDecisions.Count) _Field("Adaptive", $"{_a.AdaptiveDecisions.Count} (predicted by ALL(*) at parse time)");
				var policy = ds.Where(d => null != d.AppliedPolicy && !_a.IsOperatorLoop(d.Node)).ToList();
				if (0 < policy.Count)
					_Field("Settled by policy", $"{policy.Count} ({policy.Count(_ExplicitPolicy)} written in the grammar, {policy.Count(d => !_ExplicitPolicy(d))} by default)");
				var strings = ds.Where(d => 1 < d.Depth && !d.IsAdaptive).Sum(d => d.Branches.Sum(b => b.Count));
				if (0 < strings) _Field("Predictor strings", $"{strings} lookahead strings across {_Plural(ds.Count(d => 1 < d.Depth && !d.IsAdaptive), "synthesized predictor")}");

				// only the ones that cost something: plain one-token tests aren't worth listing
				var notable = ds.Where(d => 1 != d.Depth || d.IsAdaptive || (null != d.AppliedPolicy && !_a.IsOperatorLoop(d.Node))).ToList();
				if (0 == notable.Count)
				{
					_w.WriteLine("  Every decision is a one-token switch or if.");
					return;
				}
				_w.WriteLine();
				_w.WriteLine("  Decisions needing more than a one-token test:");
				var rows = notable.Select(d =>
				{
					var how = d.IsAdaptive ? (0 == d.Depth ? "ALL(*), unbounded" : $"ALL(*), LL({d.Depth}) would do") : $"predictor, {_Tokens(d.Depth)}";
					if (null != d.AppliedPolicy && !d.IsAdaptive) how = (1 < d.Depth ? $"predictor, {_Tokens(d.Depth)}" : "one-token test") + $", {d.AppliedPolicy} policy";
					var note = 0 < d.Ambiguities.Count ? $"overlaps on {_Conflicts(d, 2)}" : "";
					return new[] { $"{d.Production.Name} #{d.Index}", _Kind(d), how, _Clip(d.Node.ToString(), 40) + (0 < note.Length ? "  " + note : "") };
				});
				_Table(new[] { "Decision", "Kind", "Made by", "Expression" }, rows);
			}

			void _Productions()
			{
				_Heading("Productions");
				var recursive = _Recursive();
				var reachable = _Reachable();
				var rows = new List<string[]>();
				foreach (var p in _g.Productions)
				{
					var flags = new List<string>();
					if (p.Name == _g.StartSymbol) flags.Add("start");
					if (p.IsCollapsed) flags.Add("collapsed");
					if (null != p.OperatorOwner)
					{
						// parsed by its operator rule, which makes its nodes
						flags.Add($"operator form of {p.OperatorOwner.Production.Name}");
						if (null != p.Action) flags.Add("action");
						rows.Add(new[] { p.Name, string.Join(", ", flags), "-", "-", "-" });
						continue;
					}
					if (null != p.Operators) flags.Add($"operators, {p.Operators.Forms.Where(f => OperatorKind.Atom != f.Kind).Select(f => f.Level).Distinct().Count()} levels");
					if (_a.IsNullable(p.Expression)) flags.Add("nullable");
					if (recursive.Contains(p.Name)) flags.Add("recursive");
					if (p.Expression.Descendants().OfType<Repeat>().Any(r => r.LeftFold)) flags.Add("left-rec rewritten");
					if (null != p.Action) flags.Add("action");
					if (!reachable.Contains(p.Name)) flags.Add("unreachable");
					var ds = _a.Decisions.Where(d => d.Production == p).ToList();
					var lookahead = 0 == ds.Count ? "-" : ds.Any(d => d.IsAdaptive) ? "ALL(*)" : _Tokens(ds.Max(d => d.Depth));
					rows.Add(new[] { p.Name, string.Join(", ", flags), ds.Count.ToString(), lookahead, _TokenList(_a.GetFirstTokens(p.Expression)) });
				}
				_Table(new[] { "Name", "Flags", "Decisions", "Lookahead", "Starts with" }, rows);
			}

			void _GeneratedCode()
			{
				_Heading("Synthesized code");
				string src;
				try { src = CSharpSynthesizer.Synthesize(_a); }
				catch (GrammarException ex)
				{
					_w.WriteLine("  Can't synthesize: " + ex.Message);
					return;
				}
				var lines = src.Split('\n').Length - 1;
				_Field("Parser source", $"{lines} lines, {Math.Round(Encoding.UTF8.GetByteCount(src) / 1024.0, 1)} KB" + (_g.HasActions ? " (evaluator included)" : ""));
				var predictors = _a.Decisions.Count(d => 1 < d.Depth && !d.IsAdaptive);
				_Field("Methods", $"{_Plural(_g.Productions.Count(p => null == p.OperatorOwner), "production method")}, {_Plural(predictors, "predictor method")}, {_Plural(_a.AdaptiveDecisions.Count, "adaptive call site")}");
				var sets = src.Split('\n').Count(l => l.TrimStart().StartsWith("static readonly bool[] s_"));
				_Field("Tables", $"{_s.Count} symbols, {_Plural(sets, "token set")}, recovery sets for {_Plural(_s.NonTerminalCount, "non-terminal")}");
				if (ParserEngine.AllStar == _a.Engine)
				{
					var atn = _a.Atn;
					var transitions = atn.SerializeStates().Length - atn.StateCount;
					_Field("Network", $"{atn.StateCount} states, {transitions / 3} transitions");
				}
				_Field("Runtime files", $"{CSharpSynthesizer.RuntimeFileName}, {CSharpSynthesizer.BaseFileName(_a.Engine)}");
				_Field("At parse time", _a.Engine switch
				{
					ParserEngine.LL1 => "reads tokens straight from the lexer; nothing buffered",
					ParserEngine.LLk => $"buffers up to {_Plural(Math.Max(1, _a.Depth), "token")} of lookahead",
					_ => "buffers as far ahead as predictions look; caches a DFA per adaptive decision, shared across parses"
				});
			}

			void _Messages()
			{
				var warnings = _a.Messages.Count(m => m.Level == ErrorLevel.Warning);
				var notes = _a.Messages.Count(m => m.Level == ErrorLevel.Message);
				if (0 == warnings + notes) return;
				_w.WriteLine();
				_w.WriteLine($"{_Plural(warnings, "warning")} and {_Plural(notes, "message")}, listed above the report.");
			}

			#region Graph
			Dictionary<string, HashSet<string>>? _calls;
			Dictionary<string, HashSet<string>> _Calls()
			{
				if (null != _calls) return _calls;
				_calls = new Dictionary<string, HashSet<string>>();
				foreach (var p in _g.Productions)
					_calls[p.Name] = new HashSet<string>(p.Expression.Descendants().OfType<SymbolRef>()
						.Where(r => null != r.Name && _s.IsNonTerminal(_s.GetId(r.Name))).Select(r => r.Name!));
				return _calls;
			}
			HashSet<string> _From(string start)
			{
				var seen = new HashSet<string>();
				var stack = new Stack<string>(_Calls()[start]);
				while (0 < stack.Count)
				{
					var n = stack.Pop();
					if (seen.Add(n))
						foreach (var m in _Calls()[n]) stack.Push(m);
				}
				return seen;
			}
			// productions that can reach themselves, directly or through others
			HashSet<string> _Recursive() => new HashSet<string>(_g.Productions.Select(p => p.Name).Where(n => _From(n).Contains(n)));
			HashSet<string> _Reachable()
			{
				var result = null != _g.StartSymbol ? _From(_g.StartSymbol) : new HashSet<string>();
				if (null != _g.StartSymbol) result.Add(_g.StartSymbol);
				foreach (var p in _g.Productions)
					if (null != p.OperatorOwner && result.Contains(p.OperatorOwner.Production.Name)) result.Add(p.Name);
				return result;
			}
			#endregion
		}
	}
}
