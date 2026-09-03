using System.Net;
using System.Net.Http.Json;
using DataPitcher.Api.Contracts;
using DataPitcher.Infrastructure.Selections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class WorkbenchEndpointsTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SaveSelection_WithConnectionAndSnapshot_PersistsTheBinding()
    {
        var connectionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var request = new
        {
            mode = "raw",
            visual = (object?)null,
            rawSql = "SELECT 1",
            parameters = Array.Empty<object>(),
            schemaRevision = "schema-revision",
            connectionId,
            snapshotId,
        };

        using var response = await _client.PostAsJsonAsync("/api/selections/save", request, CancellationToken.None);
        var saved = await response.Content.ReadFromJsonAsync<SavedSelectionResponse>();
        var selections = _factory.Services.GetRequiredService<SelectionStore>();
        var selection = await selections.FindAsync(saved!.SelectionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(connectionId, selection!.ConnectionId);
        Assert.Equal(snapshotId, selection.SnapshotId);
    }

    [Fact]
    public async Task SaveSelection_WhenConnectionAccessIsDenied_Returns403()
    {
        var connectionId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/selections/save")
        {
            Content = JsonContent.Create(
                new
                {
                    mode = "raw",
                    visual = (object?)null,
                    rawSql = "SELECT 1",
                    parameters = Array.Empty<object>(),
                    schemaRevision = "schema-revision",
                    connectionId,
                }
            ),
        };
        request.Headers.Add("X-Test-Denied-Resource", connectionId.ToString());

        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SaveSelection_WithoutConnection_SavesNormally()
    {
        var request = new
        {
            mode = "raw",
            visual = (object?)null,
            rawSql = "SELECT 1",
            parameters = Array.Empty<object>(),
            schemaRevision = "schema-revision",
        };

        using var response = await _client.PostAsJsonAsync("/api/selections/save", request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
