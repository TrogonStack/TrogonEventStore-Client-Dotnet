using TrogonEventStore.Client.Compatibility;

namespace KurrentDB.Client.Tests.Compatibility;

public class CompatibilityOptionsTests {
	[Fact]
	public void loads_required_environment() {
		var environment = ValidEnvironment();

		var options = CompatibilityOptions.Load(environment.GetValueOrDefault);

		options.ServerUri.ShouldBe(new Uri("esdb://admin:changeit@localhost:2113?tls=false"));
		options.RunId.ShouldBe("run-1");
		options.OtlpEndpoint.ShouldBe(new Uri("http://localhost:4317"));
		options.ReadyFile.ShouldBeNull();
	}

	[Fact]
	public void loads_optional_ready_file() {
		var environment = ValidEnvironment();
		var path = Path.Combine(Path.GetTempPath(), "trogon-eventstore-ready");
		environment[CompatibilityContract.ReadyFileName] = path;

		var options = CompatibilityOptions.Load(environment.GetValueOrDefault);

		options.ReadyFile.ShouldBe(ReadyFilePath.Parse(path));
	}

	[Theory]
	[InlineData(CompatibilityContract.ServerUriName)]
	[InlineData(CompatibilityContract.RunIdName)]
	[InlineData(CompatibilityContract.OtlpEndpointName)]
	public void rejects_missing_environment(string name) {
		var environment = ValidEnvironment();
		environment.Remove(name);

		Should.Throw<ArgumentException>(() => CompatibilityOptions.Load(environment.GetValueOrDefault));
	}

	[Theory]
	[InlineData(CompatibilityContract.ServerUriName)]
	[InlineData(CompatibilityContract.OtlpEndpointName)]
	public void rejects_relative_uris(string name) {
		var environment = ValidEnvironment();
		environment[name] = "relative";

		Should.Throw<ArgumentException>(() => CompatibilityOptions.Load(environment.GetValueOrDefault));
	}

	[Fact]
	public void rejects_relative_ready_file() {
		var environment = ValidEnvironment();
		environment[CompatibilityContract.ReadyFileName] = "ready";

		Should.Throw<ArgumentException>(() => CompatibilityOptions.Load(environment.GetValueOrDefault));
	}

	static Dictionary<string, string> ValidEnvironment() => new() {
		[CompatibilityContract.ServerUriName] = "esdb://admin:changeit@localhost:2113?tls=false",
		[CompatibilityContract.RunIdName] = "run-1",
		[CompatibilityContract.OtlpEndpointName] = "http://localhost:4317"
	};
}
