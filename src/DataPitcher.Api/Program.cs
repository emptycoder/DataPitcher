using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Endpoints;
using DataPitcher.Api.Errors;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication();
builder.Services.AddApiAuthorization();

var app = builder.Build();

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

app.Run();

public partial class Program;
