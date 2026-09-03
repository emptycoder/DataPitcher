using System.Security.Claims;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Errors;
using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Core.Authorization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DataPitcher.Api.Authorization;

public sealed record AnonymousAccessJustificationMetadata(string Reason);

public static class ApiPolicyNames
{
    public const string ConnectionsRead = "permission:Connections.Read";
    public const string ConnectionsWrite = "permission:Connections.Write";
    public const string SchemaRead = "permission:Schema.Read";
    public const string SchemaWrite = "permission:Schema.Write";
    public const string SelectionsRead = "permission:Selections.Read";
    public const string SelectionsWrite = "permission:Selections.Write";
    public const string SelectionsRawSql = "permission:Selections.RawSql";
    public const string PlansRead = "permission:Plans.Read";
    public const string PlansWrite = "permission:Plans.Write";
    public const string PlansSeal = "permission:Plans.Seal";
    public const string TransfersRead = "permission:Transfers.Read";
    public const string TransfersWrite = "permission:Transfers.Write";
    public const string TransfersStart = "permission:Transfers.Start";
}

public static class ApiClaimTypes
{
    public const string Permission = "permission";
}

public interface IValidatedAccessTokenLifetime
{
    DateTimeOffset GetExpiryUtc(ClaimsPrincipal principal);
}

public abstract record ApiResource;

public sealed record ConnectionResource(Guid ConnectionId) : ApiResource;

public sealed record PlanResource(Guid PlanId) : ApiResource;

public sealed record JobResource(Guid JobId) : ApiResource;

public interface IResourceAccessGrantReader
{
    Task<bool> IsGrantedAsync(ClaimsPrincipal principal, ApiResource resource, CancellationToken cancellationToken);
}

public sealed record PermissionRequirement(Permission Permission) : IAuthorizationRequirement;

public sealed record ResourcePermissionRequirement(Permission Permission) : IAuthorizationRequirement;

public static class AuthorizationFailureDiagnostics
{
    public const string IndeterminateReason = "authorization_indeterminate";

    public static bool IsIndeterminate(AuthorizationFailure? failure) =>
        failure?.FailureReasons.Any(reason =>
            string.Equals(reason.Message, IndeterminateReason, StringComparison.Ordinal)
        ) == true;
}

public sealed class PermissionAuthorizationHandler(IPermissionDecisionResolver resolver)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement
    )
    {
        var decision = resolver.Resolve(context.User, requirement.Permission);
        if (decision.Outcome == AuthorizationOutcome.Granted)
            context.Succeed(requirement);
        else if (decision.Outcome == AuthorizationOutcome.Indeterminate)
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureDiagnostics.IndeterminateReason));
        return Task.CompletedTask;
    }
}

public sealed class ResourceAuthorizationHandler(
    IResourceAccessGrantReader grants,
    IPermissionDecisionResolver? resolver = null
) : AuthorizationHandler<ResourcePermissionRequirement, ApiResource>
{
    private readonly IPermissionDecisionResolver _resolver = resolver ?? new ClaimsPermissionDecisionResolver();

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourcePermissionRequirement requirement,
        ApiResource resource
    )
    {
        var decision = _resolver.Resolve(context.User, requirement.Permission);
        if (decision.Outcome == AuthorizationOutcome.Indeterminate)
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureDiagnostics.IndeterminateReason));
            return;
        }
        if (decision.Outcome != AuthorizationOutcome.Granted)
            return;
        if (await grants.IsGrantedAsync(context.User, resource, CancellationToken.None))
            context.Succeed(requirement);
    }
}

public static class ApiAuthorizationResults
{
    public static ProblemHttpResult Unauthenticated(HttpContext context) =>
        ApiProblemMapper.Result(new(ApiErrorClass.Unauthenticated, new(null, null, null, null, null)), context);

    public static ProblemHttpResult Forbidden(HttpContext context, ApiResource? resource = null) =>
        ApiProblemMapper.Result(new(ApiErrorClass.Forbidden, Resources(resource)), context);

    public static ProblemHttpResult Indeterminate(HttpContext context, ApiResource? resource = null) =>
        ApiProblemMapper.Result(new(ApiErrorClass.GroupResolutionFailed, Resources(resource)), context);

    private static ResourceIdentifiers Resources(ApiResource? resource) =>
        resource switch
        {
            ConnectionResource connection => new(connection.ConnectionId, null, null, null, null),
            PlanResource plan => new(null, null, null, plan.PlanId, null),
            JobResource job => new(null, null, null, null, job.JobId),
            _ => new(null, null, null, null, null),
        };
}

public sealed class ApiAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    )
    {
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }
        if (authorizeResult.Challenged)
        {
            await ApiAuthorizationResults.Unauthenticated(context).ExecuteAsync(context);
            return;
        }
        if (AuthorizationFailureDiagnostics.IsIndeterminate(authorizeResult.AuthorizationFailure))
        {
            await ApiAuthorizationResults.Indeterminate(context).ExecuteAsync(context);
            return;
        }
        await ApiAuthorizationResults.Forbidden(context).ExecuteAsync(context);
    }
}

public static class AnonymousEndpointConventionBuilderExtensions
{
    public static TBuilder AllowAnonymousWithJustification<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        builder.AllowAnonymous();
        builder.WithMetadata(new AnonymousAccessJustificationMetadata(reason));
        return builder;
    }
}

public static class ApiAuthorizationSetup
{
    private static readonly (string Name, Permission Permission)[] PolicyMap =
    [
        (ApiPolicyNames.ConnectionsRead, Permissions.ConnectionsRead),
        (ApiPolicyNames.ConnectionsWrite, Permissions.ConnectionsWrite),
        (ApiPolicyNames.SchemaRead, Permissions.SchemaRead),
        (ApiPolicyNames.SchemaWrite, Permissions.SchemaWrite),
        (ApiPolicyNames.SelectionsRead, Permissions.SelectionsRead),
        (ApiPolicyNames.SelectionsWrite, Permissions.SelectionsWrite),
        (ApiPolicyNames.SelectionsRawSql, Permissions.SelectionsRawSql),
        (ApiPolicyNames.PlansRead, Permissions.PlansRead),
        (ApiPolicyNames.PlansWrite, Permissions.PlansWrite),
        (ApiPolicyNames.PlansSeal, Permissions.PlansSeal),
        (ApiPolicyNames.TransfersRead, Permissions.TransfersRead),
        (ApiPolicyNames.TransfersWrite, Permissions.TransfersWrite),
        (ApiPolicyNames.TransfersStart, Permissions.TransfersStart),
    ];

    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionDecisionResolver, ClaimsPermissionDecisionResolver>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, ResourceAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationMiddlewareResultHandler>();

        var builder = services
            .AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var (name, permission) in PolicyMap)
        {
            builder.AddPolicy(name, policy => policy.Requirements.Add(new PermissionRequirement(permission)));
        }
        return services;
    }
}
