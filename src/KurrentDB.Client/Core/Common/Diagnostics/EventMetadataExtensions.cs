using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace KurrentDB.Client.Diagnostics;

static class EventMetadataExtensions {
	public static void InjectTracingContext(this Dictionary<string, string> metadata, Activity? activity) {
		if (activity is null)
			return;

		Propagators.DefaultTextMapPropagator.Inject(
			new PropagationContext(activity.Context, Baggage.Current),
			metadata,
			static (carrier, name, value) => SetPropagationField(carrier, name, value)
		);
	}

	static void SetPropagationField(Dictionary<string, string> metadata, string name, string value) {
		while (true) {
			string? existingName = null;
			foreach (var key in metadata.Keys) {
				if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
					continue;

				existingName = key;
				break;
			}

			if (existingName is null)
				break;

			metadata.Remove(existingName);
		}

		metadata[name] = value;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ReadOnlySpan<byte> InjectTracingContext(
		this ReadOnlyMemory<byte> eventMetadata, Activity? activity
	) {
		if (activity is null)
			return eventMetadata.Span;

		var propagationMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Propagators.DefaultTextMapPropagator.Inject(
			new PropagationContext(activity.Context, Baggage.Current),
			propagationMetadata,
			static (carrier, name, value) => carrier[name] = value
		);

		return eventMetadata.InjectPropagationMetadata(propagationMetadata);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static PropagationContext ExtractPropagationContext(this ReadOnlyMemory<byte> eventMetadata) {
		if (eventMetadata.IsEmpty)
			return default;

		var reader = new Utf8JsonReader(eventMetadata.Span);
		try {
			if (!JsonDocument.TryParseValue(ref reader, out var doc))
				return default;

			using (doc) {
				if (doc.RootElement.ValueKind != JsonValueKind.Object)
					return default;

				var propagationMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (var property in doc.RootElement.EnumerateObject()) {
					if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
						propagationMetadata[property.Name] = value;
				}

				return Propagators.DefaultTextMapPropagator.Extract(
					default,
					propagationMetadata,
					static (carrier, name) =>
						carrier.TryGetValue(name, out var value) ? [value] : []
				);
			}
		} catch (Exception) {
			return default;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static ReadOnlySpan<byte> InjectPropagationMetadata(
		this ReadOnlyMemory<byte> eventMetadata, Dictionary<string, string> propagationMetadata
	) {
		if (propagationMetadata.Count == 0)
			return eventMetadata.Span;

		return eventMetadata.IsEmpty
			? JsonSerializer.SerializeToUtf8Bytes(propagationMetadata)
			: TryInjectPropagationMetadata(eventMetadata, propagationMetadata).ToArray();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static ReadOnlyMemory<byte> TryInjectPropagationMetadata(
		this ReadOnlyMemory<byte> utf8Json, Dictionary<string, string> propagationMetadata
	) {
		try {
			using var doc = JsonDocument.Parse(utf8Json);
			using var stream = new MemoryStream();
			using var writer = new Utf8JsonWriter(stream);

			if (doc.RootElement.ValueKind != JsonValueKind.Object)
				return utf8Json;

			writer.WriteStartObject();

			foreach (var property in doc.RootElement.EnumerateObject()) {
				if (!propagationMetadata.ContainsKey(property.Name))
					property.WriteTo(writer);
			}


			foreach (var (name, value) in propagationMetadata)
				writer.WriteString(name, value);

			writer.WriteEndObject();
			writer.Flush();

			return stream.ToArray();
		} catch (Exception) {
			return utf8Json;
		}
	}
}
