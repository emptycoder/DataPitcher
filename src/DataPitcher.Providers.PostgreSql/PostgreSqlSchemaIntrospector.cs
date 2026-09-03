using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlSchemaIntrospector : ISchemaIntrospector
{
    public async Task<SchemaSnapshotContent> ReadAsync(
        ConnectionProfile profile,
        string resolvedConnectionString,
        CancellationToken cancellationToken
    )
    {
        await using var dataSource = NpgsqlDataSource.Create(resolvedConnectionString);
        var catalog = await new PostgreSqlCatalogReader(dataSource).ReadAsync(
            profile.BusinessSchema,
            cancellationToken
        );
        return new SchemaSnapshotContent(
            catalog.Tables.Select(table => new SchemaTable(
                table.Definition.Schema,
                table.Definition.Name,
                table.Definition.Columns.Select(column => new SchemaColumn(
                    column.Name,
                    StoreType(column.ClrType),
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

    /// <summary>A PostgreSQL type able to hold values of the CLR type; used for staging tables.</summary>
    internal static string StoreType(Type type) =>
        type == typeof(long) ? "bigint"
        : type == typeof(int) ? "integer"
        : type == typeof(short) ? "smallint"
        : type == typeof(bool) ? "boolean"
        : type == typeof(decimal) ? "numeric"
        : type == typeof(double) ? "double precision"
        : type == typeof(float) ? "real"
        : type == typeof(Guid) ? "uuid"
        : type == typeof(byte[]) ? "bytea"
        : type == typeof(DateOnly) ? "date"
        : type == typeof(TimeOnly) ? "time"
        : type == typeof(DateTime) ? "timestamp"
        : type == typeof(DateTimeOffset) ? "timestamptz"
        : type == typeof(TimeSpan) ? "interval"
        : "text";
}
