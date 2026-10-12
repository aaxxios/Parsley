// Types and members the linked Parsley and LexGen sources use that netstandard2.0, which a source generator
// has to target, doesn't have. Internal to the generator.
#nullable enable
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    // records and init accessors
    internal static class IsExternalInit { }
}

namespace System
{
    // ^n indexing
    internal readonly struct Index : IEquatable<Index>
    {
        readonly int _value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative");
            _value = fromEnd ? ~value : value;
        }
        Index(int value) => _value = value;

        public static Index Start => new Index(0);
        public static Index End => new Index(~0);
        public static Index FromStart(int value) => value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : new Index(value);
        public static Index FromEnd(int value) => value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : new Index(~value);
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetOffset(int length)
        {
            var offset = _value;
            if (IsFromEnd) offset += length + 1;
            return offset;
        }
        public override bool Equals(object? value) => value is Index index && _value == index._value;
        public bool Equals(Index other) => _value == other._value;
        public override int GetHashCode() => _value;
        public static implicit operator Index(int value) => FromStart(value);
        public override string ToString() => IsFromEnd ? "^" + ((uint)Value).ToString() : ((uint)Value).ToString();
    }

    // a..b slicing
    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public override bool Equals(object? value) => value is Range r && r.Start.Equals(Start) && r.End.Equals(End);
        public bool Equals(Range other) => other.Start.Equals(Start) && other.End.Equals(End);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
        public override string ToString() => Start + ".." + End;
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end) throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
    }
}

namespace System.Collections.Generic
{
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, System.Collections.IEqualityComparer
    {
        ReferenceEqualityComparer() { }
        public static ReferenceEqualityComparer Instance { get; } = new ReferenceEqualityComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
    }

    internal static class ParsleyGeneratorPolyfills
    {
        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value) where TKey : notnull
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}
