using JetBrains.Annotations;
using KurrentDB.Client.Diagnostics;
using OpenTelemetry.Trace;

namespace KurrentDB.Client.Extensions.OpenTelemetry;

/// <summary>
/// Extension methods used to facilitate tracing instrumentation of the TrogonEventStore client.
/// </summary>
[PublicAPI]
public static class TracerProviderBuilderExtensions {
	/// <summary>
	/// Adds the TrogonEventStore client ActivitySource name to the list of subscribed sources on the <see cref="TracerProviderBuilder"/>
	/// </summary>
	/// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
	/// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain configuration.</returns>
	public static TracerProviderBuilder AddKurrentDBClientInstrumentation(this TracerProviderBuilder builder) =>
		builder.AddSource(KurrentDBClientDiagnostics.InstrumentationName);
}
