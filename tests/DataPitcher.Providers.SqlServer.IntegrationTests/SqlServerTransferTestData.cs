using System.Data;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.SqlServer;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

internal static class SqlServerTransferTestData
{
    public static SqlServerWriteTable Table() => new(new TableAddress("dbo", "transfer_rows"), [
        new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null),
        new("code", "nvarchar(64)", typeof(string), SqlDbType.NVarChar, false, false, false, false, false, "Latin1_General_100_BIN2")
    ]);

    public static SqlServerWriteTable TextKeyTable() => new(new TableAddress("dbo", "transfer_rows"), [
        new("code", "nvarchar(64)", typeof(string), SqlDbType.NVarChar, true, false, false, false, false, "Latin1_General_100_BIN2")
    ]);

    public static SqlServerTransferBatch Batch(long sequence, params (int Id, string Code)[] rows) => new(
        sequence,
        rows.Select(row => new SqlServerTransferRow(new StableKey([new KeyComponent("id", row.Id)]), new Dictionary<string, object?> { ["id"] = row.Id, ["code"] = row.Code })),
        new StableKey([new KeyComponent("id", rows.Last().Id)]),
        SqlServerConflictPolicy.InsertOnly);

    public static SqlServerExecutionContext Context(long fence = 1) => new(Guid.NewGuid(), Guid.NewGuid(), fence, "sealed-manifest-hash");
}
