namespace DataPitcher.Core.Identity;

public readonly record struct KeyComponent(string Column, object? Value)
{
    public bool Equals(KeyComponent other) =>
        DataPitcher.Core.Schema.DatabaseNames.Equals(Column, other.Column)
        && (
            Value is byte[] bytes && other.Value is byte[] otherBytes
                ? bytes.AsSpan().SequenceEqual(otherBytes)
                : EqualityComparer<object?>.Default.Equals(Value, other.Value)
        );

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Column, DataPitcher.Core.Schema.DatabaseNames.Comparer);
        if (Value is byte[] bytes)
            foreach (var value in bytes)
                hash.Add(value);
        else
            hash.Add(Value);
        return hash.ToHashCode();
    }
}

public sealed class StableKey : IEquatable<StableKey>, IComparable<StableKey>
{
    private readonly KeyComponent[] _components;

    public StableKey(IEnumerable<KeyComponent> components)
    {
        _components = components.ToArray();
        if (_components.Length == 0)
            throw new ArgumentException("Stable keys must include at least one component.");
    }

    public IReadOnlyList<KeyComponent> Components => _components;

    public bool Equals(StableKey? other) => other is not null && _components.AsSpan().SequenceEqual(other._components);

    public override bool Equals(object? obj) => obj is StableKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in _components)
            hash.Add(component);
        return hash.ToHashCode();
    }

    public static bool operator ==(StableKey? left, StableKey? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(StableKey? left, StableKey? right) => !(left == right);

    public int CompareTo(StableKey? other)
    {
        if (other is null)
            return 1;
        foreach (var pair in _components.Zip(other._components))
        {
            var column = DataPitcher.Core.Schema.DatabaseNames.Comparer.Compare(pair.First.Column, pair.Second.Column);
            if (column != 0)
                return column;
            var value = CompareValues(pair.First.Value, pair.Second.Value);
            if (value != 0)
                return value;
        }
        return _components.Length.CompareTo(other._components.Length);
    }

    private static int CompareValues(object? left, object? right)
    {
        if (left is null)
            return right is null ? 0 : -1;
        if (right is null)
            return 1;
        var type = StringComparer.Ordinal.Compare(
            left.GetType().AssemblyQualifiedName,
            right.GetType().AssemblyQualifiedName
        );
        if (type != 0)
            return type;
        if (left is string leftString && right is string rightString)
            return StringComparer.Ordinal.Compare(leftString, rightString);
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
        return ((IComparable)left).CompareTo(right);
    }
}
