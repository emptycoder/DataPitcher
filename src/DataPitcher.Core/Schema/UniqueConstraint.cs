namespace DataPitcher.Core.Schema;

public sealed record UniqueConstraint
{
    public UniqueConstraint(string name, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
            throw new ArgumentException("Unique constraints must include at least one column.");

        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
}
