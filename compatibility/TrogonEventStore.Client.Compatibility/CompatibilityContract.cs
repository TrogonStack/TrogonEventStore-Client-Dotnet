namespace TrogonEventStore.Client.Compatibility;

internal static class CompatibilityContract {
	public const string EventType = "trogon-compatibility";
	public const string Producer = "dotnet";
	public const string ServiceName = "trogon-eventstore-client-dotnet";
	public const string ServerUriName = "TROGON_EVENTSTORE_URI";
	public const string RunIdName = "TROGON_EVENTSTORE_RUN_ID";
	public const string OtlpEndpointName = "OTEL_EXPORTER_OTLP_ENDPOINT";
	public const string ReadyFileName = "TROGON_EVENTSTORE_READY_FILE";
}
