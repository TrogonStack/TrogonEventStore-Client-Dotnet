using KurrentDB.Client.Extensions.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TrogonEventStore.Client.Compatibility;

internal static class CompatibilityProgram {
	const int OperationTimeoutSeconds = 30;

	public static async Task<int> RunAsync(
		IReadOnlyList<string> arguments,
		Func<string, string?> readEnvironment
	) {
		try {
			var options = CompatibilityOptions.Load(readEnvironment);
			var command = CompatibilityCommand.Parse(arguments);
			var originalPropagator = Propagators.DefaultTextMapPropagator;

			try {
				Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([
					new TraceContextPropagator(),
					new BaggagePropagator()
				]));

				using var tracerProvider = Sdk
					.CreateTracerProviderBuilder()
					.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(CompatibilityContract.ServiceName))
					.AddKurrentDBClientInstrumentation()
					.AddOtlpExporter(exporter => exporter.Endpoint = options.OtlpEndpoint)
					.Build();
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));

				await new CompatibilityApplication(options).ExecuteAsync(command, timeout.Token);
				if (!tracerProvider.ForceFlush())
					throw new InvalidOperationException("OpenTelemetry export did not flush successfully.");
			} finally {
				Sdk.SetDefaultTextMapPropagator(originalPropagator);
			}

			return 0;
		} catch (ArgumentException exception) {
			Console.Error.WriteLine(exception.Message);
			return 2;
		} catch (Exception exception) {
			Console.Error.WriteLine(exception);
			return 1;
		}
	}
}
