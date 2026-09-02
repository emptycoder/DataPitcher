using DataPitcher.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

public sealed class SqlServerWireCommandRecorder : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name;

    private SqlServerWireCommandRecorder(string admin, string name)
    {
        _admin = admin;
        _name = name;
    }

    public static async Task<SqlServerWireCommandRecorder> StartAsync(string admin, string tag)
    {
        var recorder = new SqlServerWireCommandRecorder(admin, "dp_xe_" + Guid.NewGuid().ToString("N"));
        await recorder.ExecuteAsync(
            $"CREATE EVENT SESSION [{recorder._name}] ON SERVER " +
            "ADD EVENT sqlserver.rpc_completed(ACTION(sqlserver.sql_text) " +
            "WHERE (object_name <> N'sp_reset_connection' AND " +
            $"[sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%{tag}%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'CREATE EVENT SESSION%'))), " +
            "ADD EVENT sqlserver.sql_batch_completed(ACTION(sqlserver.sql_text) " +
            $"WHERE ([sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%{tag}%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'CREATE EVENT SESSION%'))) " +
            "ADD TARGET package0.ring_buffer; " +
            $"ALTER EVENT SESSION [{recorder._name}] ON SERVER STATE=START;");
        return recorder;
    }

    public async Task<IReadOnlyList<string>> SqlTextsAsync()
    {
        await using var connection = new SqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT n.e.value('(action[@name=\"sql_text\"]/value)[1]','nvarchar(max)') " +
            "FROM sys.dm_xe_session_targets t " +
            "JOIN sys.dm_xe_sessions s ON s.address=t.event_session_address " +
            "CROSS APPLY (SELECT CAST(t.target_data AS xml) x) d " +
            "CROSS APPLY d.x.nodes('/RingBufferTarget/event') n(e) " +
            "WHERE s.name=@name", connection);
        command.Parameters.AddWithValue("@name", _name);
        await using var rows = await command.ExecuteReaderAsync();
        var texts = new List<string>();
        while (await rows.ReadAsync())
            if (!rows.IsDBNull(0))
                texts.Add(rows.GetString(0));
        return texts;
    }

    public async Task<int> Count(string tag) => (await SqlTextsAsync()).Count(x => x.Contains(tag, StringComparison.Ordinal));

    public async Task<int> Count(string tag, string table) =>
        (await SqlTextsAsync()).Count(text => text.Contains(tag, StringComparison.Ordinal) && text.Contains(SqlServerIdentifier.Quote(table), StringComparison.Ordinal));

    public async Task<bool> AnyContainsLargeInListAsync(int threshold) =>
        (await SqlTextsAsync()).Any(text => text.Split(" IN (", StringSplitOptions.None).Skip(1).Any(part => part.Split(')')[0].Split(',').Length > threshold));

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync($"ALTER EVENT SESSION [{_name}] ON SERVER STATE=STOP; DROP EVENT SESSION [{_name}] ON SERVER;");
    }
}
