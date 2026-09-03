using DataPitcher.Api.Authorization;
using DataPitcher.Api.Composition;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Endpoints;
using DataPitcher.Api.Errors;
using DataPitcher.Auth.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataPitcherAuthenticationProviders(builder.Configuration, builder.Environment);
builder.Services.AddApiAuthorization();
builder.Services.AddDevelopmentResourceAccessGrantReader(builder.Configuration);
builder.Services.AddSingleton<IValidatedAccessTokenLifetime, DevelopmentValidatedAccessTokenLifetime>();
builder.Services.AddDataPitcherComposition(builder.Configuration);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components!.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata!;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            if (metadata.OfType<AnonymousAccessJustificationMetadata>().FirstOrDefault() is { } justification)
            {
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions["x-datapitcher-anonymous-justification"] = new JsonNodeExtension(JsonValue.Create(justification.Reason)!);
            }
        }
        else if (metadata.OfType<IAuthorizeData>().Any())
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
            });
        }
        return Task.CompletedTask;
    });
});

var app = builder.Build();

app.Services.ApplyControlDatabaseMigrations();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .AllowAnonymousWithJustification("Liveness must be reachable before authentication infrastructure is confirmed healthy.");

app.MapGet("/api/providers", () => Results.Ok<IReadOnlyList<ProviderResponse>>([
        new ProviderResponse("sqlserver", "SQL Server"),
        new ProviderResponse("postgresql", "PostgreSQL"),
    ]))
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .AllowAnonymousWithJustification("Provider identifiers and display names are non-sensitive and needed before sign-in to build the connection form.");

EndpointGroups.Map(app);
SchemaTopologyEndpoints.Map(app);
WorkbenchEndpoints.Map(app);

app.MapOpenApi()
    .RequireAuthorization(ApiPolicyNames.ConnectionsRead)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status500InternalServerError);

app.Run();

public partial class Program;
