#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Parsley.Generator
{
    /// <summary>
    /// An immutable array compared by its contents, so pipeline steps holding one are cached when nothing changed
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
    {
        readonly T[]? _items;
        public EquatableArray(IEnumerable<T> items) => _items = items.ToArray();
        public static readonly EquatableArray<T> Empty = new EquatableArray<T>(Array.Empty<T>());
        public int Count => _items?.Length ?? 0;
        public T this[int index] => _items![index];
        public bool Equals(EquatableArray<T> other)
        {
            var a = _items ?? Array.Empty<T>();
            var b = other._items ?? Array.Empty<T>();
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; ++i)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);
        public override int GetHashCode()
        {
            var h = 17;
            if (null != _items)
                foreach (var item in _items) h = unchecked(h * 31 + item.GetHashCode());
            return h;
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A source location without the syntax tree, so it can be cached
    /// </summary>
    internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
    {
        public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
        public static LocationInfo? From(Location? location)
        {
            if (null == location || !location.IsInSource) return null;
            return new LocationInfo(location.SourceTree?.FilePath ?? "", location.SourceSpan, location.GetLineSpan().Span);
        }
    }

    internal sealed record DiagnosticInfo(string Id, DiagnosticSeverity Severity, string Message, LocationInfo? Location);

    /// <summary>
    /// A type the class is nested in, or the class itself: what's needed to declare another part of it
    /// </summary>
    internal sealed record TypeDeclarationInfo(string Keyword, string Name, string TypeParameters);

    /// <summary>
    /// A class marked with [Parsley]
    /// </summary>
    internal sealed record ParserClassInfo(
        string HintName,
        string? Namespace,
        EquatableArray<TypeDeclarationInfo> Types, // outermost first; the last is the class itself
        string SourceFilePath,
        string? GrammarFile,
        bool Unicode,
        bool Lexer,
        int Engine,
        EquatableArray<string> MemberNames,
        LocationInfo? AttributeLocation,
        EquatableArray<DiagnosticInfo> Diagnostics);

    /// <summary>
    /// An .xbnf file the project has as AdditionalFiles
    /// </summary>
    internal sealed record GrammarFileInfo(string Path, string Text);

    /// <summary>
    /// What the generator produced for one class. Engine is the parser engine it needs the base class of, or -1
    /// when it produced nothing. HasLexer is whether it produced a lexer, which needs the lexer runtime.
    /// </summary>
    internal sealed record GeneratedParser(string HintName, string? Source, int Engine, bool HasLexer, EquatableArray<DiagnosticInfo> Diagnostics);
}