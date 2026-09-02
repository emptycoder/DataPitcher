using DataPitcher.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
EndpointGroups.Map(app);

app.Run();

public partial class Program;
