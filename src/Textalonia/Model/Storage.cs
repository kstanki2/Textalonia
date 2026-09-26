using System.Numerics;
using System.Text;

namespace Textalonia.Model;

// The retention graph mirrors persistent ownership. Views/materialized export strings
// are deliberately not cached in these nodes.
internal interface IRetained
{
    long Bytes { get; }
    IEnumerable<object> References { get; }
}

internal readonly record struct OrderKey(BigInteger Numerator, int Scale) : IComparable<OrderKey>
{
    public OrderKey(int value) : this(new BigInteger(value), 0) { }
    public int CompareTo(OrderKey other) =>
        (Numerator << Math.Max(0, other.Scale - Scale)).CompareTo(other.Numerator << Math.Max(0, Scale - other.Scale));
    public static OrderKey Between(OrderKey? left, OrderKey? right)
    {
        if (left is null) return right is { } r ? new(r.Numerator - (BigInteger.One << r.Scale), r.Scale) : new(0);
        if (right is null) return new(left.Value.Numerator + (BigInteger.One << left.Value.Scale), left.Value.Scale);
        var scale = Math.Max(left.Value.Scale, right.Value.Scale);
        var numerator = (left.Value.Numerator << (scale - left.Value.Scale)) + (right.Value.Numerator << (scale - right.Value.Scale));
        scale++;
        while (scale > 0 && numerator.IsEven) { numerator >>= 1; scale--; }
        return new(numerator, scale);
    }
    public static OrderKey[] Insertions(OrderKey first, OrderKey? next, int count)
    {
        var keys = new OrderKey[count];
        if (count == 0) return keys;
        keys[0] = first;
        void Fill(int start, int length, OrderKey left, OrderKey? right)
        {
            if (length == 0) return;
            if (right is null)
            {
                for (var i = 0; i < length; i++) keys[start + i] = new(left.Numerator + ((BigInteger)(i + 1) << left.Scale), left.Scale);
                return;
            }
            var middle = length / 2; var key = Between(left, right);
            keys[start + middle] = key;
            Fill(start, middle, left, key); Fill(start + middle + 1, length - middle - 1, key, right);
        }
        Fill(1, count - 1, first, next); return keys;
    }
}

/// <summary>Immutable ordered AVL nodes with prefix measures. No parent pointers.</summary>
internal sealed class StorageTree<TKey, TValue> : IRetained where TKey : IComparable<TKey>
{
    public TKey Key { get; }
    public TValue Value { get; }
    public StorageTree<TKey, TValue>? Left { get; }
    public StorageTree<TKey, TValue>? Right { get; }
    public int Height { get; }
    public int Count { get; }
    public int Length { get; }
    public int Paragraphs { get; }
    private readonly int _length;
    private readonly int _paragraphs;

    public static StorageTree<TKey, TValue>? FromOrdered(IReadOnlyList<(TKey Key, TValue Value, int Length, int Paragraphs)> items)
    {
        StorageTree<TKey, TValue>? Build(int start, int count)
        {
            if (count == 0) return null;
            var middle = count / 2; var item = items[start + middle];
            return new(item.Key, item.Value, item.Length, item.Paragraphs,
                Build(start, middle), Build(start + middle + 1, count - middle - 1));
        }
        return Build(0, items.Count);
    }

    private StorageTree(TKey key, TValue value, int length, int paragraphs, StorageTree<TKey, TValue>? left, StorageTree<TKey, TValue>? right)
    {
        Key = key; Value = value; _length = length; _paragraphs = paragraphs; Left = left; Right = right;
        Height = 1 + Math.Max(H(left), H(right)); Count = 1 + (left?.Count ?? 0) + (right?.Count ?? 0);
        Length = checked(length + (left?.Length ?? 0) + (right?.Length ?? 0));
        Paragraphs = paragraphs + (left?.Paragraphs ?? 0) + (right?.Paragraphs ?? 0);
    }
    private static int H(StorageTree<TKey, TValue>? node) => node?.Height ?? 0;
    private StorageTree<TKey, TValue> Copy(StorageTree<TKey, TValue>? left, StorageTree<TKey, TValue>? right) => new(Key, Value, _length, _paragraphs, left, right);
    private StorageTree<TKey, TValue> Balance()
    {
        if (H(Left) - H(Right) > 1)
        {
            var left = Left!;
            if (H(left.Left) < H(left.Right)) left = left.RotateLeft();
            return Copy(left, Right).RotateRight();
        }
        if (H(Right) - H(Left) > 1)
        {
            var right = Right!;
            if (H(right.Right) < H(right.Left)) right = right.RotateRight();
            return Copy(Left, right).RotateLeft();
        }
        return this;
    }
    private StorageTree<TKey, TValue> RotateLeft() => Right!.Copy(Copy(Left, Right.Left), Right.Right);
    private StorageTree<TKey, TValue> RotateRight() => Left!.Copy(Left.Left, Copy(Left.Right, Right));
    public static StorageTree<TKey, TValue> Set(StorageTree<TKey, TValue>? node, TKey key, TValue value, int length = 0, int paragraphs = 0)
    {
        if (node is null) return new(key, value, length, paragraphs, null, null);
        var comparison = key.CompareTo(node.Key);
        return comparison == 0 ? new(key, value, length, paragraphs, node.Left, node.Right) :
            (comparison < 0 ? node.Copy(Set(node.Left, key, value, length, paragraphs), node.Right) :
                node.Copy(node.Left, Set(node.Right, key, value, length, paragraphs))).Balance();
    }
    public static StorageTree<TKey, TValue>? Remove(StorageTree<TKey, TValue>? node, TKey key)
    {
        if (node is null) return null;
        var comparison = key.CompareTo(node.Key);
        if (comparison < 0) return node.Copy(Remove(node.Left, key), node.Right).Balance();
        if (comparison > 0) return node.Copy(node.Left, Remove(node.Right, key)).Balance();
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        var next = node.Right;
        while (next.Left is not null) next = next.Left;
        return new StorageTree<TKey, TValue>(next.Key, next.Value, next._length, next._paragraphs, node.Left, Remove(node.Right, next.Key)).Balance();
    }
    public StorageTree<TKey, TValue>? Find(TKey key)
    {
        var node = this;
        while (node is not null)
        {
            var comparison = key.CompareTo(node.Key);
            if (comparison == 0) return node;
            node = comparison < 0 ? node.Left : node.Right;
        }
        return null;
    }
    public (int Length, int Paragraphs) Prefix(TKey key)
    {
        var length = 0; var paragraphs = 0; var node = this;
        while (node is not null)
        {
            var comparison = key.CompareTo(node.Key);
            if (comparison <= 0) node = node.Left;
            else { length += (node.Left?.Length ?? 0) + node._length; paragraphs += (node.Left?.Paragraphs ?? 0) + node._paragraphs; node = node.Right; }
        }
        return (length, paragraphs);
    }
    public (TKey Key, TValue Value, int Start) AtLength(int offset)
    {
        var node = this; var start = 0;
        while (true)
        {
            var left = node.Left?.Length ?? 0;
            if (offset < left) { node = node.Left!; continue; }
            if (offset < left + node._length || node.Right is null) return (node.Key, node.Value, start + left);
            offset -= left + node._length; start += left + node._length; node = node.Right;
        }
    }
    public IEnumerable<(TKey Key, TValue Value)> Items()
    {
        if (Left is not null) foreach (var pair in Left.Items()) yield return pair;
        yield return (Key, Value);
        if (Right is not null) foreach (var pair in Right.Items()) yield return pair;
    }
    public OrderKey? NextOrderKey(OrderKey key)
    {
        var node = this; OrderKey? result = null;
        while (node is not null)
        {
            var candidate = (OrderKey)(object)node.Key;
            if (candidate.CompareTo(key) > 0) { result = candidate; node = node.Left; } else node = node.Right;
        }
        return result;
    }
    public long Bytes => 112 + (Key is OrderKey order ? order.Numerator.GetByteCount() : 0);
    public IEnumerable<object> References
    {
        get { if (Left is not null) yield return Left; if (Right is not null) yield return Right; if (Value is object value) yield return value; }
    }
}

/// <summary>Persistent AVL rope. Leaves own bounded, immutable strings.</summary>
internal sealed class PieceText : IRetained
{
    private const int ChunkSize = 2048;
    public static PieceText Empty { get; } = new("");
    private readonly string? _buffer;
    private readonly int _start;
    private readonly PieceText? _left, _right;
    public int Length { get; }
    private int Height { get; }
    private PieceText(string buffer, int start = 0, int? length = null)
    { _buffer = buffer; _start = start; Length = length ?? buffer.Length; Height = 1; }
    private PieceText(PieceText left, PieceText right)
    { _left = left; _right = right; Length = checked(left.Length + right.Length); Height = 1 + Math.Max(left.Height, right.Height); }
    public static PieceText From(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= ChunkSize) return new(text);
        PieceText Build(int start, int length)
        {
            if (length <= ChunkSize) return new(text.Substring(start, length));
            var middle = length / 2;
            return new(Build(start, middle), Build(start + middle, length - middle));
        }
        return Build(0, text.Length);
    }
    public static PieceText Join(PieceText left, PieceText right)
    {
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        // Coalesce small pieces, with a fixed copying bound per edit.
        if (left.Length + right.Length <= 128) return new(left.ToString() + right.ToString());
        if (left.Height > right.Height + 1) return Balance(left._left!, Join(left._right!, right));
        if (right.Height > left.Height + 1) return Balance(Join(left, right._left!), right._right!);
        return new(left, right);
    }
    private static PieceText Balance(PieceText left, PieceText right)
    {
        if (left.Height > right.Height + 1)
        {
            if (left._left!.Height >= left._right!.Height) return new(left._left, new(left._right, right));
            return new(new(left._left, left._right._left!), new(left._right._right!, right));
        }
        if (right.Height > left.Height + 1)
        {
            if (right._right!.Height >= right._left!.Height) return new(new(left, right._left), right._right);
            return new(new(left, right._left._left!), new(right._left._right!, right._right));
        }
        return new(left, right);
    }
    public PieceText Slice(int start, int length)
    {
        if (start < 0 || length < 0 || start > Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        if (length == 0) return Empty;
        if (start == 0 && length == Length) return this;
        if (_buffer is not null) return new(_buffer, _start + start, length);
        if (start >= _left!.Length) return _right!.Slice(start - _left.Length, length);
        var first = Math.Min(length, _left.Length - start);
        return Join(_left.Slice(start, first), _right!.Slice(0, length - first));
    }
    public char this[int index] => _buffer is not null ? _buffer[_start + index] :
        index < _left!.Length ? _left[index] : _right![index - _left.Length];
    public IEnumerable<ReadOnlyMemory<char>> Chunks(int start, int length)
    {
        if (length == 0) yield break;
        if (_buffer is not null) { yield return _buffer.AsMemory(_start + start, length); yield break; }
        if (start < _left!.Length)
        {
            var first = Math.Min(length, _left.Length - start);
            foreach (var chunk in _left.Chunks(start, first)) yield return chunk;
            length -= first; start = 0;
        }
        else start -= _left.Length;
        if (length > 0) foreach (var chunk in _right!.Chunks(start, length)) yield return chunk;
    }
    public string Read(int start, int length)
    {
        if (start < 0 || length < 0 || start > Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        return string.Create(length, (Text: this, Start: start), static (span, state) =>
            state.Text.CopyTo(state.Start, span));
    }
    public void CopyTo(int start, Span<char> target)
    {
        if (target.IsEmpty) return;
        if (_buffer is not null) { _buffer.AsSpan(_start + start, target.Length).CopyTo(target); return; }
        if (start >= _left!.Length) { _right!.CopyTo(start - _left.Length, target); return; }
        var first = Math.Min(target.Length, _left.Length - start);
        _left.CopyTo(start, target[..first]); _right!.CopyTo(0, target[first..]);
    }
    public override string ToString() => _buffer is not null && _start == 0 && Length == _buffer.Length ? _buffer : Read(0, Length);
    public long Bytes => 64;
    public IEnumerable<object> References
    { get { if (_buffer is not null) yield return _buffer; else { yield return _left!; yield return _right!; } } }
}

// Compatibility views materialize only on explicit access, then preserve array
// identity. Their allocation is included conservatively in the owning node's
// estimate even before materialization. Equality must never force a lazy view.
internal sealed class SnapshotArray<T> : IEquatable<SnapshotArray<T>>
{
    private readonly System.Collections.Immutable.ImmutableArray<T> _eager;
    private readonly Lazy<System.Collections.Immutable.ImmutableArray<T>>? _lazy;
    public SnapshotArray(Func<System.Collections.Immutable.ImmutableArray<T>> read) => _lazy = new(read, true);
    private SnapshotArray(System.Collections.Immutable.ImmutableArray<T> array) => _eager = array;
    public System.Collections.Immutable.ImmutableArray<T> Read() => _lazy is null ? _eager : _lazy.Value;
    public static SnapshotArray<T> From(System.Collections.Immutable.ImmutableArray<T> array) => new(array);
    public bool Equals(SnapshotArray<T>? other) => ReferenceEquals(this, other) || other is not null &&
        (_lazy is null || _lazy.IsValueCreated) && (other._lazy is null || other._lazy.IsValueCreated) && Read().Equals(other.Read());
    public override bool Equals(object? obj) => obj is SnapshotArray<T> other && Equals(other);
    public override int GetHashCode() => Read().GetHashCode();
}
