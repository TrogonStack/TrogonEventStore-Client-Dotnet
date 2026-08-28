using System.Security.Cryptography.X509Certificates;
using KurrentDB.Client;

namespace KurrentDB.Client.Tests;

[Trait("Category", "Target:Misc")]
public class X509CertificatesTests {
	[RetryFact]
	public void create_from_pem_file() {
		const string certPemFilePath = "certs/ca/ca.crt";
		const string keyPemFilePath  = "certs/ca/ca.key";

		var rsa  = X509Certificates.CreateFromPemFile(certPemFilePath, keyPemFilePath);
		var cert = X509CertificateLoader.LoadCertificateFromFile(certPemFilePath);

		rsa.Issuer.ShouldBe(cert.Issuer);
		rsa.SerialNumber.ShouldBe(cert.SerialNumber);
	}

	[RetryFact]
	public void create_from_pem_file_() {
		const string certPemFilePath = "certs/user-admin/user-admin.crt";
		const string keyPemFilePath  = "certs/user-admin/user-admin.key";

		var rsa  = X509Certificates.CreateFromPemFile(certPemFilePath, keyPemFilePath);
		var cert = X509CertificateLoader.LoadCertificateFromFile(certPemFilePath);

		rsa.Issuer.ShouldBe(cert.Issuer);
		rsa.Subject.ShouldBe(cert.Subject);
		rsa.SerialNumber.ShouldBe(cert.SerialNumber);
	}
}
