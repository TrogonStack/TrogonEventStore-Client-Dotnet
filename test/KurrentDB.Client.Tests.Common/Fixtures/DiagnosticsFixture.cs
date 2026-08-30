// ReSharper disable InconsistentNaming

using System.Collections.Concurrent;
using System.Diagnostics;
using KurrentDB.Client.Diagnostics;
using KurrentDB.Client.Tests.TestNode;
using KurrentDB.Diagnostics;
using KurrentDB.Diagnostics.Telemetry;
using KurrentDB.Diagnostics.Tracing;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace KurrentDB.Client.Tests.Fixtures;

public class DiagnosticsFixture : KurrentDBPermanentFixture {
	readonly ConcurrentDictionary<(string Operation, ActivityTraceId TraceId), List<Activity>> Activities = [];
	readonly TextMapPropagator OriginalPropagator = Propagators.DefaultTextMapPropagator;

	public DiagnosticsFixture() : base(x => x.RunProjections()) {
		var diagnosticActivityListener = new ActivityListener {
			ShouldListenTo = source => source.Name == KurrentDBClientDiagnostics.InstrumentationName,
			Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity => {
				var operation = (string?)activity.GetTagItem(TelemetryAttributes.DbOperationName)
					?? (string?)activity.GetTagItem(TelemetryAttributes.MessagingOperationName);

				if (operation is null)
					return;

				Activities.AddOrUpdate(
					(operation, activity.TraceId),
					_ => [activity],
					(_, activities) => {
						activities.Add(activity);
						return activities;
					}
				);
			}
		};

		OnSetup += () => {
			Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([
				new TraceContextPropagator(),
				new BaggagePropagator()
			]));
			ActivitySource.AddActivityListener(diagnosticActivityListener);
			return Task.CompletedTask;
		};

		OnTearDown = () => {
			diagnosticActivityListener.Dispose();
			Sdk.SetDefaultTextMapPropagator(OriginalPropagator);
			return Task.CompletedTask;
		};
	}

	public ActivityTraceId CreateTraceId() {
		Activity.Current = null;
		var activity = new Activity(Guid.NewGuid().ToString("N"));
		activity.Start();
		Activity.Current = activity;
		return activity.TraceId;
	}

	public List<Activity> GetActivities(string operation, ActivityTraceId traceId) =>
		Activities.TryGetValue((operation, traceId), out var activities) ? activities : [];

	public List<Activity> GetActivities(string operation, ActivityTraceId traceId, string stream) =>
		GetActivities(operation, traceId)
			.Where(activity =>
				Equals(activity.GetTagItem(TelemetryAttributes.DbCollectionName), stream) ||
				Equals(activity.GetTagItem(TelemetryAttributes.MessagingDestinationName), stream)
			)
			.ToList();

	public void AssertMultiAppendActivityHasExpectedTags(Activity activity) {
		activity.DisplayName.ShouldBe(TracingConstants.Operations.BatchAppend);
		activity.Kind.ShouldBe(ActivityKind.Client);

		var expectedTags = new Dictionary<string, string?> {
			{ TelemetryAttributes.DbSystemName, TracingConstants.SystemName },
			{ TelemetryAttributes.DbOperationName, TracingConstants.Operations.BatchAppend }
		};

		foreach (var tag in expectedTags)
			activity.Tags.ShouldContain(tag);

		activity.GetTagItem(TelemetryAttributes.DbOperationBatchSize).ShouldBe(2);
	}

	public void AssertAppendActivityHasExpectedTags(Activity activity, string stream) {
		activity.DisplayName.ShouldBe($"{TracingConstants.Operations.Append} {stream}");
		activity.Kind.ShouldBe(ActivityKind.Client);

		var expectedTags = new Dictionary<string, string?> {
			{ TelemetryAttributes.DbSystemName, TracingConstants.SystemName },
			{ TelemetryAttributes.DbOperationName, TracingConstants.Operations.Append },
			{ TelemetryAttributes.DbCollectionName, stream }
		};

		foreach (var tag in expectedTags)
			activity.Tags.ShouldContain(tag);
	}

	public void AssertErroneousAppendActivityHasExpectedTags(Activity activity, Exception actualException) {
		var expectedTags = new Dictionary<string, string?> {
			{ TelemetryAttributes.ErrorType, actualException.GetType().FullName }
		};

		foreach (var tag in expectedTags)
			activity.Tags.ShouldContain(tag);

		var actualEvent = activity.Events.ShouldHaveSingleItem();

		actualEvent.Name.ShouldBe(TracingConstants.ExceptionEventName);
		actualEvent.Tags.ShouldContain(new KeyValuePair<string, object?>(TelemetryAttributes.ExceptionType, actualException.GetType().FullName));

		actualEvent.Tags.ShouldContain(new KeyValuePair<string, object?>(TelemetryAttributes.ExceptionMessage, actualException.Message));

		actualEvent.Tags.Any(x => x.Key == TelemetryAttributes.ExceptionStacktrace).ShouldBeTrue();
	}

	public void AssertSubscriptionActivityHasExpectedTags(
		Activity activity,
		string stream,
		string eventId,
		string? consumerGroupName = null
	) {
		activity.DisplayName.ShouldBe($"{TracingConstants.Operations.Process} {stream}");
		activity.Kind.ShouldBe(ActivityKind.Consumer);

		var expectedTags = new Dictionary<string, string?> {
			{ TelemetryAttributes.MessagingSystem, TracingConstants.SystemName },
			{ TelemetryAttributes.MessagingOperationName, TracingConstants.Operations.Process },
			{ TelemetryAttributes.MessagingOperationType, TracingConstants.Operations.Process },
			{ TelemetryAttributes.MessagingDestinationName, stream },
			{ TelemetryAttributes.MessagingMessageId, eventId },
			{ TrogonTelemetryAttributes.EventType, TestEventType }
		};

		if (consumerGroupName != null)
			expectedTags[TelemetryAttributes.MessagingConsumerGroupName] = consumerGroupName;

		foreach (var tag in expectedTags) {
			activity.Tags.ShouldContain(tag);
		}
	}
}
