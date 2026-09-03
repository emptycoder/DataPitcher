using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DataPitcher.Core.Plans;

public static class CanonicalPlanHasher
{
    public static string Hash(TransferPlanContent plan)
    {
        var w = new Writer();
        w.Text("DataPitcher.TransferPlan.v1");
        Connection(w, plan.Source);
        Connection(w, plan.Target);
        w.Text(plan.SourceSchema.Hash);
        w.Text(plan.TargetSchema.Hash);
        Unordered(w, plan.Selections, Selection);
        Unordered(w, plan.Relationships, Relationship);
        Unordered(w, plan.ConflictPolicies, Conflict);
        w.Int((int)plan.ConsistencyMode);
        w.Int((int)plan.TransferMode);
        w.Int((int)plan.TriggerStrategy);
        w.Int((int)plan.ConstraintStrategy);
        Unordered(w, plan.StableKeys, StableKey);
        Unordered(w, plan.Tables, Table);
        Batch(w, plan.BatchTarget);
        w.Int((int)plan.VerificationStrategy);
        Counts(w, plan.ManifestTotals);
        return Convert.ToHexString(SHA256.HashData(w.Bytes));
    }

    private static void Connection(Writer w, ConnectionFingerprint x)
    {
        w.Text(x.Provider);
        w.Text(x.DatabaseIdentity);
        w.Text(x.Fingerprint);
        w.Text(x.ConnectionId.ToString("D"));
    }

    private static void Selection(Writer w, SelectionReference x)
    {
        w.Text(x.SelectionId.ToString("D"));
        w.Long(x.Version);
        w.Text(x.ParameterHash);
    }

    private static void Relationship(Writer w, RelationshipPolicy x)
    {
        w.Text(x.Name);
        Address(w, x.From);
        Address(w, x.To);
        Ordered(w, x.FromColumns, (a, v) => a.Text(v));
        Ordered(w, x.ToColumns, (a, v) => a.Text(v));
        w.Int((int)x.Direction);
        w.Bool(x.IsEnabled);
    }

    private static void Conflict(Writer w, TableConflictPolicy x)
    {
        Address(w, x.Table);
        w.Int((int)x.Policy);
    }

    private static void StableKey(Writer w, StableKeyDefinition x)
    {
        Address(w, x.Table);
        w.Text(x.ConstraintName);
        Ordered(w, x.Columns, (a, v) => a.Text(v));
    }

    private static void Table(Writer w, PlanTable x)
    {
        Mapping(w, x.Mapping);
        w.Int((int)x.State);
        Counts(w, x.Manifest);
        Group(w, x.TopologicalGroup);
        w.Int((int)x.CycleStrategy);
        // Only written when present so plans sealed before deferred columns existed keep their hash.
        if (x.DeferredColumns.Count > 0)
            Ordered(w, x.DeferredColumns, (a, v) => a.Text(v));
        if (x.HierarchyColumns.Count > 0)
            Ordered(w, x.HierarchyColumns, (a, v) => a.Text(v));
    }

    private static void Mapping(Writer w, TableMapping x)
    {
        Address(w, x.Source);
        Address(w, x.Target);
        Unordered(
            w,
            x.Columns,
            (a, v) =>
            {
                a.Text(v.Source);
                a.Text(v.Target);
            }
        );
    }

    private static void Group(Writer w, TopologicalGroup x) => Unordered(w, x.Tables, Address);

    private static void Batch(Writer w, BatchTarget x)
    {
        w.Int(x.MaximumRows);
        w.Int(x.MaximumBytes);
    }

    private static void Counts(Writer w, ManifestCounts x)
    {
        w.Long(x.Included);
        w.Long(x.PlannedWrites);
        w.Long(x.Inserts);
        w.Long(x.Updates);
    }

    private static void Address(Writer w, TableAddress x)
    {
        w.Text(x.Schema);
        w.Text(x.Name);
    }

    private static void Ordered<T>(Writer w, IEnumerable<T> values, Action<Writer, T> item)
    {
        var all = values.ToArray();
        w.Int(all.Length);
        foreach (var value in all)
            item(w, value);
    }

    private static void Unordered<T>(Writer w, IEnumerable<T> values, Action<Writer, T> item)
    {
        var all = values
            .Select(value =>
            {
                var nested = new Writer();
                item(nested, value);
                return nested.Bytes.ToArray();
            })
            .OrderBy(bytes => Convert.ToHexString(bytes), StringComparer.Ordinal)
            .ToArray();
        w.Int(all.Length);
        foreach (var value in all)
            w.Raw(value);
    }

    private sealed class Writer
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public ReadOnlySpan<byte> Bytes => _buffer.WrittenSpan;

        public void Bool(bool value) => Int(value ? 1 : 0);

        public void Int(int value)
        {
            var span = _buffer.GetSpan(4);
            BinaryPrimitives.WriteInt32BigEndian(span, value);
            _buffer.Advance(4);
        }

        public void Long(long value)
        {
            var span = _buffer.GetSpan(8);
            BinaryPrimitives.WriteInt64BigEndian(span, value);
            _buffer.Advance(8);
        }

        public void Text(string value)
        {
            Int(value.Length);
            foreach (var character in value)
            {
                var span = _buffer.GetSpan(2);
                BinaryPrimitives.WriteUInt16BigEndian(span, character);
                _buffer.Advance(2);
            }
        }

        public void Raw(ReadOnlySpan<byte> value)
        {
            var span = _buffer.GetSpan(value.Length);
            value.CopyTo(span);
            _buffer.Advance(value.Length);
        }
    }
}
