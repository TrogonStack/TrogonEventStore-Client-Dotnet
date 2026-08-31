namespace TrogonEventStore.Client.Compatibility;

internal sealed record CompatibilityOptions(Uri ServerUri, string RunId, Uri OtlpEndpoint, ReadyFilePath? ReadyFile) {
	public static CompatibilityOptions Load(Func<string, string?> readEnvironment) {
		ArgumentNullException.ThrowIfNull(readEnvironment);

		var serverUri = ReadAbsoluteUri(CompatibilityContract.ServerUriName);
		var runId = ReadRequired(CompatibilityContract.RunIdName);
		var otlpEndpoint = ReadAbsoluteUri(CompatibilityContract.OtlpEndpointName);
		var readyFileValue = readEnvironment(CompatibilityContract.ReadyFileName);
		ReadyFilePath? readyFile = string.IsNullOrWhiteSpace(readyFileValue)
			? null
			: ReadyFilePath.Parse(readyFileValue);

		return new(serverUri, runId, otlpEndpoint, readyFile);

		string ReadRequired(string name) {
			var value = readEnvironment(name)?.Trim();
			return !string.IsNullOrEmpty(value)
				? value
				: throw new ArgumentException($"Environment variable {name} is required.");
		}

		Uri ReadAbsoluteUri(string name) {
			var value = ReadRequired(name);
			return Uri.TryCreate(value, UriKind.Absolute, out var uri)
				? uri
				: throw new ArgumentException($"Environment variable {name} must be an absolute URI.");
		}
	}
}

internal readonly record struct ReadyFilePath {
	static readonly byte[] ReadyContent = "ready\n"u8.ToArray();

	ReadyFilePath(string fullPath) => FullPath = fullPath;

	public string FullPath { get; }

	public static ReadyFilePath Parse(string value) {
		if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
			throw new ArgumentException($"Environment variable {CompatibilityContract.ReadyFileName} must be an absolute path.");

		return new(Path.GetFullPath(value));
	}

	public async Task Signal(CancellationToken cancellationToken) {
		await using var file = new FileStream(
			FullPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.Read,
			ReadyContent.Length,
			FileOptions.Asynchronous
		);
		await file.WriteAsync(ReadyContent, cancellationToken);
	}
}
