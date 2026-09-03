using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Application.Schema;
using DataPitcher.ControlStore;
using DataPitcher.Core.Authorization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DataPitcher.Api.Endpoints;

/// <summary>
/// The six selection-workbench routes the frontend calls. Schema browsing, listing, and saving are real, backed by
/// the latest captured schema snapshot and <see cref="ISelectionRepository"/>. Raw-SQL compilation is real, delegating to
/// the existing <see cref="RawSqlSafetyValidator"/>. Visual-mode compilation and live preview/count execution
/// require an AST-to-domain mapper and a per-request provider execution context that do not exist in this codebase
/// yet (see <see cref="SelectionExecutionNotWiredException"/>).
/// </summary>
public static class WorkbenchEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var selections = app.MapGroup("/api/selections");
        WithStandardProblems(selections.MapGet("/workbench-schema", GetWorkbenchSchemaAsync))
            .RequireAuthorization(ApiPolicyNames.SelectionsRead);
        WithStandardProblems(selections.MapPost("/compile", CompileAsync))
            .RequireAuthorization(ApiPolicyNames.SelectionsRead);
        WithStandardProblems(selections.MapPost("/preview", PreviewAsync))
            .RequireAuthorization(ApiPolicyNames.SelectionsRead);
        WithStandardProblems(selections.MapPost("/count", CountAsync))
            .RequireAuthorization(ApiPolicyNames.SelectionsRead);
        WithStandardProblems(selections.MapPost("/save", SaveAsync))
            .RequireAuthorization(ApiPolicyNames.SelectionsWrite);
        WithStandardProblems(selections.MapGet("", ListAsync)).RequireAuthorization(ApiPolicyNames.SelectionsRead);
    }

    private static RouteHandlerBuilder WithStandardProblems(RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

    private static async Task<Ok<SelectionWorkbenchSchemaResponse>> GetWorkbenchSchemaAsync(
        ISchemaSnapshotRepository snapshots,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await snapshots.GetLatestAsync(cancellationToken);
        if (snapshot is null)
            return TypedResults.Ok(new SelectionWorkbenchSchemaResponse([], [], ""));

        var tables = snapshot
            .Content.Tables.Select(table => new SelectionTableResponse(
                TableId(table.Schema, table.Name),
                table.Schema,
                table.Name,
                null,
                table.PrimaryKey?.Columns.ToArray(),
                table
                    .Columns.Select(column => new SelectionColumnResponse(column.Name, ValueKindOf(column.ClrType)))
                    .ToArray()
            ))
            .ToArray();

        var foreignKeys = snapshot
            .Content.ForeignKeys.Select(foreignKey => new ForeignKeyPathResponse(
                foreignKey.Name,
                TableId(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name),
                TableId(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name)
            ))
            .ToArray();

        return TypedResults.Ok(new SelectionWorkbenchSchemaResponse(tables, foreignKeys, snapshot.Hash));
    }

    private static Task<Ok<CompilationResponse>> CompileAsync(SelectionRequestBody request)
    {
        if (!string.Equals(request.Mode, "raw", StringComparison.Ordinal) || request.RawSql is null)
            throw new SelectionExecutionNotWiredException();

        RawSqlSafetyValidator.Validate(RawSqlDialect.PostgreSql, request.RawSql);
        var parameters = request
            .Parameters.Select(parameter => new TypedParameterDefinitionResponse(parameter.Name, parameter.Kind))
            .ToArray();
        return Task.FromResult(
            TypedResults.Ok(new CompilationResponse(request.RawSql, parameters, [], request.SchemaRevision))
        );
    }

    private static Task<PreviewResponse> PreviewAsync(SelectionRequestBody request) =>
        throw new SelectionExecutionNotWiredException();

    private static Task<CountResponse> CountAsync(SelectionRequestBody request) =>
        throw new SelectionExecutionNotWiredException();

    private static async Task<Results<Ok<SavedSelectionResponse>, ProblemHttpResult>> SaveAsync(
        SelectionRequestBody request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        ISelectionRepository selections,
        CancellationToken cancellationToken
    )
    {
        if (
            request.ConnectionId is Guid connectionId
            && await EndpointGroups.AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.ConnectionsRead
            )
                is { } problem
        )
            return problem;
        if (
            string.IsNullOrWhiteSpace(request.RootSchema)
            || string.IsNullOrWhiteSpace(request.RootTable)
            || string.IsNullOrWhiteSpace(request.StableKeyConstraintName)
            || request.StableKeyColumns is not { Count: > 0 }
            || request.StableKeyColumns.Any(string.IsNullOrWhiteSpace)
        )
            throw new ArgumentException("Selection root table and stable key must be specified.", nameof(request));
        var selectionId = Guid.NewGuid();
        var record = await selections.SaveAsync(
            selectionId,
            "",
            System.Text.Json.JsonSerializer.Serialize(request),
            "\"0\"",
            cancellationToken,
            request.ConnectionId,
            request.SnapshotId,
            request.RootSchema,
            request.RootTable,
            request.StableKeyConstraintName,
            request.StableKeyColumns
        );
        return TypedResults.Ok(
            new SavedSelectionResponse(
                record.SelectionId,
                record.DisplayName,
                record.Version,
                ETag(record.Version),
                request.Mode,
                []
            )
        );
    }

    private static async Task<Ok<ListSelectionsResponse>> ListAsync(
        ISelectionRepository selections,
        CancellationToken cancellationToken
    )
    {
        var records = await selections.ListAsync(cancellationToken);
        var saved = records
            .Select(record => new SavedSelectionResponse(
                record.SelectionId,
                record.DisplayName,
                record.Version,
                ETag(record.Version),
                Mode(record.QueryJson),
                []
            ))
            .ToArray();
        return TypedResults.Ok(new ListSelectionsResponse(saved));
    }

    private static string Mode(string queryJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(queryJson);
        return document.RootElement.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "raw" : "raw";
    }

    private static string TableId(string schema, string name) => schema + "." + name;

    private static string ETag(long version) => $"\"{version}\"";

    private static string ValueKindOf(string clrType) =>
        clrType switch
        {
            "System.Int16" or "System.Int32" or "System.Int64" or "System.Byte" or "System.SByte" => "int",
            "System.Decimal" or "System.Double" or "System.Single" => "decimal",
            "System.Boolean" => "boolean",
            "System.DateOnly" => "date",
            "System.TimeOnly" => "time",
            "System.DateTime" or "System.DateTimeOffset" => "dateTime",
            "System.Guid" => "guid",
            _ => "string",
        };
}
