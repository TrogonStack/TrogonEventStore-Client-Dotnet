using System.Diagnostics;
using System.Text.Json;
using KurrentDB.Client.Diagnostics;
using KurrentDB.Client.Extensions.OpenTelemetry;
using KurrentDB.Diagnostics.Telemetry;
using KurrentDB.Diagnostics.Tracing;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;

namespace KurrentDB.Client.Tests.Diagnostics;

[Trait("Category", "Target:Diagnostics")]
[Collection(DiagnosticsCollection.Name)]
public class OpenTelemetryIntegrationTests {
	static readonly TextMapPropagator TestPropagator = new CompositeTextMapPropagator([
		new TraceContextPropagator(),
		new BaggagePropagator()
	]);

	[Fact]
	public void public_registration_exports_client_activity() {
		var exportedActivities = new List<Activity>();
		using var provider = Sdk
			.CreateTracerProviderBuilder()
			.AddKurrentDBClientInstrumentation()
			.AddInMemoryExporter(exportedActivities)
			.Build();

		using (KurrentDBClientDiagnostics.ActivitySource.StartActivity("test")) { }

		provider.ForceFlush();

		var activity = exportedActivities.ShouldHaveSingleItem();
		KurrentDBClientDiagnostics.InstrumentationName.ShouldBe("TrogonEventStore.Client");
		activity.Source.Name.ShouldBe(KurrentDBClientDiagnostics.InstrumentationName);
	}

	[Fact]
	public async Task client_operation_exports_database_semantic_conventions() {
		var exportedActivities = new List<Activity>();
		using var provider = Sdk
			.CreateTracerProviderBuilder()
			.AddKurrentDBClientInstrumentation()
			.AddInMemoryExporter(exportedActivities)
			.Build();
		var tags = new ActivityTagsCollection {
			{ TelemetryAttributes.DbCollectionName, "orders" }
		};

		var result = await KurrentDBClientDiagnostics.ActivitySource.TraceClientOperation(
			static () => ValueTask.FromResult(42),
			TracingConstants.Operations.Append,
			tags
		);

		provider.ForceFlush();
		result.ShouldBe(42);
		var activity = exportedActivities.ShouldHaveSingleItem();
		activity.DisplayName.ShouldBe("append orders");
		activity.Kind.ShouldBe(ActivityKind.Client);
		activity.GetTagItem(TelemetryAttributes.DbSystemName).ShouldBe(TracingConstants.SystemName);
		activity.GetTagItem(TelemetryAttributes.DbOperationName).ShouldBe(TracingConstants.Operations.Append);
		activity.GetTagItem(TelemetryAttributes.DbCollectionName).ShouldBe("orders");
	}

	[Fact]
	public async Task operation_specific_tags_do_not_mutate_an_unobserved_caller() {
		using var source = new ActivitySource($"unobserved-{Guid.NewGuid():N}");
		using var caller = new Activity("caller").Start();

		await source.TraceClientOperation(
			activity => {
				activity?.SetTag(TelemetryAttributes.DbOperationBatchSize, 2);
				return ValueTask.FromResult(0);
			},
			TracingConstants.Operations.BatchAppend
		);

		caller.GetTagItem(TelemetryAttributes.DbOperationBatchSize).ShouldBeNull();
	}

	[Fact]
	public async Task operation_specific_tags_are_owned_by_the_client_span() {
		using var source = new ActivitySource($"observed-{Guid.NewGuid():N}");
		Activity? completedActivity = null;
		using var listener = new ActivityListener {
			ShouldListenTo = candidate => candidate == source,
			Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity => completedActivity = activity
		};
		ActivitySource.AddActivityListener(listener);
		using var caller = new Activity("caller").Start();

		await source.TraceClientOperation(
			activity => {
				activity?.SetTag(TelemetryAttributes.DbOperationBatchSize, 2);
				return ValueTask.FromResult(0);
			},
			TracingConstants.Operations.BatchAppend
		);

		completedActivity.ShouldNotBeNull()
			.GetTagItem(TelemetryAttributes.DbOperationBatchSize)
			.ShouldBe(2);
		caller.GetTagItem(TelemetryAttributes.DbOperationBatchSize).ShouldBeNull();
	}

	[Fact]
	public void configured_propagator_injects_context_into_json_metadata() {
		var originalPropagator = Propagators.DefaultTextMapPropagator;
		var originalBaggage = Baggage.Current;

		try {
			Sdk.SetDefaultTextMapPropagator(TestPropagator);
			Baggage.Current = Baggage.Create(new Dictionary<string, string> { ["tenant"] = "straw-hat" });

			using var activity = StartActivity(ActivityTraceFlags.None, "vendor=value");
			ReadOnlyMemory<byte> metadata = "{\"custom\":\"value\"}"u8.ToArray();

			var injected = metadata.InjectTracingContext(activity).ToArray();
			var extracted = Extract(injected);

			extracted.ActivityContext.TraceId.ShouldBe(activity.TraceId);
			extracted.ActivityContext.SpanId.ShouldBe(activity.SpanId);
			extracted.ActivityContext.TraceFlags.ShouldBe(ActivityTraceFlags.None);
			extracted.ActivityContext.TraceState.ShouldBe("vendor=value");
			extracted.Baggage.GetBaggage("tenant").ShouldBe("straw-hat");

			using var document = JsonDocument.Parse(injected);
			document.RootElement.GetProperty("custom").GetString().ShouldBe("value");
			document.RootElement.TryGetProperty("traceparent", out _).ShouldBeTrue();
			document.RootElement.TryGetProperty("tracestate", out _).ShouldBeTrue();
			document.RootElement.TryGetProperty("baggage", out _).ShouldBeTrue();
		} finally {
			Baggage.Current = originalBaggage;
			Sdk.SetDefaultTextMapPropagator(originalPropagator);
		}
	}

	[Fact]
	public void configured_propagator_injects_context_into_property_metadata() {
		var originalPropagator = Propagators.DefaultTextMapPropagator;
		var originalBaggage = Baggage.Current;

		try {
			Sdk.SetDefaultTextMapPropagator(TestPropagator);
			Baggage.Current = Baggage.Create(new Dictionary<string, string> { ["tenant"] = "straw-hat" });

			using var activity = StartActivity(ActivityTraceFlags.Recorded, "vendor=value");
			var metadata = new Dictionary<string, string> { ["custom"] = "value" };

			metadata.InjectTracingContext(activity);
			var extracted = TestPropagator.Extract(default, metadata, Getter);

			extracted.ActivityContext.TraceId.ShouldBe(activity.TraceId);
			extracted.ActivityContext.SpanId.ShouldBe(activity.SpanId);
			extracted.ActivityContext.TraceFlags.ShouldBe(ActivityTraceFlags.Recorded);
			extracted.ActivityContext.TraceState.ShouldBe("vendor=value");
			extracted.Baggage.GetBaggage("tenant").ShouldBe("straw-hat");
			metadata["custom"].ShouldBe("value");
		} finally {
			Baggage.Current = originalBaggage;
			Sdk.SetDefaultTextMapPropagator(originalPropagator);
		}
	}

	[Fact]
	public void configured_propagator_replaces_mixed_case_fields_in_property_metadata() {
		var originalPropagator = Propagators.DefaultTextMapPropagator;
		var originalBaggage = Baggage.Current;

		try {
			Sdk.SetDefaultTextMapPropagator(TestPropagator);
			Baggage.Current = Baggage.Create(new Dictionary<string, string> { ["tenant"] = "straw-hat" });

			using var activity = StartActivity(ActivityTraceFlags.Recorded, "vendor=value");
			var metadata = new Dictionary<string, string> {
				["TraceParent"] = "stale",
				["TraceState"] = "stale",
				["Baggage"] = "stale"
			};

			metadata.InjectTracingContext(activity);

			foreach (var name in new[] { "traceparent", "tracestate", "baggage" })
				metadata.Keys.Count(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ShouldBe(1);

			var extracted = TestPropagator.Extract(default, metadata, Getter);
			extracted.ActivityContext.TraceId.ShouldBe(activity.TraceId);
			extracted.ActivityContext.SpanId.ShouldBe(activity.SpanId);
			extracted.ActivityContext.TraceState.ShouldBe("vendor=value");
			extracted.Baggage.GetBaggage("tenant").ShouldBe("straw-hat");
		} finally {
			Baggage.Current = originalBaggage;
			Sdk.SetDefaultTextMapPropagator(originalPropagator);
		}
	}

	[Fact]
	public void configured_propagator_extracts_context_from_json_metadata() {
		var originalPropagator = Propagators.DefaultTextMapPropagator;

		try {
			Sdk.SetDefaultTextMapPropagator(TestPropagator);
			var activityContext = new ActivityContext(
				ActivityTraceId.CreateRandom(),
				ActivitySpanId.CreateRandom(),
				ActivityTraceFlags.None,
				"vendor=value"
			);
			var expected = new PropagationContext(
				activityContext,
				Baggage.Create(new Dictionary<string, string> { ["tenant"] = "straw-hat" })
			);
			var carrier = new Dictionary<string, string>();
			TestPropagator.Inject(expected, carrier, static (metadata, name, value) => metadata[name] = value);

			ReadOnlyMemory<byte> metadata = JsonSerializer.SerializeToUtf8Bytes(carrier);
			var extracted = metadata.ExtractPropagationContext();

			extracted.ActivityContext.TraceId.ShouldBe(activityContext.TraceId);
			extracted.ActivityContext.SpanId.ShouldBe(activityContext.SpanId);
			extracted.ActivityContext.TraceFlags.ShouldBe(ActivityTraceFlags.None);
			extracted.ActivityContext.TraceState.ShouldBe("vendor=value");
			extracted.ActivityContext.IsRemote.ShouldBeTrue();
			extracted.Baggage.GetBaggage("tenant").ShouldBe("straw-hat");
		} finally {
			Sdk.SetDefaultTextMapPropagator(originalPropagator);
		}
	}

	[Fact]
	public void configured_propagator_extracts_mixed_case_fields_from_json_metadata() {
		var originalPropagator = Propagators.DefaultTextMapPropagator;

		try {
			Sdk.SetDefaultTextMapPropagator(TestPropagator);
			var activityContext = new ActivityContext(
				ActivityTraceId.CreateRandom(),
				ActivitySpanId.CreateRandom(),
				ActivityTraceFlags.Recorded,
				"vendor=value"
			);
			var expected = new PropagationContext(
				activityContext,
				Baggage.Create(new Dictionary<string, string> { ["tenant"] = "straw-hat" })
			);
			var carrier = new Dictionary<string, string>();
			TestPropagator.Inject(expected, carrier, static (metadata, name, value) => metadata[name] = value);

			var mixedCaseCarrier = carrier.ToDictionary(
				pair => pair.Key switch {
					"traceparent" => "TraceParent",
					"tracestate" => "TraceState",
					"baggage" => "Baggage",
					_ => pair.Key
				},
				pair => pair.Value
			);
			ReadOnlyMemory<byte> metadata = JsonSerializer.SerializeToUtf8Bytes(mixedCaseCarrier);
			var extracted = metadata.ExtractPropagationContext();

			extracted.ActivityContext.TraceId.ShouldBe(activityContext.TraceId);
			extracted.ActivityContext.SpanId.ShouldBe(activityContext.SpanId);
			extracted.ActivityContext.TraceFlags.ShouldBe(ActivityTraceFlags.Recorded);
			extracted.ActivityContext.TraceState.ShouldBe("vendor=value");
			extracted.ActivityContext.IsRemote.ShouldBeTrue();
			extracted.Baggage.GetBaggage("tenant").ShouldBe("straw-hat");
		} finally {
			Sdk.SetDefaultTextMapPropagator(originalPropagator);
		}
	}

	static Activity StartActivity(ActivityTraceFlags traceFlags, string traceState) {
		var activity = new Activity("parent")
			.SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), traceFlags);
		activity.TraceStateString = traceState;
		return activity.Start();
	}

	static PropagationContext Extract(ReadOnlyMemory<byte> metadata) {
		using var document = JsonDocument.Parse(metadata);
		var carrier = document.RootElement
			.EnumerateObject()
			.ToDictionary(property => property.Name, property => property.Value.GetString()!);

		return TestPropagator.Extract(default, carrier, Getter);
	}

	static IEnumerable<string> Getter(Dictionary<string, string> carrier, string name) =>
		carrier.TryGetValue(name, out var value) ? [value] : [];
}
