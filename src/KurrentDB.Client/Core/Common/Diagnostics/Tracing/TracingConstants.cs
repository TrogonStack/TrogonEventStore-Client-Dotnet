// ReSharper disable CheckNamespace

namespace KurrentDB.Diagnostics.Tracing;

static class TracingConstants {
	public const string SystemName = "trogoneventstore";
	public const string ExceptionEventName = "exception";

	public static class Operations {
		public const string Append = "append";
		public const string BatchAppend = "batch_append";
		public const string Process = "process";
	}
}
