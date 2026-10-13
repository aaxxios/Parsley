using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// A grammar's text, with its scan and line starts, for positions in both directions
    /// </summary>
    sealed class Document
    {
        int[]? _lineStarts;
        XbnfScan? _scan;

        public Document(string path, string text, int version)
        {
            Path = path;
            Text = text;
            Version = version;
        }
        public string Path { get; }
        public string Text { get; }
        public int Version { get; }
        public XbnfScan Scan => _scan ??= XbnfScan.Scan(Text);
        /// <summary>
        /// The grammar as the reader reads it, for what only it knows, such as which rules are terminals.
        /// Read once per version, when first asked for; null if it couldn't be read.
        /// </summary>
        public Parsley.Grammar? ReadGrammar(Func<string, string> readFile)
        {
            if (!_grammarRead)
            {
                _grammarRead = true;
                try { XbnfReader.TryParse(Text, Path, readFile, out _grammar); }
                catch (Exception) { _grammar = null; }
            }
            return _grammar;
        }
        Parsley.Grammar? _grammar;
        bool _grammarRead;

        int[] LineStarts
        {
            get
            {
                if (null != _lineStarts) return _lineStarts;
                var starts = new List<int> { 0 };
                for (var i = 0; i < Text.Length; ++i)
                    if (Text[i] == '\n') starts.Add(i + 1);
                return _lineStarts = starts.ToArray();
            }
        }

        /// <summary>
        /// An LSP position (zero-based line, UTF-16 character) as an offset
        /// </summary>
        public int ToOffset(int line, int character)
        {
            var starts = LineStarts;
            if (line < 0) return 0;
            if (line >= starts.Length) return Text.Length;
            return Math.Min(starts[line] + Math.Max(0, character), line + 1 < starts.Length ? starts[line + 1] : Text.Length);
        }

        public (int Line, int Character) ToPosition(int offset)
        {
            var starts = LineStarts;
            offset = Math.Clamp(offset, 0, Text.Length);
            var line = Array.BinarySearch(starts, offset);
            if (line < 0) line = ~line - 1;
            return (line, offset - starts[line]);
        }

        public JsonObject Range(int start, int end)
        {
            var (sl, sc) = ToPosition(start);
            var (el, ec) = ToPosition(end);
            return new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
                ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
            };
        }
    }

    /// <summary>
    /// Open documents, and grammars read from disk because an open one @imports them
    /// </summary>
    sealed class Documents
    {
        static readonly StringComparer s_paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        readonly Dictionary<string, Document> _open = new Dictionary<string, Document>(s_paths);
        readonly Dictionary<string, (DateTime Stamp, Document Doc)> _disk = new Dictionary<string, (DateTime, Document)>(s_paths);
        readonly object _lock = new object();

        public static StringComparer PathComparer => s_paths;

        public static string ToPath(string uri)
        {
            var u = new Uri(uri);
            return u.IsFile ? System.IO.Path.GetFullPath(u.LocalPath) : uri;
        }

        public static string ToUri(string path) => new Uri(path).AbsoluteUri;

        public void Open(string path, string text, int version) { lock (_lock) _open[path] = new Document(path, text, version); }
        public void Close(string path) { lock (_lock) _open.Remove(path); }

        public Document? GetOpen(string path) { lock (_lock) return _open.TryGetValue(path, out var d) ? d : null; }

        /// <summary>
        /// The open document, or the file on disk, or null if neither
        /// </summary>
        public Document? Get(string path)
        {
            lock (_lock)
            {
                if (_open.TryGetValue(path, out var d)) return d;
                try
                {
                    if (!File.Exists(path)) return null;
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (_disk.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Doc;
                    var doc = new Document(path, File.ReadAllText(path), 0);
                    _disk[path] = (stamp, doc);
                    return doc;
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        /// <summary>
        /// For XbnfReader's @import: the open text if the file is open, else what's on disk
        /// </summary>
        public string ReadForImport(string path) => Get(System.IO.Path.GetFullPath(path))?.Text ?? throw new FileNotFoundException($"Could not find {path}", path);

        /// <summary>
        /// The document and everything it @imports, transitively, each once
        /// </summary>
        public List<Document> WithImports(Document root)
        {
            var result = new List<Document>();
            var seen = new HashSet<string>(s_paths);
            var stack = new Stack<Document>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!seen.Add(d.Path)) continue;
                result.Add(d);
                var dir = System.IO.Path.GetDirectoryName(d.Path) ?? "";
                foreach (var (p, _) in d.Scan.Imports)
                {
                    string full;
                    try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p)); }
                    catch (ArgumentException) { continue; }
                    var imported = Get(full);
                    if (null != imported) stack.Push(imported);
                }
            }
            return result;
        }

        /// <summary>
        /// The open documents that are the document or @import it, with their imports: every grammar a rename
        /// or a reference search in this one could touch
        /// </summary>
        public List<Document> Related(Document doc)
        {
            var result = new List<Document>();
            var seen = new HashSet<string>(s_paths);
            void add(Document d) { if (seen.Add(d.Path)) result.Add(d); }
            foreach (var d in WithImports(doc)) add(d);
            List<Document> open;
            lock (_lock) open = new List<Document>(_open.Values);
            foreach (var o in open)
            {
                var closure = WithImports(o);
                if (closure.Exists(d => s_paths.Equals(d.Path, doc.Path)))
                    foreach (var d in closure) add(d);
            }
            return result;
        }
    }
}
