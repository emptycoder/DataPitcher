using DataPitcher.Core.Connections;
using DataPitcher.Core.Schema;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlSchemaIntrospector : ISchemaIntrospector
{
    public async Task<SchemaSnapshotContent> ReadAsync(
        ConnectionProfile profile, string resolvedConnectionString, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(resolvedConnectionString);
        var catalog = await new PostgreSqlCatalogReader(dataSource).ReadAsync(profile.BusinessSchema, cancellationToken);
        return new SchemaSnapshotContent(
            catalog.Tables.Select(table => new SchemaTable(
                table.Definition.Schema,
                table.Definition.Name,
                table.Definition.Columns.Select(column => new SchemaColumn(
                    column.Name,
                    StoreType(column.ClrType),
                    column.ClrType.FullName ?? column.ClrType.Name,
                    column.IsNullable)),
                ToSchemaKey(table.Definition.PrimaryKey),
                table.Definition.UniqueConstraints.Select(key => new SchemaKey(key.Name, key.Columns)))),
            catalog.ForeignKeys.Select(foreignKey => new SchemaForeignKey(
                foreignKey.Name,
                new SchemaTableAddress(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name),
                new SchemaTableAddress(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name),
                foreignKey.ChildColumns,
                foreignKey.ParentColumns,
                foreignKey.IsEnforced,
                foreignKey.IsTrusted)));
    }

    private static SchemaKey? ToSchemaKey(UniqueConstraint? key) => key is null ? null : new SchemaKey(key.Name, key.Columns);

    private static string StoreType(Type type) => type == typeof(int) ? "integer" : "text";
}
