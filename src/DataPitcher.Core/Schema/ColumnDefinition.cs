namespace DataPitcher.Core.Schema;

public sealed record ColumnDefinition(string Name, Type ClrType, bool IsNullable);
