using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using TrogonEventStore.Client.Compatibility;

namespace KurrentDB.Client.Tests.Compatibility;

public class CompatibilityApplicationTests {
	[Fact]
	public async Task write_uses_credentials_from_the_server_uri() {
		using var certificate = CreateCertificate();
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.ConfigureKestrel(options => options.Listen(
			IPAddress.Loopback,
			0,
			listen => {
				listen.Protocols = HttpProtocols.Http2;
				listen.UseHttps(certificate);
			}
		));

		var application = builder.Build();
		string? authorizationHeader = null;

		application.MapPost(
			"/event_store.client.server_features.ServerFeatures/GetSupportedMethods",
			context => CompleteGrpcResponse(context, ReadOnlyMemory<byte>.Empty)
		);
		application.MapPost(
			"/event_store.client.streams.Streams/Append",
			async context => {
				authorizationHeader = context.Request.Headers.Authorization;
				await context.Request.Body.CopyToAsync(Stream.Null);
				await CompleteGrpcResponse(context, new byte[] { 0x0a, 0x00 });
			}
		);

		await application.StartAsync();

		try {
			var server = application.Services.GetRequiredService<IServer>();
			var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
			var port = new Uri(address).Port;
			var options = new CompatibilityOptions(
				new($"esdb://writer:secret@127.0.0.1:{port}?tls=true&tlsVerifyCert=false"),
				"run-1",
				new("http://127.0.0.1:4317"),
				null
			);

			await new CompatibilityApplication(options).ExecuteAsync(
				CompatibilityCommand.Parse(["write", "compatibility-stream"]),
				CancellationToken.None
			);

			var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("writer:secret"));
			authorizationHeader.ShouldBe($"Basic {encodedCredentials}");
		} finally {
			await application.StopAsync();
			await application.DisposeAsync();
		}
	}

	static X509Certificate2 CreateCertificate() {
		using var key = RSA.Create(2048);
		var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
	}

	static async Task CompleteGrpcResponse(HttpContext context, ReadOnlyMemory<byte> payload) {
		context.Response.ContentType = "application/grpc";
		context.Response.DeclareTrailer("grpc-status");

		var header = new byte[5];
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
		await context.Response.Body.WriteAsync(header);
		await context.Response.Body.WriteAsync(payload);
		context.Response.AppendTrailer("grpc-status", "0");
	}
}
