namespace DataPitcher.ControlStore;

/// <summary>Factory for native SQLite connections to the control database.</summary>
public sealed class ControlDatabase(string connectionString)
{
    public ControlConnection Open() => new(connectionString);
}
