using System.Diagnostics;
using KurrentDB.Client.Tests.Fixtures;
using KurrentDB.Client.Tests.TestNode;
using KurrentDB.Diagnostics.Telemetry;
using KurrentDB.Diagnostics.Tracing;

namespace KurrentDB.Client.Tests.Diagnostics;

[Trait("Category", "Target:Diagnostics")]
[Collection(DiagnosticsCollection.Name)]
public class PersistentSubscriptionsTracingInstrumentationTests(ITestOutputHelper output, DiagnosticsFixture fixture)
	: KurrentDBPermanentTests<DiagnosticsFixture>(output, fixture) {
	[RetryFact]
	public async Task persistent_subscription_receive_links_remote_append_context() {
		var traceId = Fixture.CreateTraceId();
		var subscriber = Activity.Current!;
		var stream = Fixture.GetStreamName();
		var events = Fixture.CreateTestEvents(2, metadata: Fixture.CreateTestJsonMetadata()).ToArray();

		var groupName = $"{stream}-group";
		await Fixture.Subscriptions.CreateToStreamAsync(
			stream,
			groupName,
			new()
		);

		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			events
		);

		string? subscriptionId = null;
		await Subscribe().WithTimeout();

		var appendActivity = Fixture
			.GetActivities(TracingConstants.Operations.Append, traceId)
			.SingleOrDefault()
			.ShouldNotBeNull();

		var subscribeActivities = Fixture
			.GetActivities(SubscriptionTraceSemantics.Operation, traceId, stream)
			.Where(activity => Equals(
				activity.GetTagItem(TelemetryAttributes.MessagingConsumerGroupName),
				groupName
			))
			.ToArray();
		var expectedEventIds = events.Select(@event => @event.EventId.ToString()).ToHashSet();
		var actualEventIds = subscribeActivities
			.Select(activity => Assert.IsType<string>(activity.GetTagItem(TelemetryAttributes.MessagingMessageId)))
			.ToHashSet();

		subscriptionId.ShouldNotBeNull();
		Assert.NotEmpty(subscribeActivities);
		Assert.True(expectedEventIds.SetEquals(actualEventIds));

		foreach (var subscribeActivity in subscribeActivities) {
			subscribeActivity.TraceId.ShouldBe(subscriber.TraceId);
			subscribeActivity.ParentSpanId.ShouldBe(subscriber.SpanId);
			subscribeActivity.HasRemoteParent.ShouldBeFalse();
			var messageLink = subscribeActivity.Links.ShouldHaveSingleItem().Context;
			messageLink.TraceId.ShouldBe(appendActivity.TraceId);
			messageLink.SpanId.ShouldBe(appendActivity.SpanId);
			messageLink.IsRemote.ShouldBeTrue();
			subscribeActivity.GetTagItem(TelemetryAttributes.MessagingConsumerGroupName).ShouldBe(groupName);

			Fixture.AssertSubscriptionActivityHasExpectedTags(
				subscribeActivity,
				stream,
				Assert.IsType<string>(subscribeActivity.GetTagItem(TelemetryAttributes.MessagingMessageId)),
				groupName
			);
		}

		return;

		async Task Subscribe() {
			await using var subscription = Fixture.Subscriptions.SubscribeToStream(stream, groupName);
			await using var enumerator = subscription.Messages.GetAsyncEnumerator();

			var remainingEventIds = events.Select(@event => @event.EventId).ToHashSet();
			while (await enumerator.MoveNextAsync()) {
				if (enumerator.Current is PersistentSubscriptionMessage.SubscriptionConfirmation(var sid))
					subscriptionId = sid;

				if (enumerator.Current is not PersistentSubscriptionMessage.Event(var resolvedEvent, _))
					continue;

				remainingEventIds.Remove(resolvedEvent.Event.EventId);
				if (remainingEventIds.Count == 0)
					return;
			}
		}
	}

	[RetryFact]
	public async Task persistent_subscription_handles_non_json_events() {
		var stream = Fixture.GetStreamName();
		var events = Fixture.CreateTestEvents(
			2,
			metadata: Fixture.CreateTestJsonMetadata(),
			contentType: Constants.Metadata.ContentTypes.ApplicationOctetStream
		).ToArray();

		var groupName = $"{stream}-group";
		await Fixture.Subscriptions.CreateToStreamAsync(
			stream,
			groupName,
			new()
		);

		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			events
		);

		await Subscribe().WithTimeout();

		return;

		async Task Subscribe() {
			await using var subscription = Fixture.Subscriptions.SubscribeToStream(stream, groupName);
			await using var enumerator = subscription.Messages.GetAsyncEnumerator();

			var eventsAppeared = 0;
			while (await enumerator.MoveNextAsync()) {
				if (enumerator.Current is PersistentSubscriptionMessage.Event(_, _))
					eventsAppeared++;

				if (eventsAppeared >= events.Length)
					return;
			}
		}
	}

	[RetryFact]
	public async Task persistent_subscription_handles_invalid_json_metadata() {
		var stream = Fixture.GetStreamName();
		var events = Fixture.CreateTestEvents(
			2,
			metadata: "clearlynotavalidjsonobject"u8.ToArray()
		).ToArray();

		var groupName = $"{stream}-group";
		await Fixture.Subscriptions.CreateToStreamAsync(
			stream,
			groupName,
			new()
		);

		await Fixture.Streams.AppendToStreamAsync(
			stream,
			StreamState.NoStream,
			events
		);

		await Subscribe().WithTimeout();

		return;

		async Task Subscribe() {
			await using var subscription = Fixture.Subscriptions.SubscribeToStream(stream, groupName);
			await using var enumerator = subscription.Messages.GetAsyncEnumerator();

			var eventsAppeared = 0;
			while (await enumerator.MoveNextAsync()) {
				if (enumerator.Current is PersistentSubscriptionMessage.Event(_, _))
					eventsAppeared++;

				if (eventsAppeared >= events.Length)
					return;
			}
		}
	}
}
