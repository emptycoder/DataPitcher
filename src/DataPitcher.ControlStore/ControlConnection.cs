using System.Data;
using Microsoft.Data.Sqlite;

namespace DataPitcher.ControlStore;

/// <summary>A named command parameter for the control store's native SQLite access.</summary>
public sealed record ControlParameter(string Name, object? Value);

/// <summary>
/// Thin, allocation-light wrapper over a <see cref="SqliteConnection"/>: opens with foreign keys enforced, runs
/// parameterised statements, and maps rows through explicit reader delegates. No ORM, no reflection.
/// </summary>
public sealed class ControlConnection : IDisposable
{
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    internal ControlConnection(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _connection;

    public ControlTransaction BeginTransaction()
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A control-store transaction is already active.");
        _transaction = _connection.BeginTransaction();
        return new ControlTransaction(this, _transaction);
    }

    public int Execute(string sql, params ControlParameter[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public async Task<int> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        params ControlParameter[] parameters
    )
    {
        using var command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public T? Scalar<T>(string sql, params ControlParameter[] parameters)
    {
        using var command = Command(sql, parameters);
        return Convert<T>(command.ExecuteScalar());
    }

    public IReadOnlyList<T> Query<T>(string sql, params ControlParameter[] parameters) =>
        Query(sql, reader => Convert<T>(reader.GetValue(0))!, parameters);

    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params ControlParameter[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params ControlParameter[] parameters
    )
    {
        using var command = Command(sql, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(map(reader));
        return rows;
    }

    public T? Single<T>(string sql, Func<SqliteDataReader, T> map, params ControlParameter[] parameters)
        where T : class
    {
        var rows = Query(sql, map, parameters);
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException("The query returned more than one row."),
        };
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection.Dispose();
    }

    internal void EndTransaction() => _transaction = null;

    private SqliteCommand Command(string sql, ControlParameter[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(
                parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name,
                parameter.Value ?? DBNull.Value
            );
        return command;
    }

    private static T? Convert<T>(object? value)
    {
        if (value is null || value is DBNull)
            return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(Guid))
            return (T)(object)Guid.Parse((string)value);
        return (T)System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class ControlTransaction(ControlConnection owner, SqliteTransaction transaction) : IDisposable
{
    private bool _completed;

    public void Commit()
    {
        transaction.Commit();
        _completed = true;
        owner.EndTransaction();
    }

    public void Dispose()
    {
        if (!_completed)
        {
            transaction.Dispose();
            owner.EndTransaction();
        }
        else
            transaction.Dispose();
    }
}
