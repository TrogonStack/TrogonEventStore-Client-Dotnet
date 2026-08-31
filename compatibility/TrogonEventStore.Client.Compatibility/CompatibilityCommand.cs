namespace TrogonEventStore.Client.Compatibility;

internal abstract record CompatibilityCommand(StreamName Stream) {
	public sealed record Write(StreamName Stream) : CompatibilityCommand(Stream);
	public sealed record BatchWrite(StreamName Stream) : CompatibilityCommand(Stream);
	public sealed record Read(StreamName Stream) : CompatibilityCommand(Stream);
	public sealed record Subscribe(StreamName Stream) : CompatibilityCommand(Stream);
	public sealed record CreatePersistentSubscription(StreamName Stream, GroupName Group) : CompatibilityCommand(Stream);
	public sealed record ConsumePersistentSubscription(StreamName Stream, GroupName Group) : CompatibilityCommand(Stream);

	public static CompatibilityCommand Parse(IReadOnlyList<string> arguments) => arguments switch {
		["write", var stream] => new Write(StreamName.Parse(stream)),
		["batch-write", var stream] => new BatchWrite(StreamName.Parse(stream)),
		["read", var stream] => new Read(StreamName.Parse(stream)),
		["subscribe", var stream] => new Subscribe(StreamName.Parse(stream)),
		["create-persistent-subscription", var stream, var group] =>
			new CreatePersistentSubscription(StreamName.Parse(stream), GroupName.Parse(group)),
		["consume-persistent-subscription", var stream, var group] =>
			new ConsumePersistentSubscription(StreamName.Parse(stream), GroupName.Parse(group)),
		_ => throw new ArgumentException(
			"Expected write <stream>, batch-write <stream>, read <stream>, subscribe <stream>, " +
			"create-persistent-subscription <stream> <group>, or " +
			"consume-persistent-subscription <stream> <group>."
		)
	};
}

internal readonly record struct StreamName {
	StreamName(string value) => Value = value;

	public string Value { get; }

	public static StreamName Parse(string value) =>
		new(Required(value, "Stream name"));

	internal static string Required(string value, string label) =>
		!string.IsNullOrWhiteSpace(value)
			? value.Trim()
			: throw new ArgumentException($"{label} is required.");
}

internal readonly record struct GroupName {
	GroupName(string value) => Value = value;

	public string Value { get; }

	public static GroupName Parse(string value) =>
		new(StreamName.Required(value, "Group name"));
}
