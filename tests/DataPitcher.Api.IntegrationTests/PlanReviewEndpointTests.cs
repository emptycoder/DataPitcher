using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class PlanReviewEndpointTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetPlanReview_ReturnsExistingReviewState()
    {
        var planId = Guid.NewGuid();

        using var response = await _client.GetAsync($"/api/plans/{planId}/review", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var review = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(planId, review.RootElement.GetProperty("planId").GetGuid());
        Assert.Equal("sealed", review.RootElement.GetProperty("seal").GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostInclusionPath_ReturnsExistingPath()
    {
        var planId = Guid.NewGuid();

        using var response = await _client.PostAsJsonAsync(
            $"/api/plans/{planId}/inclusion-paths",
            new { table = "sales.Orders", stableKey = "Id=42" },
            CancellationToken.None
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var path = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sales.Orders", path.RootElement.GetProperty("table").GetString());
        Assert.Equal("Id=42", path.RootElement.GetProperty("stableKey").GetString());
    }

    [Fact]
    public async Task GetPlanReview_WhenPlanAccessIsDenied_Returns403WithoutReadingState()
    {
        var planId = Guid.NewGuid();
        var invocations = _factory.Application.Invocations.Count;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/plans/{planId}/review");
        request.Headers.Add("X-Test-Denied-Resource", planId.ToString());

        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(invocations, _factory.Application.Invocations.Count);
    }

    [Fact]
    public async Task PostInclusionPath_WhenPlanAccessIsDenied_Returns403WithoutReadingState()
    {
        var planId = Guid.NewGuid();
        var invocations = _factory.Application.Invocations.Count;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{planId}/inclusion-paths")
        {
            Content = JsonContent.Create(new { table = "sales.Orders", stableKey = "Id=42" }),
        };
        request.Headers.Add("X-Test-Denied-Resource", planId.ToString());

        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(invocations, _factory.Application.Invocations.Count);
    }

    [Fact]
    public async Task PostInclusionPath_WhenTheStableKeyIsBlank_Returns400WithoutReadingState()
    {
        var invocations = _factory.Application.Invocations.Count;

        using var response = await _client.PostAsJsonAsync(
            $"/api/plans/{Guid.NewGuid()}/inclusion-paths",
            new { table = "sales.Orders", stableKey = " " },
            CancellationToken.None
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(invocations, _factory.Application.Invocations.Count);
    }

    [Fact]
    public async Task PostInclusionPath_WhenTheTableIsBlank_Returns400WithoutReadingState()
    {
        var invocations = _factory.Application.Invocations.Count;

        using var response = await _client.PostAsJsonAsync(
            $"/api/plans/{Guid.NewGuid()}/inclusion-paths",
            new { table = " ", stableKey = "Id=42" },
            CancellationToken.None
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(invocations, _factory.Application.Invocations.Count);
    }
}
