using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Luthor;

namespace LexGen
{
    record Rule(string Name, string Pattern, bool IsHidden = false);

    /// <summary>
    /// How <see cref="LexerEmitter.Emit"/> writes a lexer
    /// </summary>
    internal sealed class LexerEmitOptions
    {
        /// <summary>
        /// The namespace to put the lexer in, or null for none. Ignored when <see cref="Nested"/>.
        /// </summary>
        public string? Namespace { get; set; }
        /// <summary>
        /// The lexer class name
        /// </summary>
        public string ClassName { get; set; } = "Lexer";
        /// <summary>
        /// The class's access modifier ("public", "private" and so on), or null to write none
        /// </summary>
        public string? Access { get; set; }
        /// <summary>
        /// Use Unicode character classes
        /// </summary>
        public bool Unicode { get; set; }
        /// <summary>
        /// Write the lexer runtime into the same output, in the lexer's namespace (or none), so lexers in
        /// different namespaces each have their own. Otherwise the lexer calls the one in Luthor.Runtime,
        /// generated once per project or in Parsley.Runtime.dll.
        /// </summary>
        public bool IncludeRuntime { get; set; } = true;
        /// <summary>
        /// Write only the class, indented by <see cref="Indent"/>, for nesting in another type: no
        /// header, namespace or runtime
        /// </summary>
        public bool Nested { get; set; }
        /// <summary>
        /// The indent for a nested class
        /// </summary>
        public string Indent { get; set; } = "";
    }

    /// <summary>
    /// Builds a DFA lexer from rules and writes it as C#. Shared by the parsley-luthor tool and the source generator.
    /// </summary>
    internal static class LexerEmitter
    {
        public const string RuntimeResourceName = "LexGen.Templates.LuthorRuntime.cs";
        public const string RuntimeFileName = "LuthorRuntime.cs";

        const string _KeywordMap = "|abstract|add|as|ascending|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|descending|do|double|dynamic|else|enum|equals|explicit|extern|event|false|finally|fixed|float|for|foreach|get|global|goto|if|in|implicit|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|partial|private|protected|public|readonly|record|ref|remove|return|sbyte|sealed|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while|yield|";

        static string _Escape(string name) => _KeywordMap.Contains("|" + name + "|") ? "@" + name : name;

        /// <summary>
        /// The source of the lexer runtime, which every lexer calls into
        /// </summary>
        public static string RuntimeSource
        {
            get
            {
                using var s = typeof(LexerEmitter).Assembly.GetManifestResourceStream(RuntimeResourceName)
                    ?? throw new InvalidOperationException($"The lexer runtime source {RuntimeFileName} is missing from the assembly");
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }
        }

        /// <summary>
        /// The lexer runtime in the given namespace, or in none, instead of Luthor.Runtime
        /// </summary>
        public static string RuntimeSourceIn(string? @namespace)
        {
            var lines = RuntimeSource.Replace("\r\n", "\n").Split('\n').ToList();
            var at = lines.IndexOf("namespace Luthor.Runtime");
            var close = lines.FindLastIndex(l => l == "}");
            if (at < 0 || close < at || lines[at + 1] != "{")
                throw new InvalidOperationException($"The lexer runtime source {RuntimeFileName} isn't in the expected form");
            if (null != @namespace)
            {
                lines[at] = "namespace " + @namespace;
                return string.Join(Environment.NewLine, lines);
            }
            // the global namespace: drop the block and its indent
            var body = lines.Skip(at + 2).Take(close - at - 2).Select(l => l.StartsWith("    ") ? l.Substring(4) : l);
            return string.Join(Environment.NewLine, lines.Take(at).Concat(body).Concat(lines.Skip(close + 1)));
        }

        /// <summary>
        /// Reads lexer rules, one per line: a name, a space, and a pattern. A name starting with "." is hidden.
        /// </summary>
        public static List<Rule> ReadRules(TextReader reader)
        {
            var list = new List<Rule>();
            string? line;
            while (null != (line = reader.ReadLine()))
            {
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                var hidden = false;
                if (line[0] == '.')
                {
                    hidden = true;
                    line = line.Substring(1);
                }
                var i = line.IndexOf(' ');

                if (i < 0)
                {
                    if (hidden) line = "." + line;
                    throw new Exception($"Invalid rule: {line}");
                }
                var name = line.Substring(0, i);
                var pattern = line.Substring(i + 1).TrimStart();
                list.Add(new Rule(name, pattern, hidden));
            }
            return list;
        }

        static void _DumpArray(int[] array, string indent, TextWriter writer)
        {
            writer.Write(indent);
            for (var i = 0; i < array.Length; i++)
            {
                if (i > 0 && (i % 16 == 0))
                {
                    writer.WriteLine();
                    writer.Write(indent);
                }
                writer.Write(array[i]);
                if (i < array.Length - 1)
                {
                    writer.Write(", ");
                }
            }
        }

        /// <summary>
        /// Builds the DFA for the rules and writes the lexer class. Symbol ids are the rules' indices.
        /// </summary>
        public static void Emit(IList<Rule> rules, TextWriter writer, LexerEmitOptions options)
        {
            var cpDfa = Builder.Build(rules.Select(p => p.Pattern).ToList(), true, options.Unicode);
            var array = Compiler.Compile(cpDfa, "UTF-16");
            var ind = options.Nested ? options.Indent : "";
            var inNamespace = !options.Nested && options.Namespace != null;
            if (!options.Nested)
            {
                writer.WriteLine("// <auto-generated />");
                writer.WriteLine();
                if (options.IncludeRuntime)
                {
                    writer.WriteLine(RuntimeSourceIn(options.Namespace).TrimEnd());
                    writer.WriteLine();
                }
                if (inNamespace)
                {
                    // a block namespace, since the runtime above is in one
                    writer.WriteLine($"namespace {options.Namespace}");
                    writer.WriteLine("{");
                    ind = "    ";
                }
            }
            const string token = "global::Parsley.Runtime.Token";
            var runtime = options.IncludeRuntime && !options.Nested
                ? "global::" + (null != options.Namespace ? options.Namespace + "." : "") + "LuthorRuntime"
                : "global::Luthor.Runtime.LuthorRuntime";
            const string tokens = "global::System.Collections.Generic.IEnumerable<" + token + ">";
            var hiddenRules = rules.Where(p => p.IsHidden).Select(p => _Escape(p.Name)).ToList();
            void tokenize(string inputType, string inputName)
            {
                writer.WriteLine($"{ind}    public static {tokens} Tokenize({inputType} {inputName}, long position = 0, int line = 1, int column = 1, int tabWidth = 4) {{");
                writer.WriteLine($"{ind}        foreach(var tok in {runtime}.Tokenize(_dfa, {inputName}, tabWidth, position, line, column))");
                writer.WriteLine($"{ind}        {{");
                if (hiddenRules.Count > 0)
                {
                    writer.WriteLine($"{ind}            if({string.Join(" || ", hiddenRules.Select(h => $"tok.Symbol == {h}"))}) continue;");
                }
                writer.WriteLine($"{ind}            yield return new {token}(tok.Symbol, tok.Text, tok.Line, tok.Column, tok.Position);");
                writer.WriteLine($"{ind}        }}");
                writer.WriteLine($"{ind}    }}");
            }
            writer.WriteLine($"{ind}{(options.Access != null ? options.Access + " " : "")}static class {options.ClassName}");
            writer.WriteLine($"{ind}{{");
            writer.WriteLine($"{ind}    private static int[] _dfa = {{");
            _DumpArray(array, ind + "        ", writer);
            writer.WriteLine();
            writer.WriteLine($"{ind}    }};");
            tokenize("global::System.IO.TextReader", "reader");
            tokenize("string", "text");
            var i = 0;
            foreach (var name in rules.Select(p => p.Name))
            {
                writer.WriteLine($"{ind}    public const int {_Escape(name)} = {i};");
                i++;
            }
            writer.WriteLine($"{ind}    public const int ERROR = {i};");
            writer.WriteLine($"{ind}}}");
            if (inNamespace)
            {
                writer.WriteLine("}");
            }
        }
    }
}
