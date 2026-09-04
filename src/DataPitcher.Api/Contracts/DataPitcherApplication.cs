using System.Text.Json;
using DataPitcher.Application.Connections;
using DataPitcher.Application.Events;
using DataPitcher.Application.Plans;
using DataPitcher.Application.Schema;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Api.Contracts;

public sealed class PlanNotFoundException() : InvalidOperationException("Plan was not found.");

public sealed class PlanNotSealedException() : InvalidOperationException("Plan must be sealed before starting a job.");

public sealed class SelectionNotFoundException() : InvalidOperationException("Selection was not found.");

public sealed class SnapshotNotFoundException() : InvalidOperationException("Schema snapshot was not found.");

/// <summary>
/// Production <see cref="IDataPitcherApplication"/> delegating to the real control-database stores and connection
/// services. Connection, schema-scan/snapshot, selection, plan sealing, and job workflows are backed by durable,
/// tested Infrastructure services. Inclusion-path lookup requires persisted closure provenance.
/// </summary>
public sealed class DataPitcherApplication(
    IConnectionProfileRepository connections,
    ConnectionHealthService health,
    ISchemaSnapshotRepository snapshots,
    ISelectionRepository selections,
    IPlanRepository plans,
    IJobRepository jobs,
    IJobEventReader jobEvents,
    PlanSealingService? sealing = null,
    ISecretWriter? secretWriter = null,
    IConnectionProviderRegistry? providers = null,
    ISecretReferenceResolver? secretResolver = null
) : IDataPitcherApplication
{
    private const string DefaultStagingSchema = "__datapitcher";

    /// <summary>The schema most databases keep their tables in, unless the operator names another.</summary>
    private static string DefaultBusinessSchema(string providerId) =>
        string.Equals(providerId, "postgresql", StringComparison.OrdinalIgnoreCase) ? "public" : "dbo";

    private static string BusinessSchemaOrDefault(string? requested, string providerId, string? fallback = null)
    {
        var schema = requested?.Trim();
        if (string.IsNullOrEmpty(schema))
            return fallback ?? DefaultBusinessSchema(providerId);
        if (schema.Length > 128)
            throw new ArgumentException("Schema names are at most 128 characters.", nameof(requested));
        return schema;
    }

    public async Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var summaries = await connections.ListSummariesAsync(cancellationToken);
        return summaries.Select(ToConnectionResponse).ToArray();
    }

    public async Task<ConnectionResponse> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            throw new ArgumentException("A connection string is required.", nameof(request));
        var secretReference = await (
            secretWriter ?? throw new InvalidOperationException("Connection secret storage is not configured.")
        ).StoreAsync(request.CredentialId, request.ConnectionString, cancellationToken);
        var draft = new ConnectionProfileDraft(
            request.DisplayName,
            request.ProviderId,
            secretReference,
            BusinessSchemaOrDefault(request.BusinessSchema, request.ProviderId),
            DefaultStagingSchema
        );
        var idempotencyKey = request.IfMatch.Trim() == "*" ? request.CredentialId.ToString("N") : request.IfMatch;
        var profile = await connections.CreateAsync(draft, idempotencyKey, cancellationToken);
        return new ConnectionResponse(
            profile.ConnectionId,
            profile.DisplayName,
            profile.ProviderId,
            ConnectionHealthState.Unknown.ToString(),
            ETag(profile.Version)
        );
    }

    public async Task<ConnectionResponse> UpdateConnectionAsync(
        Guid connectionId,
        UpdateConnectionRequest request,
        CancellationToken cancellationToken
    )
    {
        var existing = await connections.GetProfileAsync(connectionId, cancellationToken);
        var replacingSecret = !string.IsNullOrWhiteSpace(request.ConnectionString);
        var secretReference = replacingSecret
            ? await (
                secretWriter ?? throw new InvalidOperationException("Connection secret storage is not configured.")
            ).StoreAsync(
                Guid.NewGuid(),
                request.KeepStoredPassword
                    ? await WithStoredPasswordAsync(existing, request.ConnectionString!, cancellationToken)
                    : request.ConnectionString!,
                cancellationToken
            )
            : existing.SecretReference;
        var profile = await connections.UpdateAsync(
            connectionId,
            new ConnectionProfileDraft(
                request.DisplayName,
                request.ProviderId,
                secretReference,
                BusinessSchemaOrDefault(request.BusinessSchema, request.ProviderId, existing.BusinessSchema),
                existing.StagingSchema
            ),
            request.IfMatch,
            cancellationToken
        );
        if (replacingSecret && secretWriter is not null)
            await secretWriter.RemoveAsync(existing.SecretReference, cancellationToken);
        var summary = await connections.GetSummaryAsync(profile.ConnectionId, cancellationToken);
        return ToConnectionResponse(summary);
    }

    public async Task<ConnectionDetailsResponse> GetConnectionDetailsAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        var profile = await connections.GetProfileAsync(connectionId, cancellationToken);
        var stored = await ResolveSecretAsync(profile, cancellationToken);
        var (redacted, hasPassword) = ConnectionStringSecrets.Redact(stored);
        return new ConnectionDetailsResponse(
            profile.ConnectionId,
            profile.ProviderId,
            redacted,
            hasPassword,
            profile.BusinessSchema
        );
    }

    private Task<string> ResolveSecretAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
        (secretResolver ?? throw new InvalidOperationException("Secret resolution is not configured.")).ResolveAsync(
            profile.SecretReference,
            cancellationToken
        );

    /// <summary>Appends the password stored for <paramref name="profile"/> to a connection string that carries none.</summary>
    private async Task<string> WithStoredPasswordAsync(
        ConnectionProfile profile,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        if (ConnectionStringSecrets.ExtractPassword(connectionString) is not null)
            return connectionString;
        var stored = await ResolveSecretAsync(profile, cancellationToken);
        return ConnectionStringSecrets.ExtractPassword(stored) is { } password
            ? ConnectionStringSecrets.WithPassword(connectionString, password)
            : connectionString;
    }

    public async Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken
    )
    {
        var registry = providers ?? throw new InvalidOperationException("Connection providers are not configured.");
        ConnectionProfile profile;
        string connectionString;
        if (!string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            profile = new ConnectionProfile(
                request.ConnectionId ?? Guid.Empty,
                "connection test",
                request.ProviderId,
                new SecretReference(SecretReferenceKind.EnvironmentVariable, "DATAPITCHER_CONNECTION_TEST"),
                BusinessSchemaOrDefault(request.BusinessSchema, request.ProviderId),
                DefaultStagingSchema,
                0
            );
            connectionString =
                request.KeepStoredPassword && request.ConnectionId is Guid storedConnectionId
                    ? await WithStoredPasswordAsync(
                        await connections.GetProfileAsync(storedConnectionId, cancellationToken),
                        request.ConnectionString,
                        cancellationToken
                    )
                    : request.ConnectionString;
        }
        else if (request.ConnectionId is Guid connectionId)
        {
            profile = await connections.GetProfileAsync(connectionId, cancellationToken);
            connectionString = await (
                secretResolver ?? throw new InvalidOperationException("Secret resolution is not configured.")
            ).ResolveAsync(profile.SecretReference, cancellationToken);
        }
        else
            throw new ArgumentException("A connection string or an existing connection is required.", nameof(request));

        IConnectionProvider provider;
        try
        {
            provider = registry.Get(profile.ProviderId);
        }
        catch (UnsupportedConnectionProviderException exception)
        {
            return new ConnectionTestResponse(
                false,
                ConnectionHealthState.Unhealthy.ToString(),
                null,
                null,
                [],
                [],
                exception.Message
            );
        }
        // The same mode plans are sealed with, so the dialog and the pre-transfer revalidation agree.
        const TransferMode mode = TransferMode.ResumableStaged;
        var requirements = ConnectionRequirements.For(mode, ConnectionRole.Source);
        try
        {
            var evidence = await provider.CapabilityDetector.ProbeAsync(
                new ConnectionProbeRequest(profile, ConnectionRole.Source, mode, connectionString),
                cancellationToken
            );
            var assessment = ConnectionHealthClassifier.Classify(requirements, evidence);
            var usable = ConnectionHealthService.IsUsable(assessment.State);
            // Targets need more (insert, identity, staging); report that readiness separately.
            IReadOnlyList<string>? targetMissing = null;
            var notes = evidence.Notes.ToList();
            try
            {
                var asTarget = ConnectionHealthClassifier.Classify(
                    ConnectionRequirements.For(mode, ConnectionRole.Target),
                    await provider.CapabilityDetector.ProbeAsync(
                        new ConnectionProbeRequest(profile, ConnectionRole.Target, mode, connectionString),
                        cancellationToken
                    )
                );
                targetMissing = asTarget
                    .MissingRequired.Select(capability => capability.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (asTarget.CleanupFailureCode is not null)
                    notes.Add("As a target, the staging probe left an object behind: " + asTarget.CleanupFailureCode);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                notes.Add(
                    "Target-role probe failed: " + Redact(exception.GetBaseException().Message, connectionString)
                );
            }
            return new ConnectionTestResponse(
                usable,
                assessment.State.ToString(),
                evidence.DatabaseIdentity,
                evidence.ProviderVersion,
                evidence.Available.Select(capability => capability.ToString()).Order(StringComparer.Ordinal).ToArray(),
                assessment
                    .MissingRequired.Select(capability => capability.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                usable ? null
                    : evidence.CleanupFailureCode is not null
                        ? "The database was reached but a staging object created by the probe could not be removed."
                    : $"The database was reached but required capabilities are missing (probed schema '{profile.BusinessSchema}').",
                notes,
                assessment
                    .MissingOptional.Select(capability => capability.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                targetMissing
            );
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResponse(
                false,
                ConnectionHealthState.Unhealthy.ToString(),
                null,
                null,
                [],
                requirements
                    .Required.Select(capability => capability.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                Redact(exception.GetBaseException().Message, connectionString),
                ConnectionFailureHints.Explain(exception.GetBaseException().Message) is { } hint ? [hint] : []
            );
        }
    }

    /// <summary>Drops any secret-looking connection-string values from a driver message before it leaves the API.</summary>
    private static string Redact(string message, string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
                continue;
            var key = part[..separator].Trim().ToLowerInvariant();
            var value = part[(separator + 1)..].Trim().Trim('"', '\'');
            if (value.Length >= 3 && (key.Contains("password") || key.Contains("pwd") || key.Contains("secret")))
                message = message.Replace(value, "•••", StringComparison.Ordinal);
        }
        return message;
    }

    public async Task DeleteConnectionAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken)
    {
        var profile = await connections.GetProfileAsync(connectionId, cancellationToken);
        await connections.DeleteAsync(connectionId, ifMatch, cancellationToken);
        if (secretWriter is not null)
            await secretWriter.RemoveAsync(profile.SecretReference, cancellationToken);
    }

    public async Task<OperationReceiptResponse> QueueConnectionCheckAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        await health.TestAsync(connectionId, TransferMode.ResumableStaged, ConnectionRole.Source, cancellationToken);
        return Receipt(connectionId: connectionId);
    }

    public async Task<OperationReceiptResponse> QueueSchemaScanAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        var scan = await snapshots.QueueAsync(connectionId, Guid.NewGuid().ToString(), cancellationToken);
        return Receipt(scan.ScanId, connectionId: scan.ConnectionId, state: "queued");
    }

    public async Task<OperationStatusResponse?> GetOperationStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        var scan = await snapshots.FindScanAsync(operationId, cancellationToken);
        if (scan is not null)
            return new(
                scan.ScanId,
                "schema-scan",
                scan.State.ToString(),
                scan.State is SchemaScanState.Completed or SchemaScanState.Failed,
                scan.State is SchemaScanState.Failed,
                scan.FailureCode,
                scan.ConnectionId,
                scan.SnapshotId,
                null,
                null,
                scan.FailureDetail
            );

        var job = jobs.Find(operationId);
        return job is null
            ? null
            : new(
                job.JobId,
                "job",
                job.State.ToString(),
                job.State is JobState.Cancelled or JobState.Succeeded or JobState.Failed or JobState.VerificationFailed,
                job.State is JobState.Failed or JobState.VerificationFailed,
                job.FailureCode,
                null,
                null,
                job.PlanId,
                job.JobId
            );
    }

    public async Task<IReadOnlyList<SchemaSnapshotSummaryResponse>> ListSnapshotsAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    ) =>
        (await snapshots.ListAsync(connectionId, cancellationToken))
            .Select(snapshot => new SchemaSnapshotSummaryResponse(
                snapshot.SnapshotId,
                snapshot.Hash,
                snapshot.CapturedAtUtc
            ))
            .ToArray();

    public async Task DeleteSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken)
    {
        if (!await snapshots.DeleteAsync(connectionId, snapshotId, cancellationToken))
            throw new SnapshotNotFoundException();
    }

    public async Task<SchemaSnapshotResponse> GetSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await snapshots.GetAsync(connectionId, snapshotId, cancellationToken);
        return ToSnapshotResponse(snapshot);
    }

    public async Task<SchemaSnapshotResponse?> FindSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await snapshots.FindAsync(connectionId, snapshotId, cancellationToken);
        return snapshot is null ? null : ToSnapshotResponse(snapshot);
    }

    private static readonly JsonSerializerOptions StoredQueryOptions = new(JsonSerializerDefaults.Web);

    public async Task<SelectionResponse> SaveSelectionAsync(
        Guid selectionId,
        SaveSelectionRequest request,
        CancellationToken cancellationToken
    )
    {
        var existing = await selections.FindAsync(selectionId, cancellationToken);
        if (existing is null && request.Query is null)
            throw new ArgumentException("A query is required to create a selection.", nameof(request));
        if (request.Query is { } query)
            ValidateSelectionQuery(query);
        var displayName = request.DisplayName ?? existing?.DisplayName ?? "";
        if (existing is not null && request.Query is null && displayName == existing.DisplayName)
            return new SelectionResponse(existing.SelectionId, existing.Version, ETag(existing.Version));
        var record = request.Query is { } changed
            ? await selections.SaveAsync(
                selectionId,
                displayName,
                JsonSerializer.Serialize(changed),
                request.IfMatch,
                cancellationToken,
                changed.ConnectionId,
                changed.SnapshotId,
                changed.RootSchema,
                changed.RootTable,
                changed.StableKeyConstraintName,
                changed.StableKeyColumns
            )
            : await selections.SaveAsync(
                selectionId,
                displayName,
                existing!.QueryJson,
                request.IfMatch,
                cancellationToken,
                existing.ConnectionId,
                existing.SnapshotId,
                existing.RootSchema,
                existing.RootTable,
                existing.StableKeyConstraintName,
                existing.StableKeyColumns
            );
        return new SelectionResponse(record.SelectionId, record.Version, ETag(record.Version));
    }

    /// <summary>Mirrors the workbench save rule: a selection is only usable with a root table and stable key.</summary>
    private static void ValidateSelectionQuery(SelectionRequestBody query)
    {
        if (
            string.IsNullOrWhiteSpace(query.RootSchema)
            || string.IsNullOrWhiteSpace(query.RootTable)
            || string.IsNullOrWhiteSpace(query.StableKeyConstraintName)
            || query.StableKeyColumns is not { Count: > 0 }
            || query.StableKeyColumns.Any(string.IsNullOrWhiteSpace)
        )
            throw new ArgumentException("Selection root table and stable key must be specified.", nameof(query));
    }

    public async Task<SelectionDetailsResponse> GetSelectionDetailsAsync(
        Guid selectionId,
        CancellationToken cancellationToken
    )
    {
        var record =
            await selections.FindAsync(selectionId, cancellationToken) ?? throw new SelectionNotFoundException();
        SelectionRequestBody query;
        try
        {
            query =
                JsonSerializer.Deserialize<SelectionRequestBody>(record.QueryJson, StoredQueryOptions)
                ?? throw new JsonException("Stored query is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The stored selection query could not be read.", exception);
        }
        return new SelectionDetailsResponse(
            record.SelectionId,
            record.DisplayName,
            record.Version,
            ETag(record.Version),
            string.IsNullOrWhiteSpace(query.Mode) ? "raw" : query.Mode,
            query,
            record.ConnectionId,
            record.SnapshotId,
            record.RootSchema,
            record.RootTable,
            record.StableKeyConstraintName,
            record.StableKeyColumns,
            record.UpdatedUtc
        );
    }

    public Task DeleteSelectionAsync(Guid selectionId, string ifMatch, CancellationToken cancellationToken) =>
        selections.DeleteAsync(selectionId, ifMatch, cancellationToken);

    public async Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(
        Guid selectionId,
        CancellationToken cancellationToken
    )
    {
        _ =
            await selections.FindAsync(selectionId, cancellationToken)
            ?? throw new InvalidOperationException("Selection was not found.");
        return Receipt();
    }

    public async Task<PlanResponse> SavePlanAsync(
        Guid planId,
        SavePlanRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.SelectionId is Guid selectionId
            && await selections.FindAsync(selectionId, cancellationToken) is null
        )
            throw new ArgumentException("Selection was not found.", nameof(request));
        if (request.SourceConnectionId is Guid sourceConnectionId)
            _ = await connections.GetSummaryAsync(sourceConnectionId, cancellationToken);
        if (request.TargetConnectionId is Guid targetConnectionId)
            _ = await connections.GetSummaryAsync(targetConnectionId, cancellationToken);
        var existing = await plans.FindAsync(planId, cancellationToken);
        var displayName = request.DisplayName ?? existing?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(request));
        var operatorNote =
            request.OperatorNote is null ? existing?.OperatorNote
            : string.IsNullOrWhiteSpace(request.OperatorNote) ? null
            : request.OperatorNote;
        var merged = new PlanRecord(
            planId,
            displayName,
            operatorNote,
            existing?.Version ?? 0,
            existing?.CanonicalHash,
            existing?.UpdatedUtc ?? default,
            request.SelectionId ?? existing?.SelectionId,
            request.SourceConnectionId ?? existing?.SourceConnectionId,
            request.TargetConnectionId ?? existing?.TargetConnectionId
        );
        if (existing is not null && merged == existing)
            return ToPlanResponse(existing);
        var record = await plans.SaveAsync(
            planId,
            merged.DisplayName,
            merged.OperatorNote,
            request.IfMatch,
            cancellationToken,
            merged.SelectionId,
            merged.SourceConnectionId,
            merged.TargetConnectionId
        );
        return ToPlanResponse(record);
    }

    private static PlanResponse ToPlanResponse(PlanRecord record) =>
        new(record.PlanId, checked((int)record.Version), record.CanonicalHash, ETag(record.Version));

    public async Task<PlanDetailsResponse> GetPlanDetailsAsync(Guid planId, CancellationToken cancellationToken)
    {
        var record = await plans.FindAsync(planId, cancellationToken) ?? throw new PlanNotFoundException();
        return new PlanDetailsResponse(
            record.PlanId,
            record.DisplayName,
            record.OperatorNote,
            checked((int)record.Version),
            ETag(record.Version),
            record.CanonicalHash,
            record.CanonicalHash is not null,
            record.SelectionId,
            record.SourceConnectionId,
            record.TargetConnectionId,
            record.UpdatedUtc
        );
    }

    public async Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan =
            await plans.FindAsync(planId, cancellationToken)
            ?? throw new InvalidOperationException("Plan was not found.");
        if (plan.SelectionId is not null && plan.SourceConnectionId is not null && plan.TargetConnectionId is not null)
            try
            {
                await (sealing ?? throw new InvalidOperationException("Plan sealing is not configured.")).SealAsync(
                    planId,
                    cancellationToken
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The reason stays on the plan so the review shows it after the request has gone.
                await plans.RecordSealFailureAsync(
                    planId,
                    SealFailureCode(exception),
                    exception.Message,
                    cancellationToken
                );
                throw;
            }
        return Receipt(planId: planId);
    }

    /// <summary>A stable code per sealing refusal, so clients can tell "fix the graph" from "the database failed".</summary>
    internal static string SealFailureCode(Exception exception) =>
        exception switch
        {
            UnorderablePlanException => "unorderable_cycle",
            IncompleteGraphException => "incomplete_graph",
            SourceOrphansException => "source_orphans",
            UniqueKeyCollisionException => "unique_key_collision",
            PlanInUseException => "plan_in_use",
            InvalidOperationException or NotSupportedException => "seal_rejected",
            _ => "seal_failed",
        };

    public async Task<PlanReviewResponse> GetPlanReviewAsync(Guid planId, CancellationToken cancellationToken)
    {
        var record = await plans.FindAsync(planId, cancellationToken) ?? throw new PlanNotFoundException();
        var selection = record.SelectionId is Guid selectionId
            ? await selections.FindAsync(selectionId, cancellationToken)
                ?? throw new ArgumentException("Selection was not found.")
            : null;
        var source = record.SourceConnectionId is Guid sourceConnectionId
            ? ToConnectionResponse(await connections.GetSummaryAsync(sourceConnectionId, cancellationToken))
            : null;
        var target = record.TargetConnectionId is Guid targetConnectionId
            ? ToConnectionResponse(await connections.GetSummaryAsync(targetConnectionId, cancellationToken))
            : null;
        var content = await plans.LoadContentAsync(planId, cancellationToken);
        if (content is not null)
        {
            // A plan sealed by an older sealing algorithm cannot start; say so where the operator looks.
            var stale = content.IsSealedByCurrentVersion
                ? []
                : new[]
                {
                    new PlanReviewMessageResponse("plan_stale", new StalePlanException(content.SealingVersion).Message),
                };
            return new PlanReviewResponse(
                record.PlanId,
                checked((int)record.Version),
                record.CanonicalHash ?? "",
                new PlanReviewSealResponse(stale.Length == 0 ? "sealed" : "invalidated", stale),
                new PlanReviewTotalsResponse(
                    content.ManifestTotals.Included,
                    content.ManifestTotals.PlannedWrites,
                    content.ManifestTotals.Inserts,
                    content.ManifestTotals.Updates,
                    0
                ),
                [],
                content
                    .Tables.Select(
                        (table, index) =>
                            new PlanReviewTableResponse(
                                new PlanReviewAddressResponse(table.Mapping.Source.Schema, table.Mapping.Source.Name),
                                new PlanReviewAddressResponse(table.Mapping.Target.Schema, table.Mapping.Target.Name),
                                table.State.ToString(),
                                index,
                                table.Manifest.Included,
                                table.Manifest.PlannedWrites,
                                table.Manifest.Inserts,
                                table.Manifest.Updates,
                                0,
                                table
                                    .Mapping.Columns.Select(column => new PlanReviewColumnResponse(
                                        column.Source,
                                        column.Target
                                    ))
                                    .ToArray()
                            )
                    )
                    .ToArray(),
                content
                    .ConflictPolicies.Select(policy => new PlanReviewConflictResponse(
                        policy.Table.Schema + "." + policy.Table.Name,
                        policy.Policy.ToString(),
                        ""
                    ))
                    .ToArray(),
                content
                    .Tables.Where(table => table.BackfilledColumns.Count > 0)
                    .Select(table => new PlanReviewCycleResponse(
                        [table.Mapping.Source.Schema + "." + table.Mapping.Source.Name],
                        table.CycleStrategy == CycleStrategy.NullableForeignKeyTwoPhase
                            ? "NullableForeignKeyTwoPhase"
                            : "Ordered",
                        "Column(s) "
                            + string.Join(", ", table.BackfilledColumns)
                            + " are written NULL first and filled in after every table has been written, so the target's constraints stay enforced."
                    ))
                    .ToArray(),
                content
                    .Warnings.Select(warning => new PlanReviewMessageResponse(warning.Code, warning.Message))
                    .ToArray(),
                stale,
                selection is null
                    ? null
                    : new PlanReviewSelectionResponse(
                        selection.SelectionId,
                        selection.DisplayName,
                        selection.ConnectionId,
                        selection.SnapshotId
                    ),
                source,
                target
            );
        }
        var notSealed = new PlanReviewMessageResponse("plan_not_sealed", "This plan has not completed sealing.");
        var reasons = new List<PlanReviewMessageResponse> { notSealed };
        if (record.SealFailureCode is not null)
            reasons.Add(new PlanReviewMessageResponse(record.SealFailureCode, record.SealFailureDetail ?? ""));
        return new PlanReviewResponse(
            record.PlanId,
            checked((int)record.Version),
            record.CanonicalHash ?? "",
            new PlanReviewSealResponse("invalidated", reasons),
            new PlanReviewTotalsResponse(0, 0, 0, 0, 0),
            [],
            [],
            [],
            [],
            [],
            reasons,
            selection is null
                ? null
                : new PlanReviewSelectionResponse(
                    selection.SelectionId,
                    selection.DisplayName,
                    selection.ConnectionId,
                    selection.SnapshotId
                ),
            source,
            target
        );
    }

    public Task<InclusionPathResponse> GetPlanInclusionPathAsync(
        Guid planId,
        InclusionPathRequest request,
        CancellationToken cancellationToken
    ) =>
        throw new InvalidOperationException(
            "Inclusion-path lookup requires a sealed transfer plan with a computed dependency closure, which is not yet wired."
        );

    public async Task<OperationReceiptResponse> StartJobAsync(
        Guid planId,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        _ = await plans.FindAsync(planId, cancellationToken) ?? throw new PlanNotFoundException();
        var content = await plans.LoadContentAsync(planId, cancellationToken) ?? throw new PlanNotSealedException();
        if (!content.IsSealedByCurrentVersion)
            throw new StalePlanException(content.SealingVersion);
        var result = jobs.Start(new StartJobRequest(planId, idempotencyKey));
        return Receipt(result.Job.JobId, planId: planId, jobId: result.Job.JobId, state: "queued");
    }

    public async Task<IReadOnlyList<JobSummaryResponse>> ListJobsAsync(CancellationToken cancellationToken)
    {
        var jobsToList = jobs.List(cancellationToken);
        return await Task.WhenAll(
            jobsToList.Select(async job =>
            {
                var current = await GetJobAsync(job.JobId, cancellationToken);
                return new JobSummaryResponse(
                    job.JobId,
                    job.PlanId,
                    job.State.ToString(),
                    job.CreatedUtc,
                    job.UpdatedUtc,
                    current.RowsTransferred,
                    current.BytesTransferred,
                    job.FailureCode,
                    job.FailureDetail
                );
            })
        );
    }

    public async Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = jobs.Get(jobId);
        var page = await jobEvents.ReadAfterAsync(jobId, null, cancellationToken);
        var latest = page.Events.Count == 0 ? null : page.Events[^1];
        return new JobResponse(
            job.JobId,
            job.PlanId,
            job.State.ToString(),
            latest?.Payload.RowsTransferred ?? 0,
            latest?.Payload.BytesTransferred ?? 0,
            job.FailureCode,
            job.FailureDetail
        );
    }

    public async Task<OperationReceiptResponse> QueueJobCommandAsync(
        Guid jobId,
        JobCommand command,
        CancellationToken cancellationToken
    )
    {
        await (
            command switch
            {
                JobCommand.Pause => jobs.RequestPauseAsync(jobId, cancellationToken),
                JobCommand.Resume => jobs.RequestResumeAsync(jobId, cancellationToken),
                JobCommand.Cancel => jobs.RequestCancelAsync(jobId, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            }
        );
        return Receipt(jobId, jobId: jobId);
    }

    private static ConnectionResponse ToConnectionResponse(ConnectionProfileSummary summary) =>
        new(summary.ConnectionId, summary.DisplayName, summary.ProviderId, summary.Health.ToString(), summary.ETag);

    private static SchemaSnapshotResponse ToSnapshotResponse(StoredSchemaSnapshot snapshot) =>
        new(snapshot.ConnectionId, snapshot.SnapshotId, snapshot.Hash, snapshot.CapturedAtUtc)
        {
            Tables = snapshot
                .Content.Tables.OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => new SchemaSnapshotTableResponse(
                    table.Schema,
                    table.Name,
                    table
                        .Columns.Select(column => new SchemaSnapshotColumnResponse(
                            column.Name,
                            column.StoreType,
                            column.IsNullable
                        ))
                        .ToArray(),
                    table.PrimaryKey is null
                        ? null
                        : new SchemaSnapshotKeyResponse(table.PrimaryKey.Name, table.PrimaryKey.Columns.ToArray())
                ))
                .ToArray(),
            ForeignKeys = snapshot
                .Content.ForeignKeys.OrderBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
                .ThenBy(foreignKey => foreignKey.ChildTable.Schema, StringComparer.Ordinal)
                .ThenBy(foreignKey => foreignKey.ChildTable.Name, StringComparer.Ordinal)
                .Select(foreignKey => new SchemaSnapshotForeignKeyResponse(
                    foreignKey.Name,
                    new SchemaSnapshotAddressResponse(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name),
                    new SchemaSnapshotAddressResponse(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name),
                    foreignKey.ChildColumns.ToArray(),
                    foreignKey.ParentColumns.ToArray(),
                    foreignKey.IsEnforced,
                    foreignKey.IsTrusted
                ))
                .ToArray(),
        };

    private static OperationReceiptResponse Receipt(
        Guid? operationId = null,
        Guid? connectionId = null,
        Guid? planId = null,
        Guid? jobId = null,
        string state = "unknown"
    )
    {
        var id = operationId ?? Guid.NewGuid();
        return new(id, state, new Uri("https://datapitcher.local/api/operations/" + id), connectionId, planId, jobId);
    }

    private static string ETag(long version) => $"\"{version}\"";
}
