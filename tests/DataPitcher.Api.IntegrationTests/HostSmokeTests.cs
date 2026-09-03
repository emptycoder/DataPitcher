using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using DataPitcher.Api.Contracts;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class HostSmokeTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HealthLive_ReturnsLiveStatusOnly()
    {
        using var response = await _client.GetAsync("/health/live", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Single(root.EnumerateObject(), property => property.Name == "status");
        Assert.Equal("live", root.GetProperty("status").GetString());
    }

    private static readonly IReadOnlySet<string> AllowedResourceIdentifierNames = new HashSet<string>
    {
        "connectionId",
        "planId",
        "jobId",
        "snapshotId",
        "selectionId",
        "operationId",
    };

    public static IEnumerable<object[]> ResponseRecordTypes()
    {
        yield return [typeof(ConnectionResponse)];
        yield return [typeof(OperationReceiptResponse)];
        yield return [typeof(OperationStatusResponse)];
        yield return [typeof(ProviderResponse)];
        yield return [typeof(ResourceIdentifiers)];
        yield return [typeof(SchemaSnapshotResponse)];
        yield return [typeof(SelectionResponse)];
        yield return [typeof(PlanResponse)];
        yield return [typeof(JobResponse)];
        yield return [typeof(JobSummaryResponse)];
    }

    [Theory]
    [MemberData(nameof(ResponseRecordTypes))]
    public void ResponseRecords_SerializeOnlyKnownResourceIdentifierNamesAsGuids(Type responseType)
    {
        var instance = CreateSample(responseType);
        var json = JsonSerializer.Serialize(
            instance,
            responseType,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && Guid.TryParse(property.Value.GetString(), out _))
            {
                Assert.Contains(property.Name, AllowedResourceIdentifierNames);
            }
        }
    }

    private static object CreateSample(Type type)
    {
        var constructor = type.GetConstructors().Single();
        var guid = Guid.NewGuid();
        var arguments = constructor
            .GetParameters()
            .Select(parameter => CreateArgument(parameter.ParameterType, parameter.Name!, guid))
            .ToArray();
        return constructor.Invoke(arguments);
    }

    private static object? CreateArgument(Type parameterType, string name, Guid guid)
    {
        var underlying = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        if (underlying == typeof(Guid))
            return guid;
        if (underlying == typeof(string))
            return name.Equals("ETag", StringComparison.OrdinalIgnoreCase) ? "\"etag-1\""
                : name.Equals("Hash", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CanonicalHash", StringComparison.OrdinalIgnoreCase)
                    ? "sha256-not-a-guid"
                : "sample-" + name;
        if (underlying == typeof(long))
            return 1L;
        if (underlying == typeof(int))
            return 1;
        if (underlying == typeof(bool))
            return true;
        if (underlying == typeof(DateTimeOffset))
            return DateTimeOffset.UnixEpoch;
        if (underlying == typeof(Uri))
            return new Uri("https://example.test/status");
        throw new NotSupportedException($"Unsupported parameter type {parameterType} for {name}.");
    }
}
