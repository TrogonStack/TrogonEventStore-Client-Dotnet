using System.Text.Json.Serialization;

namespace TrogonEventStore.Client.Compatibility;

internal sealed record CompatibilityEvent(
	[property: JsonPropertyName("producer")] string Producer,
	[property: JsonPropertyName("runId")] string RunId
);
