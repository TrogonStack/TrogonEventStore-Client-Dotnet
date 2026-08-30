// ReSharper disable ConvertIfStatementToSwitchStatement
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

using System.Diagnostics;
using KurrentDB.Diagnostics;
using KurrentDB.Diagnostics.Telemetry;
using KurrentDB.Diagnostics.Tracing;
using OpenTelemetry;
using static KurrentDB.Diagnostics.Tracing.TracingConstants;

namespace KurrentDB.Client.Diagnostics;

static class ActivitySourceExtensions {
	public static ValueTask<T> TraceClientOperation<T>(
		this ActivitySource source,
		Func<ValueTask<T>> tracedOperation,
		string operationName,
		ActivityTagsCollection? tags = null
	) => source.TraceClientOperation(_ => tracedOperation(), operationName, tags);

	public static async ValueTask<T> TraceClientOperation<T>(
		this ActivitySource source,
		Func<Activity?, ValueTask<T>> tracedOperation,
		string operationName,
		ActivityTagsCollection? tags = null
	) {
		if (source.HasNoActiveListeners())
			return await tracedOperation(null).ConfigureAwait(false);

		(tags ??= new ActivityTagsCollection())
			.WithRequiredTag(TelemetryAttributes.DbSystemName, SystemName)
			.WithRequiredTag(TelemetryAttributes.DbOperationName, operationName);

		var target = tags.FirstOrDefault(tag => tag.Key == TelemetryAttributes.DbCollectionName).Value as string;
		var spanName = target is null ? operationName : $"{operationName} {target}";
		using var activity = StartActivity(source, spanName, ActivityKind.Client, tags, Activity.Current?.Context);

		try {
			var res = await tracedOperation(activity).ConfigureAwait(false);
			activity?.StatusOk();
			return res;
		} catch (Exception ex) {
			activity?.StatusError(ex);
			throw;
		}
	}

	public static SubscriptionReceive StartSubscriptionReceive(
		this ActivitySource source,
		string? consumerGroupName
	) => source.HasNoActiveListeners()
		? default
		: new(source, consumerGroupName, Activity.Current?.Context ?? default, DateTimeOffset.UtcNow);

	public readonly struct SubscriptionReceive {
		readonly ActivitySource? _source;
		readonly string? _consumerGroupName;
		readonly ActivityContext _parentContext;
		readonly DateTimeOffset _startedAt;

		internal SubscriptionReceive(
			ActivitySource source,
			string? consumerGroupName,
			ActivityContext parentContext,
			DateTimeOffset startedAt
		) {
			_source = source;
			_consumerGroupName = consumerGroupName;
			_parentContext = parentContext;
			_startedAt = startedAt;
		}

		public void Complete(
			ResolvedEvent resolvedEvent,
			ChannelInfo channelInfo,
			KurrentDBClientSettings settings
		) {
			if (_source is null)
				return;

			var deliveredEvent = resolvedEvent.Event ?? resolvedEvent.Link;
			if (deliveredEvent is null)
				return;

			var propagationContext = deliveredEvent.Metadata.ExtractPropagationContext();
			var destination = resolvedEvent.OriginalEvent.EventStreamId;
			var tags = new ActivityTagsCollection()
				.WithRequiredTag(TelemetryAttributes.MessagingSystem, SystemName)
				.WithRequiredTag(TelemetryAttributes.MessagingOperationName, SubscriptionTraceSemantics.Operation)
				.WithRequiredTag(TelemetryAttributes.MessagingOperationType, SubscriptionTraceSemantics.Operation)
				.WithRequiredTag(TelemetryAttributes.MessagingDestinationName, destination)
				.WithOptionalTag(TelemetryAttributes.MessagingConsumerGroupName, _consumerGroupName)
				.WithRequiredTag(TelemetryAttributes.MessagingMessageId, resolvedEvent.OriginalEvent.EventId.ToString())
				.WithRequiredTag(TrogonTelemetryAttributes.EventType, deliveredEvent.EventType)
				.WithGrpcChannelServerTags(channelInfo)
				.WithClientSettingsServerTags(settings);
			var links = propagationContext.ActivityContext == default
				? null
				: new[] { new ActivityLink(propagationContext.ActivityContext) };

			using var activity = StartActivity(
				_source,
				$"{SubscriptionTraceSemantics.Operation} {destination}",
				SubscriptionTraceSemantics.SpanKind,
				tags,
				_parentContext,
				links,
				_startedAt
			);

			if (activity is null)
				return;

			foreach (var (name, value) in propagationContext.Baggage.GetBaggage())
				activity.AddBaggage(name, value);
		}
	}

	static Activity? StartActivity(
		this ActivitySource source,
		string operationName, ActivityKind activityKind, ActivityTagsCollection? tags = null,
		ActivityContext? parentContext = null,
		IEnumerable<ActivityLink>? links = null,
		DateTimeOffset startTime = default
	) {
		if (source.HasNoActiveListeners())
			return null;

		var activity = source.CreateActivity(
				operationName,
				activityKind,
				parentContext ?? default,
				tags,
				links,
				idFormat: ActivityIdFormat.W3C
			);

		if (activity is null)
			return null;

		if (startTime != default)
			activity.SetStartTime(startTime.UtcDateTime);

		return activity.Start();
	}

	static bool HasNoActiveListeners(this ActivitySource source) => !source.HasListeners();
}
