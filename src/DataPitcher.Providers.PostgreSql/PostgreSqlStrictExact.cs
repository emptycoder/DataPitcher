using DataPitcher.Core.Identity;
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlStrictExact(NpgsqlDataSource dataSource)
{
    public async Task EnsureAvailableAsync(PostgreSqlWriteTable table, CancellationToken cancellationToken)
    {
        var target = PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name);
        if (
            await ExistsAsync(
                "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid=@target::regclass AND NOT tgisinternal AND tgenabled <> 'D')",
                target,
                cancellationToken
            )
        )
            throw new PostgreSqlStrictExactBlockedException("StrictExact is blocked by a target trigger.");
        if (
            await ExistsAsync(
                "SELECT EXISTS (SELECT 1 FROM pg_rewrite WHERE ev_class=@target::regclass AND rulename <> '_RETURN')",
                target,
                cancellationToken
            )
        )
            throw new PostgreSqlStrictExactBlockedException("StrictExact is blocked by a target rewrite rule.");
        if (
            await ExistsAsync(
                "SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE confrelid=@target::regclass AND contype='f' AND confupdtype IN ('c','n','d'))",
                target,
                cancellationToken
            )
        )
            throw new PostgreSqlStrictExactBlockedException("StrictExact is blocked by a target cascading write path.");
    }

    public async Task RecordPlannedAsync(
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (
            var create = new NpgsqlCommand(
                "CREATE SCHEMA IF NOT EXISTS datapitcher; CREATE TABLE IF NOT EXISTS datapitcher.transfer_write_manifest (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key)); CREATE TABLE IF NOT EXISTS datapitcher.transfer_affected_keys (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key));",
                connection,
                transaction
            )
        )
            await create.ExecuteNonQueryAsync(cancellationToken);
        foreach (var key in keys)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO datapitcher.transfer_write_manifest VALUES (@job,@run,@schema,@table,@key) ON CONFLICT DO NOTHING",
                connection,
                transaction
            );
            insert.Parameters.AddWithValue("job", context.JobId);
            insert.Parameters.AddWithValue("run", context.RunId);
            insert.Parameters.AddWithValue("schema", table.Target.Schema);
            insert.Parameters.AddWithValue("table", table.Target.Name);
            insert.Parameters.AddWithValue("key", PostgreSqlStableKeyCodec.Encode(key, table));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task VerifyAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken)
    {
        const string sql =
            "(SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_affected_keys WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_write_manifest WHERE job_id=@job AND run_id=@run) UNION ALL (SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_write_manifest WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_affected_keys WHERE job_id=@job AND run_id=@run)";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Committed affected keys differ from the planned write manifest.");
    }

    private async Task<bool> ExistsAsync(string sql, string target, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("target", target);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

public sealed class PostgreSqlSequenceRealigner(NpgsqlDataSource dataSource)
{
    public async Task RealignAsync(PostgreSqlWriteTable table, string column, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (
            var lockCommand = new NpgsqlCommand(
                "LOCK TABLE "
                    + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name)
                    + " IN ACCESS EXCLUSIVE MODE",
                connection,
                transaction
            )
        )
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);

        const string sequenceSql =
            "SELECT seq.oid::regclass::text,seq.oid,s.seqincrement,s.seqcycle,tab.oid,att.attnum FROM pg_class tab JOIN pg_namespace ns ON ns.oid=tab.relnamespace JOIN pg_attribute att ON att.attrelid=tab.oid JOIN pg_depend dep ON dep.refobjid=tab.oid AND dep.refobjsubid=att.attnum AND dep.deptype IN ('a','i') JOIN pg_class seq ON seq.oid=dep.objid JOIN pg_sequence s ON s.seqrelid=seq.oid WHERE ns.nspname=@schema AND tab.relname=@table AND att.attname=@column";
        (string Name, uint SequenceOid, long Increment, bool Cycles, uint TableOid, short ColumnNumber)? owned = null;
        await using (var sequence = new NpgsqlCommand(sequenceSql, connection, transaction))
        {
            sequence.Parameters.AddWithValue("schema", table.Target.Schema);
            sequence.Parameters.AddWithValue("table", table.Target.Name);
            sequence.Parameters.AddWithValue("column", column);
            await using var reader = await sequence.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                owned = new(
                    reader.GetString(0),
                    reader.GetFieldValue<uint>(1),
                    reader.GetInt64(2),
                    reader.GetBoolean(3),
                    reader.GetFieldValue<uint>(4),
                    reader.GetInt16(5)
                );
        }

        if (owned is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var (name, sequenceOid, increment, cycles, tableOid, columnNumber) = owned.Value;

        if (cycles)
            throw new NotSupportedException("Cycling sequences are not supported.");

        const string sharedSql =
            "SELECT EXISTS (SELECT 1 FROM pg_depend dep LEFT JOIN pg_attrdef def ON def.oid=dep.objid AND dep.classid='pg_attrdef'::regclass WHERE dep.refobjid=@sequence AND (def.oid IS NULL OR def.adrelid<>@table OR def.adnum<>@column))";
        await using (var dependents = new NpgsqlCommand(sharedSql, connection, transaction))
        {
            dependents.Parameters.AddWithValue("sequence", NpgsqlDbType.Oid, sequenceOid);
            dependents.Parameters.AddWithValue("table", NpgsqlDbType.Oid, tableOid);
            dependents.Parameters.AddWithValue("column", NpgsqlDbType.Smallint, columnNumber);
            if ((bool)(await dependents.ExecuteScalarAsync(cancellationToken))!)
                throw new NotSupportedException("Shared sequences are not supported.");
        }

        long last;
        bool called;
        await using (
            var current = new NpgsqlCommand("SELECT last_value,is_called FROM " + name, connection, transaction)
        )
        {
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            last = reader.GetInt64(0);
            called = reader.GetBoolean(1);
        }

        await using var extreme = new NpgsqlCommand(
            "SELECT "
                + (increment > 0 ? "max" : "min")
                + "("
                + PostgreSqlIdentifier.Quote(column)
                + ") FROM "
                + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name),
            connection,
            transaction
        );
        if (await extreme.ExecuteScalarAsync(cancellationToken) is not long bound)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var next = called ? checked(last + increment) : last;
        var needsRealignment = increment > 0 ? next <= bound : next >= bound;
        if (!needsRealignment)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var set = new NpgsqlCommand(
            "SELECT setval(@sequence::regclass,@value,true)",
            connection,
            transaction
        );
        set.Parameters.AddWithValue("sequence", name);
        set.Parameters.AddWithValue("value", bound);
        await set.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
