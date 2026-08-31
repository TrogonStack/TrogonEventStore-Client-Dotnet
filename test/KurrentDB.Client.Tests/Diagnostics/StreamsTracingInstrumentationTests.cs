// ReSharper disable AccessToDisposedClosure

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KurrentDB.Client.Diagnostics;
using KurrentDB.Client.Tests.Fixtures;
using KurrentDB.Diagnostics.Telemetry;
using KurrentDB.Diagnostics.Tracing;

namespace KurrentDB.Client.Tests.Diagnostics;

[Trait("Category", "Target:Diagnostics")]
[Collection(DiagnosticsCollection.Name)]
public class StreamsTracingInstrumentationTests(ITestOutputHelper output, DiagnosticsFixture fixture) : KurrentDBPermanentTests<DiagnosticsFixture>(output, fixture) {
	[Fact]
	public void trace_contexts_are_independent() {
		var first = Fixture.CreateTraceId();
		var second = Fixture.CreateTraceId();

		Assert.NotEqual(first, second);
	}

	[Fact]
	public async Task append_to_stream() {
		var traceId = Fixture.CreateTraceId();

		var stream = Fixture.GetStreamName();

		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			Fixture.CreateTestEvents()
		);

		var activity = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId)
			.SingleOrDefault()
			.ShouldNotBeNull();

		Fixture.AssertAppendActivityHasExpectedTags(activity, stream);
	}

	[MinimumVersion.Fact(25, 1)]
	public async Task multi_stream_append() {
		// Arrange
		var traceId = Fixture.CreateTraceId();
		var subscriber = Activity.Current!;

		var seedEvents = Fixture.CreateTestEvents(10).ToList();

		var availableEvents = new HashSet<Uuid>(seedEvents.Select(x => x.EventId));

		var stream1 = Fixture.GetStreamName();
		var stream2 = Fixture.GetStreamName();

		AppendStreamRequest[] requests = [new(stream1, StreamState.NoStream, seedEvents.Take(5)), new(stream2, StreamState.NoStream, seedEvents.Skip(5))];

		// Act
		var appendResult = await Fixture.Streams.MultiStreamAppendAsync(requests.ToAsyncEnumerable());

		await using var subscription = Fixture.Streams.SubscribeToAll(
			FromAll.Start,
			filterOptions: new SubscriptionFilterOptions(StreamFilter.Prefix(stream1, stream2))
		);

		await using var enumerator = subscription.Messages.GetAsyncEnumerator();

		await Subscribe().WithTimeout();

		// Assert
		appendResult.Position.ShouldBePositive();

		var appendActivities = Fixture.GetActivities(TracingConstants.Operations.BatchAppend, traceId);
		var subscribeActivities = Fixture.GetActivities(SubscriptionTraceSemantics.Operation, traceId);

		appendActivities.ShouldNotBeEmpty();
		subscribeActivities.ShouldNotBeEmpty();

		appendActivities.Count.ShouldBe(1);
		subscribeActivities.Count.ShouldBe(10);

		// They also have the same duration
		appendActivities.Select(x => x.Duration).Distinct().Count().ShouldBe(1);

		Assert.All(
			subscribeActivities,
			receiveActivity => {
				receiveActivity.ParentSpanId.ShouldBe(subscriber.SpanId);
				var messageLink = receiveActivity.Links.ShouldHaveSingleItem().Context;
				messageLink.TraceId.ShouldBe(appendActivities[0].TraceId);
				messageLink.SpanId.ShouldBe(appendActivities[0].SpanId);
			}
		);

		subscribeActivities
			.All(x => x.StartTimeUtc > appendActivities.First().StartTimeUtc)
			.ShouldBeTrue();

		Fixture.AssertMultiAppendActivityHasExpectedTags(appendActivities.First());
		Fixture.AssertSubscriptionActivityHasExpectedTags(subscribeActivities.First(), stream1, seedEvents.First().EventId.ToString());

		return;

		async Task Subscribe() {
			while (await enumerator.MoveNextAsync()) {
				if (enumerator.Current is not StreamMessage.Event(var resolvedEvent))
					continue;

				availableEvents.Remove(resolvedEvent.Event.EventId);

				if (availableEvents.Count is 0)
					return;
			}
		}
	}

	[MinimumVersion.Fact(25, 1)]
	public async Task multi_stream_append_with_exceptions() {
		var traceId = Fixture.CreateTraceId();

		// Arrange
		var stream1 = Fixture.GetStreamName();
		var stream2 = Fixture.GetStreamName();

		AppendStreamRequest[] requests = [
			new(stream1, StreamState.StreamExists, Fixture.CreateTestEvents()),
			new(stream2, StreamState.StreamExists, Fixture.CreateTestEvents())
		];

		// Act
		var appendTask = async () => await Fixture.Streams.MultiStreamAppendAsync(requests);
		var rex = await appendTask.ShouldThrowAsync<WrongExpectedVersionException>();

		// Assert
		var appendActivities = Fixture.GetActivities(TracingConstants.Operations.BatchAppend, traceId);

		appendActivities.ShouldNotBeEmpty();

		appendActivities.Count.ShouldBe(1);

		var activity = appendActivities.FirstOrDefault().ShouldNotBeNull();
		activity.Status.ShouldBe(ActivityStatusCode.Error);
		activity.Events.ShouldHaveSingleItem();

		var activityEvent = activity.Events.First();

		activityEvent.Name.ShouldBe(TracingConstants.ExceptionEventName);
		activityEvent.Tags.Any(tag => tag.Key == TelemetryAttributes.ExceptionMessage).ShouldBeTrue();
		activityEvent.Tags.Any(tag => tag.Key == TelemetryAttributes.ExceptionStacktrace).ShouldBeTrue();
		activityEvent.Tags.Any(tag => tag.Key == TelemetryAttributes.ExceptionType && (string?)tag.Value == rex.GetType().FullName).ShouldBeTrue();
	}

	[Fact]
	public async Task append_trace_tagged_with_error_on_exception() {
		var traceId = Fixture.CreateTraceId();
		var stream = Fixture.GetStreamName();

		var actualException = await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			Fixture.CreateTestEventsThatThrowsException()
		).ShouldThrowAsync<Exception>();

		var activity = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId)
			.SingleOrDefault()
			.ShouldNotBeNull();

		Fixture.AssertErroneousAppendActivityHasExpectedTags(activity, actualException);
	}

	[Fact]
	public async Task tracing_context_injected_when_metadata_is_json() {
		var traceId = Fixture.CreateTraceId();
		var stream = Fixture.GetStreamName();

		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			Fixture.CreateTestEvents(1, metadata: Fixture.CreateTestJsonMetadata())
		);

		var activity = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId)
			.SingleOrDefault()
			.ShouldNotBeNull();

		var readResult = await Fixture.Streams
			.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start)
			.ToListAsync();

		var propagationContext = readResult[0].OriginalEvent.Metadata.ExtractPropagationContext();

		propagationContext.ActivityContext.ShouldNotBe(default);
		propagationContext.ActivityContext.TraceId.ShouldBe(activity.TraceId);
		propagationContext.ActivityContext.SpanId.ShouldBe(activity.SpanId);
	}

	[Fact]
	public async Task subscription_receive_uses_ambient_parent_and_links_message_creation_context() {
		var producerTraceId = Fixture.CreateTraceId();
		Activity.Current!.TraceStateString = "vendor=producer";
		var stream = Fixture.GetStreamName();
		var metadata = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> {
			["baggage"] = "tenant=straw-hat"
		});
		var seedEvent = Fixture.CreateTestEvent(metadata: metadata);

		await Fixture.Streams.AppendToStreamAsync(stream, StreamState.NoStream, [seedEvent]);
		var appendActivity = Fixture
			.GetActivities(TracingConstants.Operations.Append, producerTraceId)
			.ShouldHaveSingleItem();

		Activity.Current = null;
		using var subscriber = new Activity("subscriber").SetIdFormat(ActivityIdFormat.W3C).Start();
		subscriber.TraceStateString = "vendor=consumer";

		await using var subscription = Fixture.Streams.SubscribeToStream(stream, FromStream.Start);
		await using var enumerator = subscription.Messages.GetAsyncEnumerator();

		Assert.True(await enumerator.MoveNextAsync());
		Assert.IsType<StreamMessage.SubscriptionConfirmation>(enumerator.Current);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.IsType<StreamMessage.Event>(enumerator.Current);

		var receiveActivity = Fixture
			.GetActivities(SubscriptionTraceSemantics.Operation, subscriber.TraceId, stream)
			.ShouldHaveSingleItem();

		receiveActivity.Kind.ShouldBe(ActivityKind.Client);
		receiveActivity.ParentSpanId.ShouldBe(subscriber.SpanId);
		receiveActivity.TraceStateString.ShouldBe("vendor=consumer");
		receiveActivity.Baggage.ShouldContain(new KeyValuePair<string, string?>("tenant", "straw-hat"));
		var messageLink = receiveActivity.Links.ShouldHaveSingleItem().Context;
		messageLink.TraceId.ShouldBe(appendActivity.TraceId);
		messageLink.SpanId.ShouldBe(appendActivity.SpanId);
		messageLink.TraceState.ShouldBe("vendor=producer");
		messageLink.IsRemote.ShouldBeTrue();
	}

	[Fact]
	public async Task tracing_context_not_injected_when_metadata_not_json() {
		var stream = Fixture.GetStreamName();

		var inputMetadata = "clearlynotavalidjsonobject"u8.ToArray();
		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			Fixture.CreateTestEvents(1, metadata: inputMetadata)
		);

		var readResult = await Fixture.Streams
			.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start)
			.ToListAsync();

		var outputMetadata = readResult[0].OriginalEvent.Metadata.ToArray();
		outputMetadata.ShouldBe(inputMetadata);
	}

	[Fact]
	public async Task tracing_context_replaced_when_already_present() {
		// Arrange
		var stream = Fixture.GetStreamName();

		using var activity = new Activity(Guid.NewGuid().ToString("N"));
		activity.Start();

		var metadata = new Dictionary<string, string> {
			["traceparent"] = $"00-{activity.TraceId}-{activity.SpanId}-00"
		};

		// Act
		await Fixture.Streams.AppendToStreamAsync(stream, StreamState.NoStream, Fixture.CreateTestEvents(metadata: JsonSerializer.SerializeToUtf8Bytes(metadata)));

		// Assert
		var result = await Fixture.Streams
			.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, maxCount: 1)
			.ToListAsync();

		var outputMetadata = result.First().OriginalEvent.Metadata;
		var propagationContext = outputMetadata.ExtractPropagationContext();
		var appendActivity = Fixture
			.GetActivities(TracingConstants.Operations.Append, activity.TraceId)
			.ShouldHaveSingleItem();

		propagationContext.ActivityContext.ShouldNotBe(default);
		propagationContext.ActivityContext.TraceId.ShouldBe(appendActivity.TraceId);
		propagationContext.ActivityContext.SpanId.ShouldBe(appendActivity.SpanId);
		propagationContext.ActivityContext.TraceFlags.ShouldBe(appendActivity.ActivityTraceFlags);

		using var document = JsonDocument.Parse(outputMetadata);
		document.RootElement.EnumerateObject().Count(property => property.Name == "traceparent").ShouldBe(1);
	}

	[Fact]
	public async Task tracing_context_injected_when_event_not_json_but_metadata_json() {
		var traceId = Fixture.CreateTraceId();
		var stream = Fixture.GetStreamName();

		var inputMetadata = Fixture.CreateTestJsonMetadata().ToArray();
		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			Fixture.CreateTestEvents(
				metadata: inputMetadata,
				contentType: Constants.Metadata.ContentTypes.ApplicationOctetStream
			)
		);

		var readResult = await Fixture.Streams
			.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start)
			.ToListAsync();

		var outputMetadata = readResult[0].OriginalEvent.Metadata.ToArray();
		outputMetadata.ShouldNotBe(inputMetadata);

		var appendActivities = Fixture.GetActivities(TracingConstants.Operations.Append, traceId);

		appendActivities.ShouldNotBeEmpty();
	}

	[Fact]
	public async Task subscription_receive_is_emitted_without_propagated_context() {
		var traceId = Fixture.CreateTraceId();
		var streamName = Fixture.GetStreamName();

		var seedEvents = new[] {
			Fixture.CreateTestEvent(metadata: Fixture.CreateTestJsonMetadata()),
			Fixture.CreateTestEvent(metadata: Fixture.CreateTestNonJsonMetadata())
		};

		var availableEvents = new HashSet<Uuid>(seedEvents.Select(x => x.EventId));

		await Fixture.Streams.AppendToStreamAsync(streamName, StreamState.NoStream, seedEvents);

		await using var subscription = Fixture.Streams.SubscribeToStream(streamName, FromStream.Start);
		await using var enumerator = subscription.Messages.GetAsyncEnumerator();

		var appendActivities = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId)
			.ShouldNotBeNull();

		Assert.True(await enumerator.MoveNextAsync());

		Assert.IsType<StreamMessage.SubscriptionConfirmation>(enumerator.Current);

		await Subscribe(enumerator).WithTimeout();

		var subscribeActivities = Fixture
			.GetActivities(SubscriptionTraceSemantics.Operation, traceId, streamName)
			.ToArray();

		appendActivities.ShouldHaveSingleItem();
		subscribeActivities.Length.ShouldBe(seedEvents.Length);
		var receiveWithoutMessageContext = subscribeActivities.Single(activity => Equals(
			activity.GetTagItem(TelemetryAttributes.MessagingMessageId),
			seedEvents.Last().EventId.ToString()
		));
		receiveWithoutMessageContext.Links.ShouldBeEmpty();
		Fixture.AssertSubscriptionActivityHasExpectedTags(
			receiveWithoutMessageContext,
			streamName,
			seedEvents.Last().EventId.ToString()
		);

		return;

		async Task Subscribe(IAsyncEnumerator<StreamMessage> internalEnumerator) {
			while (await internalEnumerator.MoveNextAsync()) {
				if (internalEnumerator.Current is not StreamMessage.Event(var resolvedEvent))
					continue;

				availableEvents.Remove(resolvedEvent.Event.EventId);

				if (availableEvents.Count == 0)
					return;
			}
		}
	}

	[RetryFact]
	[Trait("Category", "Special cases")]
	public async Task unresolved_link_receive_falls_back_to_original_link_semantics() {
		var traceId = Fixture.CreateTraceId();
		var targetStream = Fixture.GetStreamName();
		var linkStream = Fixture.GetStreamName();

		await Fixture.Streams.AppendToStreamAsync(
			targetStream,
			StreamState.NoStream,
			Fixture.CreateTestEvents(1)
		);
		var linkEvent = new EventData(
			Uuid.NewUuid(),
			SystemEventTypes.LinkTo,
			Encoding.UTF8.GetBytes($"0@{targetStream}"),
			Fixture.CreateTestJsonMetadata(),
			Constants.Metadata.ContentTypes.ApplicationOctetStream
		);
		await Fixture.Streams.AppendToStreamAsync(linkStream, StreamState.NoStream, [linkEvent]);
		var linkAppendActivity = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId, linkStream)
			.ShouldHaveSingleItem();
		await Fixture.Streams.DeleteAsync(targetStream, StreamState.StreamExists);

		await using var subscription = Fixture.Streams.SubscribeToStream(
			linkStream,
			FromStream.Start,
			resolveLinkTos: true
		);
		await using var enumerator = subscription.Messages.GetAsyncEnumerator();

		Assert.True(await enumerator.MoveNextAsync());
		Assert.IsType<StreamMessage.SubscriptionConfirmation>(enumerator.Current);
		Assert.True(await enumerator.MoveNextAsync());
		var unresolvedLink = Assert.IsType<StreamMessage.Event>(enumerator.Current).ResolvedEvent;

		unresolvedLink.Event.ShouldBeNull();
		unresolvedLink.Link.ShouldNotBeNull();
		unresolvedLink.OriginalEvent.EventType.ShouldBe(SystemEventTypes.LinkTo);

		var receiveActivity = Fixture
			.GetActivities(SubscriptionTraceSemantics.Operation, traceId, linkStream)
			.ShouldHaveSingleItem();
		receiveActivity.GetTagItem(TelemetryAttributes.MessagingDestinationName)
			.ShouldBe(unresolvedLink.OriginalEvent.EventStreamId);
		receiveActivity.GetTagItem(TelemetryAttributes.MessagingMessageId)
			.ShouldBe(unresolvedLink.OriginalEvent.EventId.ToString());
		receiveActivity.GetTagItem(TrogonTelemetryAttributes.EventType)
			.ShouldBe(unresolvedLink.OriginalEvent.EventType);
		var messageLink = receiveActivity.Links.ShouldHaveSingleItem().Context;
		messageLink.TraceId.ShouldBe(linkAppendActivity.TraceId);
		messageLink.SpanId.ShouldBe(linkAppendActivity.SpanId);
	}

	[RetryFact]
	[Trait("Category", "Special cases")]
	public async Task resolved_link_receive_uses_target_event_type_and_original_link_identity() {
		var traceId = Fixture.CreateTraceId();
		var category = Guid.NewGuid().ToString("N");
		var streamName = category + "-123";
		var categoryStream = "$ce-" + category;

		var seedEvents = Fixture.CreateTestEvents(type: $"{category}-{Fixture.GetStreamName()}").ToArray();
		ResolvedEvent? receivedEvent = null;
		await Fixture.Streams.AppendToStreamAsync(streamName, StreamState.NoStream, seedEvents);

		await Fixture.Streams.DeleteAsync(streamName, StreamState.StreamExists);

		await using var subscription = Fixture.Streams.SubscribeToStream(categoryStream, FromStream.Start, resolveLinkTos: true);

		await using var enumerator = subscription.Messages.GetAsyncEnumerator();

		Assert.True(await enumerator.MoveNextAsync());

		Assert.IsType<StreamMessage.SubscriptionConfirmation>(enumerator.Current);

		await Subscribe().WithTimeout();

		var appendActivities = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId)
			.ShouldNotBeNull();

		var subscribeActivities = Fixture
			.GetActivities(SubscriptionTraceSemantics.Operation, traceId, categoryStream)
			.ToArray();

		appendActivities.ShouldHaveSingleItem();
		var resolvedLink = receivedEvent!.Value;
		var receiveActivity = subscribeActivities.Single(activity => Equals(
			activity.GetTagItem(TelemetryAttributes.MessagingMessageId),
			resolvedLink.OriginalEvent.EventId.ToString()
		));
		resolvedLink.IsResolved.ShouldBeTrue();
		resolvedLink.OriginalEvent.EventType.ShouldBe("$>");
		resolvedLink.Event.EventType.ShouldBe("$metadata");
		receiveActivity.Links.ShouldBeEmpty();
		receiveActivity.Kind.ShouldBe(SubscriptionTraceSemantics.SpanKind);
		receiveActivity.GetTagItem(TelemetryAttributes.MessagingDestinationName)
			.ShouldBe(resolvedLink.OriginalEvent.EventStreamId);
		receiveActivity.GetTagItem(TelemetryAttributes.MessagingMessageId)
			.ShouldBe(resolvedLink.OriginalEvent.EventId.ToString());
		receiveActivity.GetTagItem(TrogonTelemetryAttributes.EventType)
			.ShouldBe(resolvedLink.Event.EventType);

		return;

		async Task Subscribe() {
			while (await enumerator.MoveNextAsync()) {
				if (enumerator.Current is not StreamMessage.Event(var resolvedEvent))
					continue;

				if (resolvedEvent.Event?.EventType is "$metadata") {
					receivedEvent = resolvedEvent;
					return;
				}
			}
		}
	}
}
