using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsley
{
	public sealed class SynthesisOptions
	{
		/// <summary>
		/// The namespace, overriding the grammar's @namespace. Null leaves the class in the global namespace.
		/// </summary>
		public string? Namespace { get; set; }
		/// <summary>
		/// The class name, overriding the grammar's @class and the default (file name + "Parser")
		/// </summary>
		public string? ClassName { get; set; }
		/// <summary>
		/// The evaluator class name, overriding the grammar's @evaluator and the default (file name + "Evaluator")
		/// </summary>
		public string? EvaluatorClassName { get; set; }
		/// <summary>
		/// Whether the class is public (else internal)
		/// </summary>
		public bool Public { get; set; } = true;
	}

	/// <summary>
	/// Turns an analyzed grammar into C# recursive descent that reads as if written by hand:
	/// one method per production, each decision written with the cheapest test that decides it,
	/// and a comment quoting the grammar above each piece. When the start production has a semantic
	/// action, an evaluator class follows the parser, with one method per production that runs its action
	/// over a parse node.
	/// </summary>
	public static class CSharpSynthesizer
	{
		/// <summary>
		/// The file name of the shared runtime source
		/// </summary>
		public const string RuntimeFileName = "ParsleyRuntime.cs";
		/// <summary>
		/// The shared runtime support (Token, LookAheadEnumerator, ParseNode, ParseError, ParserTables and the
		/// abstract ParserBase) that every synthesized parser needs alongside it, whatever its engine
		/// </summary>
		public static string RuntimeSource => _Resource(RuntimeFileName);
		/// <summary>
		/// The file name of an engine's base class source, such as LL1ParserBase.cs
		/// </summary>
		public static string BaseFileName(ParserEngine engine) => engine.BaseClassName() + ".cs";
		/// <summary>
		/// The source of an engine's base class (LL1ParserBase, LLkParserBase or AllStarParserBase), which
		/// a parser synthesized for that engine derives from. It needs <see cref="RuntimeSource"/>.
		/// </summary>
		public static string BaseSource(ParserEngine engine) => _Resource(BaseFileName(engine));
		static string _Resource(string name)
		{
			using var s = typeof(CSharpSynthesizer).Assembly.GetManifestResourceStream("Parsley.Runtime." + name)
				?? throw new InvalidOperationException($"The runtime source {name} is missing from the assembly");
			using var r = new StreamReader(s);
			return r.ReadToEnd();
		}

		/// <summary>
		/// Analyzes and synthesizes in one step. Throws if the grammar has errors.
		/// </summary>
		public static string Synthesize(Grammar grammar, SynthesisOptions? options = null)
		{
			var a = GrammarAnalysis.Analyze(grammar);
			GrammarException.ThrowIfErrors(a.Messages);
			return Synthesize(a, options);
		}

		public static string Synthesize(GrammarAnalysis analysis, SynthesisOptions? options = null)
		{
			if (analysis.HasErrors)
				throw new GrammarException(analysis.Messages);
			return new _Emitter(analysis, options ?? new SynthesisOptions()).Emit();
		}

		sealed class _Emitter
		{
			readonly GrammarAnalysis _a;
			readonly SymbolTable _s;
			readonly SynthesisOptions _o;
			readonly string _className;
			readonly string? _evaluatorName; // null when there's no evaluator to synthesize
			readonly string[] _const;
			StringBuilder _sb = new();
			int _indent;
			readonly Dictionary<string, string> _sets = new(); // key: sorted ids
			readonly List<(string Name, int[] Ids, string Comment)> _setList = new();
			readonly List<Decision> _predictors = new();
			readonly ParserEngine _engine;
			readonly bool _allStar; // productions carry the ATN state they return to, for adaptive prediction
			bool _nameConsumes; // inside a multi-token decision, where no case label shows the token

			public _Emitter(GrammarAnalysis a, SynthesisOptions o)
			{
				_a = a;
				_s = a.Symbols;
				_o = o;
				_engine = a.Engine;
				_allStar = ParserEngine.AllStar == _engine;
				_className = _Ident(o.ClassName ?? a.Grammar.ClassName ?? (a.Grammar.StartSymbol + "Parser"));
				// only an action on the start production makes an evaluator; validation has already
				// rejected actions elsewhere without one there
				if (null != a.Grammar.GetProduction(a.Grammar.StartSymbol!)?.Action)
				{
					_evaluatorName = _VerbatimIdent(o.EvaluatorClassName ?? a.Grammar.EvaluatorClassName ?? (a.Grammar.StartSymbol + "Evaluator"));
					if (_evaluatorName == _className)
						throw new GrammarException($"The evaluator and the parser are both named \"{_className}\". Use @evaluator or @class to name them apart");
				}
				_const = _MakeConstantNames();
			}

			#region Naming
			static readonly HashSet<string> _keywords = new HashSet<string>(
				("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double " +
				"else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is " +
				"lock long namespace new null object operator out override params private protected public readonly ref return sbyte " +
				"sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe " +
				"ushort using virtual void volatile while").Split(' '));
			static string _Ident(string name)
			{
				var sb = new StringBuilder();
				var upper = true;
				foreach (var ch in name)
				{
					if (!char.IsLetterOrDigit(ch)) { upper = true; continue; }
					if (0 == sb.Length && char.IsDigit(ch)) sb.Append('_');
					sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
					upper = false;
				}
				var s = 0 == sb.Length ? "Symbol" : sb.ToString();
				return _keywords.Contains(s) ? "@" + s : s;
			}
			// a name used as written when it's a valid identifier, with @ in front of a keyword; otherwise made into one
			static string _VerbatimIdent(string name)
			{
				var valid = 0 < name.Length && (char.IsLetter(name[0]) || '_' == name[0]) && name.All(ch => char.IsLetterOrDigit(ch) || '_' == ch);
				if (!valid) return _Ident(name);
				return _keywords.Contains(name) ? "@" + name : name;
			}
			string[] _MakeConstantNames()
			{
				var reserved = new HashSet<string>
				{
					_className, "Parse", "Current", "Errors", "Peek", "Advance", "Consume", "Expect", "Error", "NewNode", "Fold",
					"Enter", "Leave", "ExpectEnd", "Run", "Tables", "SymbolCount", "GetHashCode", "ToString", "Equals", "GetType",
					"Start", "ReadToken", "FromLexer", "EndToken", "AdaptivePredict", "Atn",
					"Classify", "Classified", "OnMatched", "CurrentUnclassified", "Previous", "Wrap"
				};
				// the methods hooks call are members too
				foreach (var (name, _) in _Hooks()) reserved.Add(name);
				foreach (var p in _a.Grammar.Productions) reserved.Add("Parse" + _Ident(p.Name));
				// the evaluator uses the same constant names, so they mustn't clash with its members either
				if (null != _evaluatorName)
				{
					reserved.UnionWith(new[] { _evaluatorName, "Evaluate", "State" });
					foreach (var p in _a.Grammar.Productions) reserved.Add(_EvaluateName(p.Name));
				}
				var used = new HashSet<string>(reserved);
				var result = new string[_s.Count];
				for (var i = 0; i < _s.Count; ++i)
				{
					string baseName = i == _s.EndOfInput ? "EndOfInput" : i == _s.Error ? "ErrorSymbol" : _Ident(_s.GetName(i));
					var name = baseName;
					var n = 2;
					while (!used.Add(name)) name = baseName + (n++).ToString();
					result[i] = name;
				}
				return result;
			}
			string _MethodName(string production) => "Parse" + _Ident(production);

			// every hook method the grammar names, with the declaration it gets: the parse-time interface the
			// other half of the partial class implements
			List<(string Name, string Declaration)> _Hooks()
			{
				var result = new List<(string, string)>();
				void add(string name, string decl)
				{
					if (!result.Contains((name, decl))) result.Add((name, decl));
				}
				foreach (var t in _a.Grammar.Terminals)
				{
					if (null != t.When) add(t.When, $"private partial bool {t.When}(Token token, Token previous);");
					if (null != t.Matched) add(t.Matched, $"partial void {t.Matched}(Token token);");
				}
				foreach (var p in _a.Grammar.Productions)
				{
					if (null != p.Entering) add(p.Entering, $"partial void {p.Entering}();");
					if (null != p.Parsed) add(p.Parsed, $"partial void {p.Parsed}(ParseNode node);");
				}
				return result;
			}
			static string _EvaluateName(string production) => "Evaluate" + _Ident(production);
			string _PredictName(Decision d) => "Predict" + _Ident(d.Production.Name) + d.Index.ToString();
			#endregion

			#region Writing
			void _Line(string text = "")
			{
				if (0 < text.Length) _sb.Append('\t', _indent);
				_sb.AppendLine(text);
			}
			void _Open(string text)
			{
				_Line(text);
				_Line("{");
				++_indent;
			}
			void _Close(string text = "}")
			{
				--_indent;
				_Line(text);
			}
			string _Capture(Action write)
			{
				var saved = _sb;
				_sb = new StringBuilder();
				write();
				var result = _sb.ToString();
				_sb = saved;
				return result;
			}
			static string _Comment(string text) => text.Replace("\r", "").Replace("\n", " ");
			#endregion

			#region Tests on the current token
			string _SetName(ICollection<int> ids)
			{
				var sorted = ids.OrderBy(x => x).ToArray();
				var key = string.Join(",", sorted);
				if (_sets.TryGetValue(key, out var name)) return name;
				// name it after a production when it is exactly what that production starts with
				string? baseName = null;
				foreach (var p in _a.Grammar.Productions)
				{
					var first = _a.GetFirst(p.Expression, 1);
					if (first.Any(x => 0 == x.Count)) continue;
					if (first.Select(x => x[0]).Distinct().OrderBy(x => x).SequenceEqual(sorted))
					{
						baseName = "s_starts" + _Ident(p.Name);
						break;
					}
				}
				baseName ??= "s_set";
				name = baseName;
				var n = 2;
				while (_sets.ContainsValue(name) || "s_set" == name) name = baseName + (n++).ToString();
				_sets.Add(key, name);
				_setList.Add((name, sorted, string.Join(", ", sorted.Select(_s.Display))));
				return name;
			}
			string _Test(ICollection<int> ids, bool negate, string token = "Current")
			{
				var sorted = ids.OrderBy(x => x).ToList();
				if (1 == sorted.Count)
					return $"{token}.SymbolId {(negate ? "!=" : "==")} {_const[sorted[0]]}";
				if (4 >= sorted.Count)
				{
					var or = string.Join(" or ", sorted.Select(i => _const[i]));
					return negate ? $"{token}.SymbolId is not ({or})" : $"{token}.SymbolId is {or}";
				}
				var set = _SetName(sorted);
				return $"{(negate ? "!" : "")}{set}[{token}.SymbolId]";
			}
			#endregion

			public string Emit()
			{
				var g = _a.Grammar;
				var source = null != g.Filename ? Path.GetFileName(g.Filename) : "an in-memory grammar";
				_Line("// <auto-generated>");
				_Line($"// Synthesized by Parsley from {source}. Changes are lost when it is synthesized again;");
				_Line("// add members in another part of this partial class instead.");
				_Line("// </auto-generated>");
				_Line("#nullable enable");
				var usings = new List<string>();
				if (null != _evaluatorName) usings.Add("System");
				usings.Add("System.Collections.Generic");
				usings.Add("Parsley.Runtime");
				// @using is for semantic actions, so it only applies when there's an evaluator; validation warns
				// otherwise. One the file already has is skipped.
				if (null != _evaluatorName)
					foreach (var u in g.Usings)
						if (!usings.Contains(u.Namespace))
							usings.Add(u.Namespace);
				foreach (var u in usings)
					_Line($"using {u};");
				_Line();
				var ns = _o.Namespace ?? g.Namespace;
				if (null != ns)
				{
					_Line($"namespace {ns};");
					_Line();
				}
				_Line("/// <summary>");
				_Line($"/// Parses {g.StartSymbol} with {_EngineDescription()}. Synthesized from {source}.");
				_Line("/// </summary>");
				_Open($"{(_o.Public ? "public" : "internal")} partial class {_className} : {_engine.BaseClassName()}");

				_Line("#region Symbols");
				for (var i = 0; i < _s.Count; ++i)
				{
					var comment = _s.IsNonTerminal(i) ? "" : null != _s.GetTerminal(i)?.Literal ? $" // {_s.Display(i)}" : "";
					_Line($"public const int {_const[i]} = {i};{comment}");
				}
				_Line($"public const int SymbolCount = {_s.Count};");
				_Line("#endregion");
				_Line();
				_Line($"public {_className}(IEnumerable<Token> tokens) : base(tokens, s_tables{(_allStar ? ", s_atn" : "")}) {{ }}");
				_Line();
				_Line("/// <summary>");
				_Line($"/// Parses a complete {g.StartSymbol} and returns its tree. Problems are reported in <see cref=\"ParserBase.Errors\"/>.");
				_Line("/// </summary>");
				_Open("public static bool TryParse(IEnumerable<Token> tokens,out ParseNode result, out IReadOnlyList<ParseError> errors)");
				_Line($"var parser = new {_className}(tokens);");
				// an operator rule's method takes the level it starts at, so it's called rather than passed
				var startIsOperators = null != g.GetProduction(g.StartSymbol!)!.Operators;
				_Line(_allStar
					? $"result = parser.Run(() => parser.{_MethodName(g.StartSymbol!)}(-1));"
					: startIsOperators ? $"result = parser.Run(() => parser.{_MethodName(g.StartSymbol!)}());"
					: $"result = parser.Run(parser.{_MethodName(g.StartSymbol!)});");
                _Line($"errors = parser.Errors;");
				_Line($"return parser.Errors.Count == 0;");
                _Close();

                _Open("public static ParseNode Parse(IEnumerable<Token> tokens)");
				_Open($"if(!TryParse(tokens, out var result, out var errors))");
				_Line("throw new ParseException(errors);");
				_Close();
				_Line("return result;");
                _Close();

                foreach (var p in g.Productions)
				{
					// an operator form's own production is parsed by its operator rule
					if (null != p.OperatorOwner) continue;
					_Line();
					if (null != p.Operators) _OperatorProduction(p);
					else _Production(p);
				}
				foreach (var d in _predictors.ToList())
				{
					_Line();
					_Predictor(d);
				}
				_ParseHooks();
				_Line();
				_Tables();
				_Close();
				if (null != _evaluatorName)
				{
					_Line();
					_Evaluator(source);
				}
				return _sb.ToString();
			}

			string _EngineDescription()
			{
				switch (_engine)
				{
					case ParserEngine.LL1: return "one token of lookahead (LL(1))";
					case ParserEngine.LLk: return 1 < _a.Depth ? $"up to {_a.Depth} tokens of lookahead (LL({_a.Depth}))" : "one token of lookahead, on the LL(k) base";
					default:
						var n = _a.AdaptiveDecisions.Count;
						return $"adaptive prediction (ALL(*)) for {n} decision{(1 == n ? "" : "s")}";
				}
			}

			#region Evaluator
			// the evaluator walks a finished parse tree: one EvaluateXxx per production that has a node,
			// running its semantic action, or passing through to its one non-terminal child when it has none
			void _Evaluator(string source)
			{
				var g = _a.Grammar;
				var start = g.GetProduction(g.StartSymbol!)!;
				var evaluated = g.Productions.Where(p => !p.IsCollapsed).ToList();
				var startType = start.Type ?? "object";
				// a typed state has to be passed: null isn't a valid default for every type
				var stateType = start.StateType ?? "object?";
				var stateParam = null == start.StateType ? "object? state = null" : $"{stateType} state";
				_Line("/// <summary>");
				_Line($"/// Evaluates {g.StartSymbol} parse trees from <see cref=\"{_className}\"/> with the semantic actions in {source}.");
				_Line("/// </summary>");
				_Open($"{(_o.Public ? "public" : "internal")} partial class {_evaluatorName}");
				_Line("#region Symbols");
				for (var i = 0; i < _s.Count; ++i)
				{
					var comment = _s.IsNonTerminal(i) ? "" : null != _s.GetTerminal(i)?.Literal ? $" // {_s.Display(i)}" : "";
					_Line($"protected const int {_const[i]} = {i};{comment}");
				}
				_Line("#endregion");
				_Line();
				_Line("/// <summary>");
				_Line("/// The state passed to Evaluate, for the semantic actions to use");
				_Line("/// </summary>");
				_Line($"protected {stateType} State {{ get; }}");
				_Line();
				_Line($"protected {_evaluatorName}({stateType} state) {{ State = state; }}");
				_Line();
				_Line("/// <summary>");
				_Line($"/// Evaluates a parse tree from <see cref=\"{_className}\"/>, starting at its {g.StartSymbol} root");
				_Line("/// </summary>");
				_Line("/// <param name=\"node\">The root of the tree</param>");
				_Line("/// <param name=\"state\">Anything the semantic actions need, available to them as State</param>");
				_Open($"public static {startType} Evaluate(ParseNode node, {stateParam})");
				_Line($"return new {_evaluatorName}(state).{_EvaluateName(start.Name)}(node);");
				_Close();

				_Line();
				_Line("/// <summary>");
				_Line("/// Evaluates a node of any non-terminal, for when a child can be one of several");
				_Line("/// </summary>");
				_Open("protected object Evaluate(ParseNode node)");
				_Open("switch (node.SymbolId)");
				foreach (var p in evaluated)
				{
					_Line($"case {_const[_s.GetId(p.Name)]}:");
					++_indent;
					// a nullable type can return null, which object can't say
					_Line($"return {_EvaluateName(p.Name)}(node){(null != p.Type && p.Type.TrimEnd().EndsWith("?") ? "!" : "")};");
					--_indent;
				}
				_Close();
				_Line("throw new InvalidOperationException($\"{node.Symbol} can't be evaluated\");");
				_Close();

				if (evaluated.Any(p => null == p.Action))
				{
					_Line();
					_Line("// for a production with no semantic action: its node evaluates to its one non-terminal child, terminals aside");
					_Open("object _PassThrough(ParseNode node)");
					_Line("ParseNode? only = null;");
					_Open("foreach (var child in node.Children)");
					_Line("if (child.IsTerminal) continue;");
					_Line("if (null != only) throw new InvalidOperationException($\"{node.Symbol} has no semantic action and more than one non-terminal child, so it can't be evaluated\");");
					_Line("only = child;");
					_Close();
					_Line("if (null == only) throw new InvalidOperationException($\"{node.Symbol} has no semantic action and no non-terminal child, so it can't be evaluated\");");
					_Line("return Evaluate(only);");
					_Close();
				}

				foreach (var p in evaluated)
				{
					var type = p.Type ?? "object";
					_Line();
					var written = p.Operators?.Text ?? (null != p.OperatorOwner ? p.OperatorOwner.Forms.First(f => f.Production == p).Text + ";" : null) ?? XbnfWriter.Write(p, false);
					_Line($"// {_Comment(written)}");
					_Open($"protected {type} {_EvaluateName(p.Name)}(ParseNode node)");
					if (null != p.Operators)
					{
						// nodes of an operator form with an action of its own go to it; the rest are evaluated here
						var own = p.Operators.Forms.Where(f => null != f.Production?.Action).Select(f => f.Production!).Distinct().ToList();
						if (0 < own.Count)
						{
							_Open("switch (node.SymbolId)");
							foreach (var q in own)
							{
								_Line($"case {_const[_s.GetId(q.Name)]}:");
								++_indent;
								_Line($"return {_Cast(q.Type, p.Type)}{_EvaluateName(q.Name)}(node);");
								--_indent;
							}
							_Close();
						}
					}
					if (null != p.OperatorOwner && null == p.Action)
					{
						// no action of its own: the operator rule's action evaluates it
						var rule = p.OperatorOwner.Production;
						_Line($"return {_Cast(rule.Type, p.Type)}{_EvaluateName(rule.Name)}(node);");
					}
					else if (null == p.Action)
						_Line(null == p.Type ? "return _PassThrough(node);" : $"return ({type})_PassThrough(node);");
					else
						_ActionBody(p.Action);
					_Close();
				}
				_Close();
			}

			// a cast from one evaluate method's type to another's, when they're written differently. Parsley doesn't
			// know C#'s conversions, so it casts and lets the compiler say whether that's allowed
			static string _Cast(string? from, string? to)
			{
				static string norm(string? t) => string.Concat((t ?? "object").Where(c => !char.IsWhiteSpace(c)));
				return norm(from) == norm(to) ? "" : $"({to ?? "object"})";
			}

			// the action's code, re-indented to sit in its method. code with a string literal that spans
			// lines goes in exactly as written, because re-indenting it would change the string
			void _ActionBody(SemanticAction action)
			{
				if (action.HasMultilineLiteral)
				{
					// a blank line right after the opening brace or right before the closing one can't be
					// inside a string, so those can go
					var code = action.Code;
					var nl = code.IndexOf('\n');
					if (-1 < nl && 0 == code.Substring(0, nl).Trim().Length) code = code.Substring(nl + 1);
					nl = code.LastIndexOf('\n');
					if (-1 < nl && 0 == code.Substring(nl + 1).Trim().Length) code = code.Substring(0, nl + 1);
					_sb.Append(code);
					if (!code.EndsWith("\n")) _sb.AppendLine();
					return;
				}
				var lines = action.Code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
				// anything on the same line as the opening brace has no indentation to go by
				var first = lines[0].Trim();
				lines.RemoveAt(0);
				while (0 < lines.Count && 0 == lines[lines.Count - 1].Trim().Length)
					lines.RemoveAt(lines.Count - 1);
				if (0 < first.Length) _Line(first);
				string? common = null;
				foreach (var line in lines)
				{
					if (0 == line.Trim().Length) continue;
					var lead = line.Substring(0, line.Length - line.TrimStart().Length);
					if (null == common) { common = lead; continue; }
					var n = 0;
					while (n < common.Length && n < lead.Length && common[n] == lead[n]) ++n;
					common = common.Substring(0, n);
				}
				foreach (var line in lines)
					_Line(0 == line.Trim().Length ? "" : line.Substring(common!.Length).TrimEnd());
			}
			#endregion

			void _Production(Production p)
			{
				var id = _s.GetId(p.Name);
				var rewritten = p.Expression.Descendants().OfType<Repeat>().Any(r => r.LeftFold);
				_Line($"// {_Comment(XbnfWriter.Write(p, false))}" + (rewritten ? " (left recursion rewritten as a loop)" : ""));
				var enter = _allStar ? $"Enter({_const[id]}, returnState);" : $"Enter({_const[id]});";
				var retParam = _allStar ? "int returnState" : "";
				if (p.IsCollapsed)
				{
					_Open($"void {_MethodName(p.Name)}(ParseNode parent{(_allStar ? ", " : "")}{retParam})");
					_Line(enter);
					if (null != p.Entering) _Line($"{p.Entering}();");
					_Expr(p.Expression, "parent", null, p, true);
					if (null != p.Parsed) _Line($"{p.Parsed}(parent);");
					_Line("Leave();");
					_Close();
				}
				else
				{
					_Open($"ParseNode {_MethodName(p.Name)}({retParam})");
					_Line(enter);
					_Line($"var node = NewNode({_const[id]});");
					if (null != p.Entering) _Line($"{p.Entering}();");
					_Expr(p.Expression, "node", null, p, true);
					if (null != p.Parsed) _Line($"{p.Parsed}(node);");
					_Line("Leave();");
					_Line("return node;");
					_Close();
				}
			}

			// an operator rule, parsed by precedence climbing: an operand (an atom, or a prefix form and its operand),
			// then a loop that applies infix and postfix forms for as long as they bind at least as tightly as
			// minLevel. A form's operand is parsed by a call that starts at the form's level, so it takes the
			// operators that bind tighter and leaves the rest to this loop.
			void _OperatorProduction(Production p)
			{
				var rule = p.Operators!;
				var id = _s.GetId(p.Name);
				_Line($"// {_Comment(rule.Text)}");
				_Line("// an operator rule, parsed by precedence climbing. minLevel is the loosest operator this call may apply:");
				_Line("// a looser one is left for the call that's waiting for this operand");
				var enter = _allStar ? $"Enter({_const[id]}, returnState);" : $"Enter({_const[id]});";
				_Open($"ParseNode {_MethodName(p.Name)}({(_allStar ? "int returnState, " : "")}int minLevel = 0)");
				_Line(enter);
				_Line($"var node = NewNode({_const[id]});");
				if (null != p.Entering) _Line($"{p.Entering}();");
				_Expr(rule.Primary, "node", null, p, true);
				if (null != rule.Loop)
				{
					_Expr(rule.Loop, "node", null, p, false);
					--_indent;
					_Line("done:");
					++_indent;
				}
				if (null != p.Parsed) _Line($"{p.Parsed}(node);");
				_Line("Leave();");
				_Line("return node;");
				_Close();
			}

			static string _Describe(OperatorForm f) => f.Kind switch
			{
				OperatorKind.Atom => "an atom",
				OperatorKind.Prefix => $"prefix, level {f.Level}",
				OperatorKind.Postfix => $"postfix, level {f.Level}",
				_ => $"infix, level {f.Level}, {(f.IsRightAssociative ? "right" : "left")} associative"
			};

			// one operator form, chosen by the decision it's an alternative of
			void _OperatorForm(OperatorForm f, string node, IReadOnlyList<int>? known, Production p)
			{
				_Line($"// {_Comment(f.Text)} ({_Describe(f)})");
				var symbol = _const[_s.GetId(f.Symbol)];
				switch (f.Kind)
				{
					case OperatorKind.Atom:
						_Expr(f.Option, node, known, p, false, true);
						return;
					case OperatorKind.Prefix:
						// node is new, made for the operand; a form with a production of its own makes its own instead
						if (null != f.Production) _Line($"{node} = NewNode({symbol});");
						break;
					default:
						_Line($"if ({f.Level} < minLevel) goto done;");
						_Line($"{node} = Wrap({symbol}, {node});");
						break;
				}
				foreach (var item in f.Operator)
				{
					_Expr(item, node, known, p, false);
					if (null != known && 0 < known.Count && item is SymbolRef sr && _s.GetId(sr.Name!) == known[0])
						known = known.Skip(1).ToList();
					else
						known = null;
				}
				if (null == f.Operand) return;
				// left associative: the operand stops at this level's own operators, so they apply to this node next
				var level = OperatorKind.Infix == f.Kind && !f.IsRightAssociative ? f.Level + 1 : f.Level;
				var ret = _allStar ? _a.Atn.GetReturnState(f.Operand).ToString() + ", " : "";
				_Line($"{node}.Add({_MethodName(p.Name)}({ret}{level}));");
			}

			// emits the code for an expression. known: the token already established as Current, if any
			void _Expr(Expression e, string node, IReadOnlyList<int>? known, Production p, bool isRoot, bool asWritten = false)
			{
				if (!asWritten && _a.GetOperatorForm(e) is OperatorForm form)
				{
					_OperatorForm(form, node, known, p);
					return;
				}
				switch (e)
				{
					case EmptyExpression:
						break;
					case SymbolRef sr:
						{
							var id = _s.GetId(sr.Name!);
							if (_s.IsTerminal(id))
							{
								if (null != known && 0 < known.Count && known[0] == id)
									_Line(_nameConsumes ? $"Consume({node}); // {_Comment(_s.Display(id))}" : $"Consume({node});");
								else
									_Line($"Expect({_const[id]}, {node});");
							}
							else
							{
								// an ALL(*) parser tells each production where its caller continues
								var ret = _allStar ? _a.Atn.GetReturnState(sr).ToString() : "";
								if (_s.GetProduction(id)!.IsCollapsed)
									_Line($"{_MethodName(sr.Name!)}({node}{(_allStar ? ", " : "")}{ret});");
								else
									_Line($"{node}.Add({_MethodName(sr.Name!)}({ret}));");
							}
							break;
						}
					case Sequence seq:
						// tokens the decision already verified carry on to the terminals that start the sequence
						for (var i = 0; i < seq.Items.Count; ++i)
						{
							_Expr(seq.Items[i], node, known, p, false);
							if (null != known && 0 < known.Count && seq.Items[i] is SymbolRef sr && _s.GetId(sr.Name!) == known[0])
								known = known.Skip(1).ToList();
							else
								known = null;
						}
						break;
					default:
						_Decision(_a.GetDecision(e)!, node, p, isRoot);
						break;
				}
			}

			static IReadOnlyList<int>? _Single(IReadOnlyList<int> tokens) => 1 == tokens.Count ? tokens : null;

			void _Decision(Decision d, string node, Production p, bool isRoot)
			{
				// a decision that is the whole body of a loop or optional is already described by its parent's comment
				var parent = _a.GetParent(d.Node);
				var operatorLoop = _a.IsOperatorLoop(d.Node);
				if (operatorLoop)
					_Line("// apply infix and postfix operators while the next token starts one");
				else if (!isRoot && !(parent is Repeat || parent is OptionalExpression) && !(null != _a.GetOperatorForm(d.Node)))
					_Line($"// {_Comment(d.Node.ToString())}");
				if (null != d.AppliedPolicy && !operatorLoop)
					_Line($"// ambiguous within {_a.MaxK} tokens; resolved {(d.Kind == DecisionKind.Alternation ? "by taking the first alternative" : "greedy" == d.AppliedPolicy ? "greedily" : "lazily")}");
				if (d.IsAdaptive)
				{
					if (0 == d.Depth)
						_Line($"// no fixed lookahead decides this; ALL(*) looks as far as it has to");
					// a prediction has matched at least the first token, so a branch that can only start one way has it
					_DeepDecision(d, node, p, isRoot, $"AdaptivePredict({d.AdaptiveIndex})", i => _Single(d.TokensOf(i)));
					return;
				}
				if (1 < d.Depth)
				{
					_predictors.Add(d);
					_DeepDecision(d, node, p, isRoot, _PredictName(d) + "()", i => _CommonPrefix(d.Branches[i]));
					return;
				}
				switch (d.Node)
				{
					case Alternation a:
						_Alternation(d, a, node, p, isRoot);
						break;
					case OptionalExpression o:
						if (1 == d.DefaultBranch)
						{
							var take = d.TokensOf(0);
							_Open($"if ({_Test(take.ToList(), false)})");
							_Expr(o.Body, node, _Single(take), p, false);
							_Close();
						}
						else
						{
							_Open($"if ({_Test(_ExitTokens(d), true)})");
							_Expr(o.Body, node, null, p, false);
							_Close();
						}
						break;
					case Repeat r:
						{
							var useContinue = 1 == d.DefaultBranch;
							var cont = d.TokensOf(0);
							var test = useContinue ? _Test(cont.ToList(), false) : _Test(_ExitTokens(d), true);
							var fold = r.LeftFold && !p.IsCollapsed;
							if (1 == r.Min)
							{
								_Open("do");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, null, p, false);
								_Close($"}} while ({test});");
							}
							else
							{
								_Open($"while ({test})");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, useContinue ? _Single(cont) : null, p, false);
								_Close();
							}
							break;
						}
				}
			}

			List<int> _ExitTokens(Decision d)
			{
				var result = d.TokensOf(1).ToList();
				if (!result.Contains(_s.EndOfInput)) result.Add(_s.EndOfInput);
				return result;
			}

			string _ErrorCall(Decision d, string node, Production p, bool isRoot)
			{
				var tokens = new SortedSet<int>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						tokens.Add(x[0]);
				string? expected = null;
				if (isRoot) expected = p.Attributes.GetString("expected");
				expected ??= d.Node.Attributes.GetString("expected");
				var desc = null == expected ? "null" : GrammarAttribute.FormatValue(expected);
				// a rule that can't match any finite input has no tokens to name
				return 0 == tokens.Count ? $"Error({node}, {desc});" : $"Error({node}, {desc}, {string.Join(", ", tokens.Select(t => _const[t]))});";
			}

			void _Alternation(Decision d, Alternation a, string node, Production p, bool isRoot)
			{
				var n = a.Options.Count;
				if (2 == n && 0 <= d.DefaultBranch)
				{
					// if/else: test the other branch, the default is the else
					var other = 1 - d.DefaultBranch;
					var tokens = d.TokensOf(other);
					if (0 == tokens.Count)
					{
						_Expr(a.Options[d.DefaultBranch], node, null, p, false);
						return;
					}
					_Open($"if ({_Test(tokens.ToList(), false)})");
					_Expr(a.Options[other], node, _Single(tokens), p, false);
					_Close();
					var elseBody = a.Options[d.DefaultBranch];
					if (!(elseBody is EmptyExpression))
					{
						_Open("else");
						_Expr(elseBody, node, null, p, false);
						_Close();
					}
					return;
				}
				_Open("switch (Current.SymbolId)");
				// alternatives whose code comes out the same share one set of case labels
				var cases = new List<(List<int> Tokens, string Body)>();
				for (var i = 0; i < n; ++i)
				{
					if (i == d.DefaultBranch) continue;
					var tokens = d.TokensOf(i);
					if (0 == tokens.Count) continue;
					++_indent;
					var body = _Capture(() =>
					{
						_Expr(a.Options[i], node, _Single(tokens), p, false);
						_Line("break;");
					});
					--_indent;
					var same = cases.FindIndex(c => c.Body == body);
					if (-1 < same) cases[same].Tokens.AddRange(tokens);
					else cases.Add((tokens.ToList(), body));
				}
				foreach (var (tokens, body) in cases)
				{
					foreach (var t in tokens)
						_Line($"case {_const[t]}:");
					_sb.Append(body);
				}
				_Line("default:");
				++_indent;
				if (0 <= d.DefaultBranch)
					_Expr(a.Options[d.DefaultBranch], node, null, p, false);
				else
					_Line(_ErrorCall(d, node, p, isRoot));
				_Line("break;");
				--_indent;
				_Close();
			}

			// a decision made by a method that returns the branch: a synthesized predictor or adaptive prediction.
			// known: the tokens a branch is certain to start with once chosen
			void _DeepDecision(Decision d, string node, Production p, bool isRoot, string call, Func<int, IReadOnlyList<int>?> known)
			{
				switch (d.Node)
				{
					case Alternation a:
						_Open($"switch ({call})");
						for (var i = 0; i < a.Options.Count; ++i)
						{
							if (i == d.DefaultBranch || 0 == d.Branches[i].Count) continue;
							_Line($"case {i}:");
							++_indent;
							_nameConsumes = true;
							_Expr(a.Options[i], node, known(i), p, false);
							_nameConsumes = false;
							_Line("break;");
							--_indent;
						}
						_Line("default:");
						++_indent;
						if (0 <= d.DefaultBranch)
							_Expr(a.Options[d.DefaultBranch], node, null, p, false);
						else
							_Line(_ErrorCall(d, node, p, isRoot));
						_Line("break;");
						--_indent;
						_Close();
						break;
					case OptionalExpression o:
						_Open($"if (0 == {call})");
						_nameConsumes = true;
						_Expr(o.Body, node, known(0), p, false);
						_nameConsumes = false;
						_Close();
						break;
					case Repeat r:
						{
							var fold = r.LeftFold && !p.IsCollapsed;
							if (1 == r.Min)
							{
								_Open("do");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, null, p, false);
								_Close($"}} while (0 == {call});");
							}
							else
							{
								_Open($"while (0 == {call})");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, null, p, false);
								_Close();
							}
							break;
						}
				}
			}

			// the tokens every lookahead string of a branch starts with: the predictor has checked them
			static IReadOnlyList<int>? _CommonPrefix(IReadOnlyCollection<LookaheadString> strings)
			{
				if (0 == strings.Count) return null;
				var first = strings.First();
				var n = first.Count;
				foreach (var x in strings)
				{
					var i = 0;
					while (i < n && i < x.Count && x[i] == first[i]) ++i;
					n = i;
				}
				return 0 == n ? null : first.Take(n).ToList();
			}

			// the overrides that connect contextual terminals and matched hooks, and the partial methods they call
			void _ParseHooks()
			{
				var hooks = _Hooks();
				if (0 == hooks.Count) return;
				var contextual = _a.Grammar.Terminals.Where(t => t.IsContextual).ToList();
				if (0 < contextual.Count)
				{
					_Line();
					_Line("// contextual terminals: what a token is depends on what has been parsed, so it's decided as the parser looks at it");
					_Open("protected override int Classify(Token token, Token previous)");
					_Open("switch (token.SymbolId)");
					foreach (var g in contextual.GroupBy(t => t.ContextualBase!))
					{
						_Line($"case {_const[_s.GetId(g.Key)]}:");
						++_indent;
						foreach (var t in g)
							_Line($"if ({t.When}(token, previous)) return {_const[_s.GetId(t.Name)]};");
						_Line("break;");
						--_indent;
					}
					_Close();
					_Line("return token.SymbolId;");
					_Close();
				}
				var matched = _a.Grammar.Terminals.Where(t => null != t.Matched).ToList();
				if (0 < matched.Count)
				{
					_Line();
					_Open("protected override void OnMatched(Token token)");
					_Open("switch (token.SymbolId)");
					foreach (var t in matched)
					{
						_Line($"case {_const[_s.GetId(t.Name)]}:");
						++_indent;
						_Line($"{t.Matched}(token);");
						_Line("break;");
						--_indent;
					}
					_Close();
					_Close();
				}
				_Line();
				_Line("// implemented in another part of this partial class: the parse-time hooks the grammar names");
				foreach (var (_, decl) in hooks) _Line(decl);
			}

			void _Predictor(Decision d)
			{
				var what = d.Kind switch
				{
					DecisionKind.Alternation => "which alternative",
					DecisionKind.Optional => "0 to take it, 1 to skip it",
					_ => "0 to go around again, 1 to stop"
				};
				_Line($"// {_Comment(d.Production.Name)}: {_Comment(d.Node.ToString())}");
				_Line($"// looks {d.Depth} tokens ahead and returns {what}, or -1 if nothing matches");
				_Open($"int {_PredictName(d)}()");
				var strings = new List<(LookaheadString S, int Branch)>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						strings.Add((x, i));
				_Trie(strings, 0);
				_Line("return -1;");
				_Close();
			}

			void _Trie(List<(LookaheadString S, int Branch)> strings, int level)
			{
				var token = 0 == level ? "Current" : $"Peek({level})";
				_Open($"switch ({token}.SymbolId)");
				var groups = strings.Where(x => x.S.Count > level).GroupBy(x => x.S[level]).OrderBy(gr => gr.Key).ToList();
				// leaves that return the same branch share one set of case labels
				var leaves = groups.Where(gr => 1 == gr.Select(x => x.Branch).Distinct().Count()).GroupBy(gr => gr.First().Branch).OrderBy(x => x.Key);
				foreach (var leaf in leaves)
				{
					foreach (var gr in leaf)
						_Line($"case {_const[gr.Key]}:");
					++_indent;
					_Line($"return {leaf.Key};");
					--_indent;
				}
				foreach (var gr in groups.Where(gr => 1 < gr.Select(x => x.Branch).Distinct().Count()))
				{
					_Line($"case {_const[gr.Key]}:");
					++_indent;
					_Trie(gr.ToList(), level + 1);
					_Line("break;");
					--_indent;
				}
				_Close();
			}

			void _Tables()
			{
				_Line("#region Tables");
				foreach (var (name, ids, comment) in _setList)
				{
					_Line($"// {_Comment(comment)}");
					_Line($"static readonly bool[] {name} = ParserTables.Set(SymbolCount, {string.Join(", ", ids.Select(i => _const[i]))});");
				}
				_Line("static readonly ParserTables s_tables = new(");
				++_indent;
				_Line($"terminalStart: {_const[_s.NonTerminalCount]}, // lexer ids count terminals from 0; the parser adds this");
				_Line($"symbolNames: [{string.Join(", ", _s.Names.Select(GrammarAttribute.FormatValue))}],");
				_Line($"displayNames: [{string.Join(", ", Enumerable.Range(0, _s.Count).Select(i => GrammarAttribute.FormatValue(_s.Display(i))))}],");
				var collapsed = Enumerable.Range(0, _s.Count).Where(_s.IsCollapsedTerminal).Select(i => _const[i]);
				_Line($"collapsedTerminals: [{string.Join(", ", collapsed)}],");
				_Line("// for each non-terminal, where error recovery can stop while parsing it: what can follow it, plus its sync terminals");
				_Line("recoverySets:");
				_Line("[");
				++_indent;
				var nts = _s.NonTerminalCount;
				for (var i = 0; i < nts; ++i)
				{
					var set = _RecoverySet(_s.GetProduction(i)!);
					_Line($"[{string.Join(", ", set.Select(x => _const[x]))}]{(i < nts - 1 ? "," : "")} // {_s.GetName(i)}");
				}
				--_indent;
				_Line("],");
				var sync = Enumerable.Range(0, _s.Count).Where(i => _s.GetTerminal(i)?.IsSync ?? false).Select(i => _const[i]);
				_Line($"syncTerminals: [{string.Join(", ", sync)}],");
				_Line("endOfInput: EndOfInput,");
				var contextualIds = Enumerable.Range(0, _s.Count).Where(i => _s.GetTerminal(i)?.IsContextual ?? false).Select(i => _const[i]).ToList();
				if (0 < contextualIds.Count)
				{
					_Line("error: ErrorSymbol,");
					_Line($"contextualTerminals: [{string.Join(", ", contextualIds)}]);");
				}
				else
					_Line("error: ErrorSymbol);");
				--_indent;
				if (_allStar) _Atn();
				_Line("#endregion");
			}

			void _Atn()
			{
				var atn = _a.Atn;
				_Line("// the grammar as a transition network, for adaptive prediction. rules: each non-terminal's start and");
				_Line("// stop state. states: each state's transition count, then for each transition a kind (0: no input,");
				_Line("// to a; 1: match terminal a, to b; 2: call non-terminal a, returning to b) and its a and b.");
				_Line("// decisions: the state where each adaptive decision chooses its branch.");
				_Line("static readonly Atn s_atn = new(");
				++_indent;
				_Line($"startRule: {_const[atn.StartRule]},");
				_Line("endOfInput: EndOfInput,");
				_Numbers("rules", atn.Rules, ",");
				_Numbers("states", atn.SerializeStates(), ",");
				_Numbers("decisions", _a.AdaptiveDecisions.Select(d => atn.GetDecisionState(d.Node)).ToList(), ");");
				--_indent;
			}

			// a long list of numbers, wrapped
			void _Numbers(string name, IReadOnlyList<int> values, string end)
			{
				if (24 >= values.Count)
				{
					_Line($"{name}: [{string.Join(", ", values)}]{end}");
					return;
				}
				_Line($"{name}:");
				_Line("[");
				++_indent;
				for (var i = 0; i < values.Count; i += 24)
				{
					var last = i + 24 >= values.Count;
					_Line(string.Join(", ", values.Skip(i).Take(24)) + (last ? "" : ","));
				}
				--_indent;
				_Line("]" + end);
			}

			SortedSet<int> _RecoverySet(Production p)
			{
				var result = new SortedSet<int>(_a.GetFollowTokens(p.Name));
				void addSync(object? value)
				{
					if (!(value is string s)) return;
					foreach (var part in s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
					{
						var id = _s.GetId(part);
						if (-1 == id)
						{
							var t = _a.Grammar.Terminals.FirstOrDefault(x => x.Literal == part);
							if (null != t) id = _s.GetId(t.Name);
						}
						if (-1 != id && _s.IsTerminal(id)) result.Add(id);
					}
				}
				addSync(p.Attributes.Get("sync"));
				foreach (var e in p.Expression.Descendants())
					addSync(e.Attributes.Get("sync"));
				return result;
			}
		}
	}
}
