namespace DataPitcher.Core.Authorization;

public sealed class PermissionSet : IEquatable<PermissionSet>
{
    private readonly HashSet<Permission> permissions;

    public PermissionSet(IEnumerable<Permission> permissions)
    {
        this.permissions = [.. permissions];
        Permissions = Array.AsReadOnly(
            this.permissions.OrderBy(permission => permission.Value, StringComparer.Ordinal).ToArray()
        );
    }

    public static PermissionSet Empty { get; } = new([]);

    public IReadOnlyCollection<Permission> Permissions { get; }

    public bool Contains(Permission permission) => permissions.Contains(permission);

    public PermissionSet Union(PermissionSet other) => new(permissions.Concat(other.permissions));

    public PermissionSet Without(Permission permission) => new(permissions.Where(candidate => candidate != permission));

    public bool IsSubsetOf(PermissionSet other) => permissions.IsSubsetOf(other.permissions);

    public bool Equals(PermissionSet? other) => other is not null && permissions.SetEquals(other.permissions);

    public override bool Equals(object? obj) => obj is PermissionSet other && Equals(other);

    public override int GetHashCode() =>
        permissions.Aggregate(0, (hash, permission) => hash ^ permission.GetHashCode());
}
