using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.PostgreSql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

internal static class PostgreSqlTransferTestData
{
    public static PostgreSqlWriteTable Table(string schema) =>
        new(
            new(schema, "transfer_rows"),
            [
                new("id", "integer", NpgsqlDbType.Integer, true, false, false, false, null),
                new("code", "text", NpgsqlDbType.Text, false, false, false, false, "C"),
                new("computed", "integer", NpgsqlDbType.Integer, false, true, false, false, null),
            ]
        );

    public static PostgreSqlWriteTable TextKeyTable(string schema) =>
        new(new(schema, "transfer_rows"), [new("code", "text", NpgsqlDbType.Text, true, false, false, false, "C")]);

    public static PostgreSqlTransferBatch Batch(long sequence, params (int Id, string Code)[] rows) =>
        new(
            sequence,
            rows.Select(row => new PostgreSqlTransferRow(
                new StableKey([new KeyComponent("id", row.Id)]),
                new Dictionary<string, object?> { ["id"] = row.Id, ["code"] = row.Code }
            )),
            new StableKey([new KeyComponent("id", rows.Last().Id)]),
            PostgreSqlConflictPolicy.InsertOnly
        );

    public static PostgreSqlExecutionContext Context(long fence = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), fence, "sealed-manifest-hash");
}
