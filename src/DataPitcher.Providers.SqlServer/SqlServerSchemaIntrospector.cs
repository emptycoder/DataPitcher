using DataPitcher.Core.Connections;
using DataPitcher.Core.Schema;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerSchemaIntrospector : ISchemaIntrospector
{
    public async Task<SchemaSnapshotContent> ReadAsync(
        ConnectionProfile profile,
        string resolvedConnectionString,
        CancellationToken cancellationToken
    )
    {
        var catalog = await new SqlServerCatalogReader(resolvedConnectionString).ReadAsync(
            profile.BusinessSchema,
            cancellationToken
        );
        return new SchemaSnapshotContent(
            catalog.Tables.Select(table => new SchemaTable(
                table.Definition.Schema,
                table.Definition.Name,
                table.Columns.Select(column => new SchemaColumn(
                    column.Name,
                    column.StoreType,
                    column.ClrType.FullName ?? column.ClrType.Name,
                    column.IsNullable
                )),
                ToSchemaKey(table.Definition.PrimaryKey),
                table.Definition.UniqueConstraints.Select(key => new SchemaKey(key.Name, key.Columns))
            )),
            catalog.ForeignKeys.Select(foreignKey => new SchemaForeignKey(
                foreignKey.Name,
                new SchemaTableAddress(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name),
                new SchemaTableAddress(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name),
                foreignKey.ChildColumns,
                foreignKey.ParentColumns,
                foreignKey.IsEnforced,
                foreignKey.IsTrusted
            ))
        );
    }

    private static SchemaKey? ToSchemaKey(UniqueConstraint? key) =>
        key is null ? null : new SchemaKey(key.Name, key.Columns);
}
