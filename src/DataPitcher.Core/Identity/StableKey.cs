using System.Globalization;

namespace DataPitcher.Core.Identity;

public readonly record struct KeyComponent(string Column, IComparable? Value);

public sealed class StableKey : IEquatable<StableKey>, IComparable<StableKey>
{
    private readonly KeyComponent[] _components;
    public StableKey(IEnumerable<KeyComponent> components) => _components = components.ToArray();
    public IReadOnlyList<KeyComponent> Components => _components;
    public bool Equals(StableKey? other) => other is not null && _components.AsSpan().SequenceEqual(other._components);
    public override bool Equals(object? obj) => obj is StableKey other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var component in _components) hash.Add(component); return hash.ToHashCode(); }
    public static bool operator ==(StableKey? left, StableKey? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(StableKey? left, StableKey? right) => !(left == right);
    public int CompareTo(StableKey? other)
    {
        if (other is null) return 1;
        foreach (var pair in _components.Zip(other._components))
        {
            var column = StringComparer.Ordinal.Compare(pair.First.Column, pair.Second.Column);
            if (column != 0) return column;
            var value = CompareValues(pair.First.Value, pair.Second.Value);
            if (value != 0) return value;
        }
        return _components.Length.CompareTo(other._components.Length);
    }
    private static int CompareValues(IComparable? left, IComparable? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        var type = StringComparer.Ordinal.Compare(left.GetType().AssemblyQualifiedName, right.GetType().AssemblyQualifiedName);
        if (type != 0) return type;
        var result = left.CompareTo(right);
        return result != 0 ? result : StringComparer.Ordinal.Compare(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture));
    }
}
