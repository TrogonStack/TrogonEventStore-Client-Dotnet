// ReSharper disable CheckNamespace

using System.Diagnostics;
using System.Runtime.CompilerServices;
using KurrentDB.Diagnostics.Telemetry;

using static KurrentDB.Diagnostics.Tracing.TracingConstants;

namespace KurrentDB.Diagnostics;

static class ActivityExtensions {
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Activity StatusOk(this Activity activity, string? description = null) =>
		activity.SetActivityStatus(ActivityStatus.Ok(description));

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Activity StatusError(this Activity activity, Exception exception) =>
		activity.SetActivityStatus(ActivityStatus.Error(exception));

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Activity RecordException(this Activity activity, Exception? exception) {
		if (exception is null)
			return activity;

		var ex = exception is AggregateException aex ? aex.Flatten() : exception;

		var tags = new ActivityTagsCollection {
			{ TelemetryAttributes.ExceptionType, ex.GetType().FullName },
			{ TelemetryAttributes.ExceptionStacktrace, ex.ToInvariantString() }
		};

		if (!string.IsNullOrWhiteSpace(exception.Message))
			tags.Add(TelemetryAttributes.ExceptionMessage, ex.Message);

		activity.AddEvent(new ActivityEvent(ExceptionEventName, default, tags));

		return activity;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Activity SetActivityStatus(this Activity activity, ActivityStatus status) {
		activity.SetStatus(status.StatusCode, status.Description);

		if (status.Exception is { } exception)
			activity.SetTag(TelemetryAttributes.ErrorType, exception.GetType().FullName);

		return activity.IsAllDataRequested ? activity.RecordException(status.Exception) : activity;
	}
}
