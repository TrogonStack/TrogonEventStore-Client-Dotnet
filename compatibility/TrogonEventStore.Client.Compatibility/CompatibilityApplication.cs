using System.Text.Json;
using KurrentDB.Client;

namespace TrogonEventStore.Client.Compatibility;

internal sealed class CompatibilityApplication(CompatibilityOptions options) {
	static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	enum AppendRpc { Standard, Batch }

	public async Task ExecuteAsync(CompatibilityCommand command, CancellationToken cancellationToken) {
		switch (command) {
			case CompatibilityCommand.Write write:
				await Write(write.Stream, AppendRpc.Standard, "write", cancellationToken);
				break;
			case CompatibilityCommand.BatchWrite write:
				await Write(write.Stream, AppendRpc.Batch, "batch-write", cancellationToken);
				break;
			case CompatibilityCommand.Read read:
				await Read(read.Stream, cancellationToken);
				break;
			case CompatibilityCommand.Subscribe subscribe:
				await Subscribe(subscribe.Stream, cancellationToken);
				break;
			case CompatibilityCommand.CreatePersistentSubscription create:
				await CreatePersistentSubscription(create.Stream, create.Group, cancellationToken);
				break;
			case CompatibilityCommand.ConsumePersistentSubscription consume:
				await ConsumePersistentSubscription(consume.Stream, consume.Group, cancellationToken);
				break;
		}
	}

	async Task Write(
		StreamName stream,
		AppendRpc appendRpc,
		string command,
		CancellationToken cancellationToken
	) {
		var settings = CreateClientSettings();
		await using var client = new KurrentDBClient(settings);
		var userCredentials = appendRpc switch {
			AppendRpc.Standard => settings.DefaultCredentials ?? throw new ArgumentException(
				$"Environment variable {CompatibilityContract.ServerUriName} must include credentials for write."
			),
			AppendRpc.Batch => null,
			_ => throw new ArgumentOutOfRangeException(nameof(appendRpc), appendRpc, null)
		};
		var payload = new CompatibilityEvent(CompatibilityContract.Producer, options.RunId);
		var eventData = new EventData(
			Uuid.NewUuid(),
			CompatibilityContract.EventType,
			JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
			"{}"u8.ToArray()
		);

		await client.AppendToStreamAsync(
			stream.Value,
			StreamState.NoStream,
			[eventData],
			userCredentials: userCredentials,
			cancellationToken: cancellationToken
		);

		WriteResult(command, stream, null, payload, eventData.EventId.ToString());
	}

	async Task Read(StreamName stream, CancellationToken cancellationToken) {
		await using var client = CreateStreamsClient();
		var result = client.ReadStreamAsync(
			Direction.Forwards,
			stream.Value,
			StreamPosition.Start,
			cancellationToken: cancellationToken
		);

		await foreach (var resolvedEvent in result.WithCancellation(cancellationToken)) {
			var payload = ReadPayload(resolvedEvent);
			if (payload is null)
				continue;

			WriteResult("read", stream, null, payload, resolvedEvent.Event.EventId.ToString());
			return;
		}

		throw MissingEvent(stream);
	}

	async Task Subscribe(StreamName stream, CancellationToken cancellationToken) {
		await using var client = CreateStreamsClient();
		await using var subscription = client.SubscribeToStream(
			stream.Value,
			FromStream.Start,
			cancellationToken: cancellationToken
		);

		await foreach (var message in subscription.Messages.WithCancellation(cancellationToken)) {
			if (message is StreamMessage.SubscriptionConfirmation) {
				await SignalReady(cancellationToken);
				continue;
			}

			if (message is not StreamMessage.Event(var resolvedEvent))
				continue;

			var payload = ReadPayload(resolvedEvent);
			if (payload is null)
				continue;

			WriteResult("subscribe", stream, null, payload, resolvedEvent.Event.EventId.ToString());
			return;
		}

		throw MissingEvent(stream);
	}

	async Task CreatePersistentSubscription(
		StreamName stream,
		GroupName group,
		CancellationToken cancellationToken
	) {
		await using var client = CreatePersistentSubscriptionsClient();
		await client.CreateToStreamAsync(
			stream.Value,
			group.Value,
			new(startFrom: StreamPosition.Start),
			cancellationToken: cancellationToken
		);

		WriteResult(
			"create-persistent-subscription",
			stream,
			group,
			new(CompatibilityContract.Producer, options.RunId),
			null
		);
	}

	async Task ConsumePersistentSubscription(
		StreamName stream,
		GroupName group,
		CancellationToken cancellationToken
	) {
		await using var client = CreatePersistentSubscriptionsClient();
		await using var subscription = client.SubscribeToStream(
			stream.Value,
			group.Value,
			cancellationToken: cancellationToken
		);

		await foreach (var message in subscription.Messages.WithCancellation(cancellationToken)) {
			if (message is PersistentSubscriptionMessage.SubscriptionConfirmation) {
				await SignalReady(cancellationToken);
				continue;
			}

			if (message is not PersistentSubscriptionMessage.Event(var resolvedEvent, _))
				continue;

			await subscription.Ack(resolvedEvent);
			var payload = ReadPayload(resolvedEvent);
			if (payload is null)
				continue;

			WriteResult(
				"consume-persistent-subscription",
				stream,
				group,
				payload,
				resolvedEvent.Event.EventId.ToString()
			);
			return;
		}

		throw MissingEvent(stream);
	}

	CompatibilityEvent? ReadPayload(ResolvedEvent resolvedEvent) {
		if (resolvedEvent.Event.EventType != CompatibilityContract.EventType)
			return null;

		var payload = JsonSerializer.Deserialize<CompatibilityEvent>(resolvedEvent.Event.Data.Span, JsonOptions)
			?? throw new InvalidDataException("Compatibility event payload is required.");

		if (payload.RunId != options.RunId)
			return null;

		if (string.IsNullOrWhiteSpace(payload.Producer))
			throw new InvalidDataException("Compatibility event producer is required.");

		return payload;
	}

	KurrentDBClientSettings CreateClientSettings() =>
		KurrentDBClientSettings.Create(options.ServerUri.OriginalString);

	KurrentDBClient CreateStreamsClient() => new(CreateClientSettings());

	KurrentDBPersistentSubscriptionsClient CreatePersistentSubscriptionsClient() =>
		new(CreateClientSettings());

	async Task SignalReady(CancellationToken cancellationToken) {
		if (options.ReadyFile is { } readyFile)
			await readyFile.Signal(cancellationToken);
	}

	void WriteResult(
		string command,
		StreamName stream,
		GroupName? group,
		CompatibilityEvent payload,
		string? eventId
	) => Console.WriteLine(JsonSerializer.Serialize(
		new CompatibilityResult(command, stream.Value, group?.Value, payload.Producer, payload.RunId, eventId),
		JsonOptions
	));

	InvalidDataException MissingEvent(StreamName stream) =>
		new($"Stream {stream.Value} does not contain a {CompatibilityContract.EventType} event for run {options.RunId}.");
}

internal sealed record CompatibilityResult(
	string Command,
	string Stream,
	string? Group,
	string Producer,
	string RunId,
	string? EventId
);
