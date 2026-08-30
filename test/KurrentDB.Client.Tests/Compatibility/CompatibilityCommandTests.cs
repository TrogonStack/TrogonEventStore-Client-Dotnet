using TrogonEventStore.Client.Compatibility;

namespace KurrentDB.Client.Tests.Compatibility;

public class CompatibilityCommandTests {
	[Theory]
	[InlineData("write")]
	[InlineData("batch-write")]
	[InlineData("read")]
	[InlineData("subscribe")]
	public void parses_stream_commands(string command) {
		var parsed = CompatibilityCommand.Parse([command, "compatibility-stream"]);

		parsed.Stream.Value.ShouldBe("compatibility-stream");
	}

	[Fact]
	public void parses_batch_write_as_a_distinct_command() =>
		CompatibilityCommand.Parse(["batch-write", "compatibility-stream"])
			.ShouldBeOfType<CompatibilityCommand.BatchWrite>();

	[Theory]
	[InlineData("create-persistent-subscription")]
	[InlineData("consume-persistent-subscription")]
	public void parses_persistent_subscription_commands(string command) {
		var parsed = CompatibilityCommand.Parse([command, "compatibility-stream", "compatibility-group"]);

		parsed.Stream.Value.ShouldBe("compatibility-stream");
		var group = parsed switch {
			CompatibilityCommand.CreatePersistentSubscription create => create.Group,
			CompatibilityCommand.ConsumePersistentSubscription consume => consume.Group,
			_ => throw new InvalidOperationException()
		};
		group.Value.ShouldBe("compatibility-group");
	}

	[Theory]
	[InlineData()]
	[InlineData("write")]
	[InlineData("write", "")]
	[InlineData("batch-write")]
	[InlineData("create-persistent-subscription", "stream")]
	[InlineData("unknown", "stream")]
	public void rejects_invalid_commands(params string[] arguments) =>
		Should.Throw<ArgumentException>(() => CompatibilityCommand.Parse(arguments));

	[Fact]
	public void command_error_describes_batch_write() =>
		Should.Throw<ArgumentException>(() => CompatibilityCommand.Parse([]))
			.Message.ShouldContain("batch-write <stream>");
}
