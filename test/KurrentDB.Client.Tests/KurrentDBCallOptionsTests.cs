namespace KurrentDB.Client.Tests;

[Trait("Category", "Target:Misc")]
public class KurrentDBCallOptionsTests {
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void expired_deadline_is_always_in_the_past(long ticks) {
		var before = DateTime.UtcNow;
		var options = KurrentDBCallOptions.CreateNonStreaming(
			new KurrentDBClientSettings(),
			TimeSpan.FromTicks(ticks),
			null,
			default
		);

		options.Deadline.ShouldNotBeNull();
		options.Deadline.Value.ShouldBeLessThan(before);
	}

	[Fact]
	public void infinite_deadline_remains_infinite() {
		var options = KurrentDBCallOptions.CreateNonStreaming(
			new KurrentDBClientSettings(),
			Timeout.InfiniteTimeSpan,
			null,
			default
		);

		var deadline = options.Deadline.GetValueOrDefault();
		deadline.ShouldBe(DateTime.MaxValue);
		deadline.Kind.ShouldBe(DateTimeKind.Utc);
	}

	[Fact]
	public void expired_batch_append_deadline_is_always_in_the_past() {
		var before = DateTime.UtcNow;
		var options = KurrentDB.Protocol.Streams.V1.BatchAppendReq.Types.Options.Create(
			"stream",
			StreamState.StreamRevision(0),
			TimeSpan.Zero
		);

		options.Deadline21100.ToDateTime().ShouldBeLessThan(before);
	}

	[Theory]
	[InlineData(-10000L)]
	[InlineData(long.MaxValue)]
	public void infinite_batch_append_deadline_is_utc(long ticks) {
		var timeout = TimeSpan.FromTicks(ticks);
		var options = new[] {
			KurrentDB.Protocol.Streams.V1.BatchAppendReq.Types.Options.Create(
				"stream",
				StreamState.StreamRevision(0),
				timeout
			),
			KurrentDB.Protocol.Streams.V1.BatchAppendReq.Types.Options.Create(
				"stream",
				StreamState.Any,
				timeout
			)
		};

		Assert.All(
			options,
			option => {
				option.Deadline21100.ToDateTime().ShouldBe(DateTime.MaxValue);
				option.Deadline21100.ToDateTime().Kind.ShouldBe(DateTimeKind.Utc);
			}
		);
	}
}
