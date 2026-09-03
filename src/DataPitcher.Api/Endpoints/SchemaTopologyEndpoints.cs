using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Core.Authorization;
using DataPitcher.Core.Graph;
using DataPitcher.Core.Schema;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Schema;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DataPitcher.Api.Endpoints;

/// <summary>
/// Real schema topology built from the source schema snapshot sealed in the requested plan via the existing tested
/// <see cref="DependencyGraph"/>/<see cref="CondensedGraph"/> types. <c>plannedTableIds</c> remains empty and table
/// state reports only what the schema graph itself proves (cycle membership), not plan selection membership.
/// </summary>
public static class SchemaTopologyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var plans = app.MapGroup("/api/plans");
        plans
            .MapGet("/{planId:guid}/schema-dependency-graph", GetSchemaDependencyGraphAsync)
            .RequireAuthorization(ApiPolicyNames.PlansRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<
        Results<Ok<PlanSchemaDependencyGraphResponse>, ProblemHttpResult>
    > GetSchemaDependencyGraphAsync(
        Guid planId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        PlanStore plans,
        SchemaSnapshotStore snapshots,
        CancellationToken cancellationToken
    )
    {
        var authorization = await authorizationService.AuthorizeAsync(
            user,
            new PlanResource(planId),
            new ResourcePermissionRequirement(Permissions.PlansRead)
        );
        if (!authorization.Succeeded)
            return ApiAuthorizationResults.Forbidden(context, new PlanResource(planId));

        var plan = await plans.LoadContentAsync(planId, cancellationToken);
        if (plan is null)
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Schema snapshot not found.");
        var snapshot = await snapshots.FindByHashAsync(
            plan.Source.ConnectionId,
            plan.SourceSchema.Hash,
            cancellationToken
        );
        if (snapshot is null)
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Schema snapshot not found.");

        var tables = snapshot
            .Content.Tables.Select(table => new TableDefinition(table.Schema, table.Name, [], null, []))
            .ToArray();
        var foreignKeys = snapshot
            .Content.ForeignKeys.Select(foreignKey => new ForeignKeyDefinition(
                foreignKey.Name,
                new TableDefinition(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name, [], null, []),
                new TableDefinition(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name, [], null, []),
                foreignKey.ChildColumns,
                foreignKey.ParentColumns,
                foreignKey.IsEnforced,
                foreignKey.IsTrusted
            ))
            .ToArray();

        var graph = new DependencyGraph(tables, foreignKeys);
        var condensed = new CondensedGraph(graph);
        var componentByTable = condensed
            .Components.SelectMany(component => component.Tables.Select(table => (table, component)))
            .ToDictionary(pair => pair.table, pair => pair.component);

        var tableResponses = graph
            .Tables.Select(table =>
            {
                var component = componentByTable[table];
                var state = component.Tables.Count > 1 ? "cycle-member" : "unselected";
                return new PlanSchemaDependencyGraphTableResponse(
                    Id(table),
                    table.Schema,
                    table.Name,
                    "component-" + component.Id,
                    state
                );
            })
            .ToArray();

        var relationshipResponses = foreignKeys
            .Select(foreignKey => new PlanSchemaDependencyGraphRelationshipResponse(
                foreignKey.Name,
                foreignKey.Name,
                Id(foreignKey.ChildTable),
                Id(foreignKey.ParentTable)
            ))
            .OrderBy(relationship => relationship.Id, StringComparer.Ordinal)
            .ToArray();

        return TypedResults.Ok(
            new PlanSchemaDependencyGraphResponse(snapshot.Hash, [], tableResponses, relationshipResponses)
        );
    }

    private static string Id(TableDefinition table) => table.Schema + "." + table.Name;
}
