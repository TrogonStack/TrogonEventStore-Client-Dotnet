// ReSharper disable ConvertIfStatementToSwitchStatement
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

using System.Diagnostics;
using KurrentDB.Diagnostics;
using KurrentDB.Diagnostics.Telemetry;
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

	public static void TraceSubscriptionEvent(
		this ActivitySource source,
		string? consumerGroupName,
		ResolvedEvent resolvedEvent,
		ChannelInfo channelInfo,
		KurrentDBClientSettings settings
	) {
		if (source.HasNoActiveListeners() || resolvedEvent.Event is null)
			return;

		var propagationContext = resolvedEvent.Event.Metadata.ExtractPropagationContext();

		if (propagationContext.ActivityContext == default)
			return;

		var destination = resolvedEvent.OriginalEvent.EventStreamId;
		var tags = new ActivityTagsCollection()
			.WithRequiredTag(TelemetryAttributes.MessagingSystem, SystemName)
			.WithRequiredTag(TelemetryAttributes.MessagingOperationName, Operations.Process)
			.WithRequiredTag(TelemetryAttributes.MessagingOperationType, Operations.Process)
			.WithRequiredTag(TelemetryAttributes.MessagingDestinationName, destination)
			.WithOptionalTag(TelemetryAttributes.MessagingConsumerGroupName, consumerGroupName)
			.WithRequiredTag(TelemetryAttributes.MessagingMessageId, resolvedEvent.OriginalEvent.EventId.ToString())
			.WithRequiredTag(TrogonTelemetryAttributes.EventType, resolvedEvent.OriginalEvent.EventType)
			.WithGrpcChannelServerTags(channelInfo)
			.WithClientSettingsServerTags(settings);

		using var activity = StartActivity(
			source,
			$"{Operations.Process} {destination}",
			ActivityKind.Consumer,
			tags,
			propagationContext.ActivityContext
		);

		if (activity is null)
			return;

		foreach (var (name, value) in propagationContext.Baggage.GetBaggage())
			activity.AddBaggage(name, value);
	}

	static Activity? StartActivity(
		this ActivitySource source,
		string operationName, ActivityKind activityKind, ActivityTagsCollection? tags = null,
		ActivityContext? parentContext = null
	) {
		if (source.HasNoActiveListeners())
			return null;

		return source
			.CreateActivity(
				operationName,
				activityKind,
				parentContext ?? default,
				tags,
				idFormat: ActivityIdFormat.W3C
			)
			?.Start();
	}

	static bool HasNoActiveListeners(this ActivitySource source) => !source.HasListeners();
}
