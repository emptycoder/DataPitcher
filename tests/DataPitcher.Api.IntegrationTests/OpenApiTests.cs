using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class OpenApiTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task OpenApi_WhenUnauthenticated_Returns401ProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Add("X-Test-Unauthenticated", "true");
        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unauthenticated", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpenApi_DeclaresBearerSecurityForEveryProtectedOperation()
    {
        using var document = await GetDocumentAsync();

        Assert.True(
            document
                .RootElement.GetProperty("components")
                .GetProperty("securitySchemes")
                .TryGetProperty("Bearer", out var bearer)
        );
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(property => IsHttpMethod(property.Name)))
            {
                if (path.Name is "/health/live" or "/api/providers")
                {
                    Assert.True(
                        operation.Value.TryGetProperty("x-datapitcher-anonymous-justification", out var reason)
                    );
                    Assert.False(string.IsNullOrWhiteSpace(reason.GetString()));
                    Assert.False(operation.Value.TryGetProperty("security", out _));
                    continue;
                }

                Assert.True(operation.Value.TryGetProperty("security", out var security));
                Assert.Contains(
                    security.EnumerateArray().ToArray(),
                    requirement => requirement.TryGetProperty("Bearer", out _)
                );
            }
        }
    }

    [Fact]
    public async Task OpenApi_DeclaresProblemDetailsForEveryErrorResponse()
    {
        using var document = await GetDocumentAsync();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(property => IsHttpMethod(property.Name)))
            {
                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject())
                {
                    if (
                        !int.TryParse(response.Name, out var statusCode)
                        || statusCode < StatusCodes.Status400BadRequest
                    )
                        continue;
                    Assert.True(
                        response.Value.GetProperty("content").TryGetProperty("application/problem+json", out _)
                    );
                }
            }
        }
    }

    [Fact]
    public async Task OpenApi_DescribesTheSseHeaderAndContentType()
    {
        using var document = await GetDocumentAsync();
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/api/jobs/{jobId}/events")
            .GetProperty("get");

        Assert.True(
            operation
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .TryGetProperty("text/event-stream", out _)
        );
        Assert.Contains(
            operation.GetProperty("parameters").EnumerateArray().ToArray(),
            parameter =>
                parameter.GetProperty("name").GetString() == "Last-Event-ID"
                && parameter.GetProperty("in").GetString() == "header"
        );
    }

    [Fact]
    public async Task OpenApiDocumentAndRepresentativeErrorContainNoCredentialContent()
    {
        _factory.Application.Delay = _ =>
            Task.FromException(new InvalidOperationException(string.Join(' ', ForbiddenSentinels)));
        try
        {
            using var document = await GetDocumentAsync();
            using var response = await _client.GetAsync("/api/connections", CancellationToken.None);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var values = Strings(document.RootElement).Append(await response.Content.ReadAsStringAsync());

            foreach (var sentinel in ForbiddenSentinels)
                Assert.DoesNotContain(values, value => value.Contains(sentinel, StringComparison.Ordinal));
        }
        finally
        {
            _factory.Application.Delay = null;
        }
    }

    private async Task<JsonDocument> GetDocumentAsync()
    {
        using var response = await _client.GetAsync("/openapi/v1.json", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static bool IsHttpMethod(string value) =>
        value is "get" or "put" or "post" or "delete" or "patch" or "head" or "options";

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                foreach (var value in Strings(item))
                    yield return value;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in Strings(property.Value))
                        yield return value;
                }
                break;
        }
    }

    private static readonly string[] ForbiddenSentinels =
    [
        "password-redaction-sentinel",
        "token-redaction-sentinel",
        "client-secret-redaction-sentinel",
        "Server=redaction-sentinel;Password=redaction-sentinel",
        "secret-reference-redaction-sentinel",
    ];
}
