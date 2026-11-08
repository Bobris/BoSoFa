namespace BoSoFa.Server.Tests;

/// <summary>A private Azurite for the cluster tests (see <see cref="DevCluster.Azurite"/>).</summary>
public sealed class Azurite : IAsyncLifetime
{
    readonly DevCluster.Azurite _azurite = new();

    public string ConnectionString => _azurite.ConnectionString;

    public Task InitializeAsync() => _azurite.StartAsync();

    public async Task DisposeAsync() => await _azurite.DisposeAsync();
}
