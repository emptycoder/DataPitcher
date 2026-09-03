using System.Net;
using System.Text;
using System.Text.Json;
using DataPitcher.Api.Authorization;
using DataPitcher.Application.Events;
using DataPitcher.ControlStore;
using DataPitcher.Core.Jobs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class JobEventStreamTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;

    [Fact]
    public async Task JobEvents_WhenReadAfterCursor_ReturnsOnlyStrictlyLaterOrderedEvents()
    {
        var jobId = Guid.NewGuid();
        var first = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "state", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );
        var second = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 20, 200)),
            CancellationToken.None
        );

        var page = await _factory.Events.ReadAfterAsync(jobId, first.EventId, CancellationToken.None);

        Assert.Equal([second.EventId], page.Events.Select(jobEvent => jobEvent.EventId));
    }

    [Fact]
    public async Task JobEvents_WhenCursorPrecedesRetentionBoundary_RequiresReload()
    {
        var jobId = Guid.NewGuid();
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "state", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 20, 200)),
            CancellationToken.None
        );
        var retained = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 30, 300)),
            CancellationToken.None
        );
        await _factory.Events.TrimBeforeAsync(jobId, retained.EventId, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<EventCursorExpiredException>(() =>
            _factory.Events.ReadAfterAsync(jobId, retained.EventId - 2, CancellationToken.None)
        );

        Assert.Equal(retained.EventId, exception.OldestAvailableEventId);
    }

    [Fact]
    public async Task JobEvents_WhenTrimBoundaryExceedsTheNextEventId_RejectsTheInvalidRetentionBoundary()
    {
        var jobId = Guid.NewGuid();
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "state", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _factory.Events.TrimBeforeAsync(jobId, 3, CancellationToken.None)
        );
    }

    [Fact]
    public async Task JobEventSignal_WhenCommittedEventIsPublished_WakesWaitingStream()
    {
        var jobId = Guid.NewGuid();
        var signal = new JobEventSignal();
        var wait = signal.WaitAsync(jobId, 0, CancellationToken.None);

        signal.Publish(
            new JobEvent(jobId, 1, "progress", new JobEventPayload("running", 10, 100), DateTimeOffset.UnixEpoch)
        );

        await wait;
    }

    [Fact]
    public async Task JobEventSignal_WhenEventWasAlreadyPublishedAfterCursor_DoesNotWait()
    {
        var jobId = Guid.NewGuid();
        var signal = new JobEventSignal();
        signal.Publish(
            new JobEvent(jobId, 1, "progress", new JobEventPayload("running", 10, 100), DateTimeOffset.UnixEpoch)
        );

        await signal.WaitAsync(jobId, 0, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task JobEventSignal_WhenPublishersRace_RetainsTheLatestNotification()
    {
        var jobId = Guid.NewGuid();
        var signal = new JobEventSignal();
        Parallel.For(
            1,
            1025,
            eventId =>
                signal.Publish(
                    new JobEvent(
                        jobId,
                        eventId,
                        "progress",
                        new JobEventPayload("running", eventId, eventId),
                        DateTimeOffset.UnixEpoch
                    )
                )
        );

        await signal.WaitAsync(jobId, 1023, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task JobEventSignal_WhenPublishingRacesWithWait_DoesNotMissTheEvent()
    {
        for (var eventId = 1; eventId <= 128; eventId++)
        {
            var jobId = Guid.NewGuid();
            var signal = new JobEventSignal();
            using var gate = new Barrier(2);
            var wait = Task.Run(async () =>
            {
                gate.SignalAndWait();
                await signal.WaitAsync(jobId, 0, CancellationToken.None);
            });
            var publish = Task.Run(() =>
            {
                gate.SignalAndWait();
                signal.Publish(
                    new JobEvent(
                        jobId,
                        eventId,
                        "progress",
                        new JobEventPayload("running", eventId, eventId),
                        DateTimeOffset.UnixEpoch
                    )
                );
            });

            await Task.WhenAll(wait, publish).WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task JobEvents_WhenReconnectedWithLastEventId_StreamsOnlyLaterFrame()
    {
        var jobId = Guid.NewGuid();
        var first = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "state", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );
        var second = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 20, 200)),
            CancellationToken.None
        );
        using var request = EventRequest(jobId, first.EventId);
        using var response = await _factory
            .CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var frame = await ReadFrameAsync(response);

        Assert.Equal(
            $"id: {second.EventId}\nevent: progress\ndata: {{\"State\":\"running\",\"RowsTransferred\":20,\"BytesTransferred\":200}}\n\n",
            frame
        );
    }

    [Fact]
    public async Task JobEvents_WhenEmptyStreamReceivesCommittedEvent_DeliversTheFrame()
    {
        var jobId = Guid.NewGuid();
        using var request = EventRequest(jobId);
        using var response = await _factory
            .CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        var frame = ReadFrameAsync(response);
        var appended = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );

        Assert.Equal(
            $"id: {appended.EventId}\nevent: progress\ndata: {{\"State\":\"running\",\"RowsTransferred\":10,\"BytesTransferred\":100}}\n\n",
            await frame
        );
    }

    [Fact]
    public async Task JobEvents_WhenAnEventArrivesBeforeWaiting_RechecksTheDurableStore()
    {
        var signal = new AppendDuringWaitSignal();
        using var factory = new ApiWebApplicationFactory(signal);
        var jobId = Guid.NewGuid();
        signal.AppendOnFirstWait = () =>
            factory.Events.AppendAsync(
                new JobEventAppend(jobId, "progress", new JobEventPayload("running", 10, 100)),
                CancellationToken.None
            );
        using var request = EventRequest(jobId);
        using var response = await factory
            .CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Contains(
            "data: {\"State\":\"running\",\"RowsTransferred\":10,\"BytesTransferred\":100}",
            await ReadFrameAsync(response),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task JobEvents_WhenCursorHasExpired_ReturnsReloadRequiredProblemDetails()
    {
        var jobId = Guid.NewGuid();
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "state", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 20, 200)),
            CancellationToken.None
        );
        var retained = await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 30, 300)),
            CancellationToken.None
        );
        await _factory.Events.TrimBeforeAsync(jobId, retained.EventId, CancellationToken.None);
        using var request = EventRequest(jobId, retained.EventId - 2);
        using var response = await _factory.CreateClient().SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("event_cursor_expired", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("reloadRequired").GetBoolean());
    }

    [Fact]
    public async Task JobEvents_WhenReconnectsAfterAuthorizedOpen_ReauthorizesTheSpecificJob()
    {
        var jobId = Guid.NewGuid();
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "state", new JobEventPayload("running", 0, 0)),
            CancellationToken.None
        );
        var calls = _factory.Grants.JobAuthorizationCalls;
        using var firstRequest = EventRequest(jobId);
        using var first = await _factory
            .CreateClient()
            .SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        await ReadFrameAsync(first);
        _factory.Grants.AllowJob(jobId, false);
        using var secondRequest = EventRequest(jobId);
        using var second = await _factory.CreateClient().SendAsync(secondRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(calls + 2, _factory.Grants.JobAuthorizationCalls);
    }

    [Fact]
    public async Task JobEvents_WhenCredentialsAreMissing_Returns401()
    {
        using var request = EventRequest(Guid.NewGuid());
        request.Headers.Add("X-Test-Unauthenticated", "true");
        using var response = await _factory.CreateClient().SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints,
            endpoint =>
                endpoint is RouteEndpoint routeEndpoint
                && routeEndpoint.RoutePattern.RawText == "/api/jobs/{jobId:guid}/events"
        );
    }

    [Fact]
    public async Task JobEvents_WhenJobAccessIsDenied_Returns403()
    {
        var jobId = Guid.NewGuid();
        _factory.Grants.AllowJob(jobId, false);
        using var response = await _factory.CreateClient().SendAsync(EventRequest(jobId), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("token")]
    [InlineData("authorization")]
    public async Task JobEvents_WhenCredentialQueryParameterIsSupplied_ReturnsValidationProblemDetails(string parameter)
    {
        using var response = await _factory
            .CreateClient()
            .GetAsync($"/api/jobs/{Guid.NewGuid()}/events?{parameter}=forbidden", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task JobEvents_WhenLastEventIdIsMalformed_ReturnsValidationProblemDetails()
    {
        using var request = EventRequest(Guid.NewGuid());
        request.Headers.Add("Last-Event-ID", "not-an-event-id");
        using var response = await _factory.CreateClient().SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task JobEvents_WhenTokenExpires_ClosesBeforeLaterEvent()
    {
        var jobId = Guid.NewGuid();
        using var request = EventRequest(jobId);
        request.Headers.Add("X-Test-Token-Expiry", DateTimeOffset.UtcNow.AddMilliseconds(150).ToString("O"));
        using var response = await _factory
            .CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(250);
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );

        Assert.DoesNotContain(
            "data:",
            await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(1)),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task JobEvents_WhenTokenIsAlreadyExpired_ClosesWithoutWritingEvents()
    {
        var jobId = Guid.NewGuid();
        using var request = EventRequest(jobId);
        request.Headers.Add("X-Test-Token-Expiry", DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));
        using var response = await _factory
            .CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        await _factory.Events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            "data:",
            await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(1)),
            StringComparison.Ordinal
        );
    }

    private static HttpRequestMessage EventRequest(Guid jobId, long? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/events");
        if (lastEventId is { } cursor)
            request.Headers.Add("Last-Event-ID", cursor.ToString());
        return request;
    }

    private static async Task<string> ReadFrameAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var length = await stream.ReadAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private sealed class AppendDuringWaitSignal : IJobEventSignal
    {
        private readonly JobEventSignal _signal = new();
        private int _waits;
        public Func<Task>? AppendOnFirstWait { get; set; }

        public async Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _waits) == 1 && AppendOnFirstWait is { } append)
                await append();
            await _signal.WaitAsync(jobId, lastObservedEventId, cancellationToken);
        }

        public void Publish(JobEvent jobEvent) => _signal.Publish(jobEvent);
    }
}
