# TrogonEventStore .NET Client

The .NET client for TrogonEventStore. The package targets .NET 10.

This project is derived from the Apache 2.0 licensed KurrentDB .NET client. See [LICENSE.md](LICENSE.md) for licensing and attribution. During the initial bootstrap, the public API remains under the `KurrentDB.Client` namespaces while package identity and distribution move to TrogonEventStore.

## Install from GitHub Packages

GitHub Packages requires a classic personal access token with `read:packages` permission. Fine-grained personal access tokens are not supported for NuGet authentication.

Set `GITHUB_PACKAGES_TOKEN` in your environment, then add the TrogonStack feed and credentials to your user-level NuGet configuration:

```xml
<configuration>
  <packageSources>
    <add key="TrogonStack" value="https://nuget.pkg.github.com/TrogonStack/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <TrogonStack>
      <add key="Username" value="GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </TrogonStack>
  </packageSourceCredentials>
</configuration>
```

Do not put the token directly in this file or commit a credentials-bearing NuGet configuration. Add the package to your project after configuring the feed:

```xml
<PackageReference Include="TrogonEventStore.Client" Version="VERSION" />
```

## Build

The repository builds the .NET 10 target with the .NET 10 SDK:

```console
dotnet build
```
