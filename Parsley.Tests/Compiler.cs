using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Parsley.Tests
{
	/// <summary>
	/// Compiles a synthesized parser together with the runtime file, in memory and on its own
	/// (no reference to Parsley.dll), then runs it through reflection.
	/// </summary>
	sealed class CompiledParser
	{
		readonly Type _parserType;
		readonly Type _tokenType;
		public IReadOnlyList<Diagnostic> Diagnostics { get; }
		/// <summary>
		/// The compiled assembly, for calling into other classes compiled with the parser
		/// </summary>
		public Assembly Assembly => _parserType.Assembly;

		CompiledParser(Type parserType, Type tokenType, IReadOnlyList<Diagnostic> diagnostics)
		{
			_parserType = parserType;
			_tokenType = tokenType;
			Diagnostics = diagnostics;
		}

		static List<MetadataReference>? _refs;
		static List<MetadataReference> _References()
		{
			if (null != _refs) return _refs;
			var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
			_refs = tpa.Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) == "netstandard.dll" || Path.GetFileName(p) == "mscorlib.dll")
				.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
			return _refs;
		}

		/// <param name="extra">More sources compiled with it, such as the other half of the partial class</param>
		public static CompiledParser Compile(string source, string className, params string[] extra)
		{
			var opts = new CSharpParseOptions(LanguageVersion.CSharp12);
			var trees = new List<SyntaxTree>
			{
				CSharpSyntaxTree.ParseText(source, opts, "Parser.cs"),
				CSharpSyntaxTree.ParseText(CSharpSynthesizer.RuntimeSource, opts, CSharpSynthesizer.RuntimeFileName),
				// every engine's base, as a project holding parsers for several engines would have
				CSharpSyntaxTree.ParseText(CSharpSynthesizer.BaseSource(ParserEngine.LL1), opts, CSharpSynthesizer.BaseFileName(ParserEngine.LL1)),
				CSharpSyntaxTree.ParseText(CSharpSynthesizer.BaseSource(ParserEngine.LLk), opts, CSharpSynthesizer.BaseFileName(ParserEngine.LLk)),
				CSharpSyntaxTree.ParseText(CSharpSynthesizer.BaseSource(ParserEngine.AllStar), opts, CSharpSynthesizer.BaseFileName(ParserEngine.AllStar))
			};
			for (var i = 0; i < extra.Length; ++i)
				trees.Add(CSharpSyntaxTree.ParseText(extra[i], opts, $"Extra{i}.cs"));
			var comp = CSharpCompilation.Create("Synth" + Guid.NewGuid().ToString("N"), trees, _References(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, warningLevel: 4));
			using var ms = new MemoryStream();
			var result = comp.Emit(ms);
			var diags = result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
			if (!result.Success)
				throw new InvalidOperationException("Synthesized code did not compile:\n" + string.Join("\n", diags));
			ms.Position = 0;
			var asm = new AssemblyLoadContext(null, true).LoadFromStream(ms);
			var parserType = asm.GetTypes().First(t => t.Name == className);
			var tokenType = asm.GetType("Parsley.Runtime.Token")!;
			return new CompiledParser(parserType, tokenType, diags);
		}

		/// <summary>
		/// Parses and returns the tree dump and the error messages
		/// </summary>
		public (string Tree, List<string> Errors) Parse(IEnumerable<Parsley.Runtime.Token> tokens)
		{
			var (root, errors) = ParseRaw(tokens);
			return (root?.ToString() ?? "", errors);
		}

		/// <summary>
		/// Parses and returns the root, as the compiled assembly's own ParseNode, and the error messages
		/// </summary>
		public (object? Root, List<string> Errors) ParseRaw(IEnumerable<Parsley.Runtime.Token> tokens)
		{
			var list = tokens.ToList();
			var arr = Array.CreateInstance(_tokenType, list.Count);
			var parseNodeType = _parserType.GetMethod("Parse",BindingFlags.Static | BindingFlags.Public)!.ReturnType;
            var parseErrorType = parseNodeType.Assembly.GetType("Parsley.Runtime.ParseError", throwOnError: true)!;
            var errorListType = typeof(IReadOnlyList<>).MakeGenericType(parseErrorType);
            var tokenEnumType = typeof(IEnumerable<>).MakeGenericType(_tokenType);

            for (var i = 0; i < list.Count; ++i)
			{
				var t = list[i];
				arr.SetValue(Activator.CreateInstance(_tokenType, t.SymbolId, t.Value, t.Line, t.Column, t.Position), i);
			}
            var tryParse = _parserType.GetMethod(
			"TryParse",
			BindingFlags.Public | BindingFlags.Static,
			binder: null,
			types: new[]
			{
				tokenEnumType,
				parseNodeType.MakeByRefType(),
				errorListType.MakeByRefType()
			},
			modifiers: null)
			?? throw new MissingMethodException(_parserType.FullName, "TryParse");

            var args = new object?[] { arr, null, null };
            tryParse.Invoke(null, args);

            var result = args[1];
            var errors = (System.Collections.IEnumerable?)args[2];

            return (
                result,
                errors?.Cast<object>().Select(e => e.ToString()!).ToList() ?? new List<string>());
        }

		/// <summary>
		/// A compiled assembly's ParseNode as text, every node with its location, so trees that differ only
		/// in where their nodes are said to be come out different
		/// </summary>
		public static string Dump(object? node)
		{
			var sb = new System.Text.StringBuilder();
			void write(object n, int depth)
			{
				var t = n.GetType();
				string get(string name) => t.GetProperty(name)!.GetValue(n)?.ToString() ?? "";
				sb.Append(' ', depth * 2).Append(get("Symbol")).Append('@').Append(get("Line")).Append(':').Append(get("Column")).Append(':').Append(get("Position"));
				var v = t.GetProperty("Value")!.GetValue(n);
				if (null != v) sb.Append(' ').Append(v);
				sb.Append('\n');
				foreach (var c in (System.Collections.IEnumerable)t.GetProperty("Children")!.GetValue(n)!)
					write(c, depth + 1);
			}
			if (null != node) write(node, 0);
			return sb.ToString();
		}

		/// <summary>
		/// The synthesized evaluator class with this name
		/// </summary>
		public Type GetEvaluator(string className) => _parserType.Assembly.GetTypes().First(t => t.Name == className);

		/// <summary>
		/// Parses, then evaluates the tree with the synthesized evaluator. Throws if the parse fails.
		/// </summary>
		public object? Evaluate(string evaluatorClassName, IEnumerable<Parsley.Runtime.Token> tokens, object? state = null)
		{
			var list = tokens.ToList();
			var arr = Array.CreateInstance(_tokenType, list.Count);
			for (var i = 0; i < list.Count; ++i)
			{
				var t = list[i];
				arr.SetValue(Activator.CreateInstance(_tokenType, t.SymbolId, t.Value, t.Line, t.Column, t.Position), i);
			}
			var parse = _parserType.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public)!;
			object? tree;
			try { tree = parse.Invoke(null, new object[] { arr }); }
			catch (TargetInvocationException ex) { throw ex.InnerException!; }
			var evaluate = GetEvaluator(evaluatorClassName).GetMethod("Evaluate", BindingFlags.Static | BindingFlags.Public)!;
			try { return evaluate.Invoke(null, new[] { tree, state }); }
			catch (TargetInvocationException ex) { throw ex.InnerException!; }
		}
	}

	static class Interp
	{
		public static (string Tree, List<string> Errors) Parse(GrammarAnalysis a, IEnumerable<Parsley.Runtime.Token> tokens)
		{
			var p = new GrammarInterpreter(a, tokens);
			var root = p.Parse();
			return (root.ToString(), p.Errors.Select(e => e.ToString()).ToList());
		}
	}
}
