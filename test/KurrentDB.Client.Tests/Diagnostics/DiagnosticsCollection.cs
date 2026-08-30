namespace KurrentDB.Client.Tests.Diagnostics;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsCollection {
	public const string Name = "Diagnostics";
}
