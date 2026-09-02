using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication();
builder.Services.AddApiAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymousWithJustification("Liveness must be reachable before authentication infrastructure is confirmed healthy.");

app.MapGet("/api/providers", () => Results.Ok<IReadOnlyList<ProviderResponse>>([
        new ProviderResponse("sqlserver", "SQL Server"),
        new ProviderResponse("postgresql", "PostgreSQL"),
    ]))
    .AllowAnonymousWithJustification("Provider identifiers and display names are non-sensitive and needed before sign-in to build the connection form.");

EndpointGroups.Map(app);

app.Run();

public partial class Program;
