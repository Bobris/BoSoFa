using System.Reflection;

namespace BoSoFa.Architecture.Tests;

/// <summary>Project boundaries from docs/plan.md §3, checked on the compiled assemblies.</summary>
public class BoundaryTest
{
    static readonly Assembly Protocol = typeof(BoSoFa.Protocol.VersionInfo).Assembly;
    static readonly Assembly Domain = typeof(BoSoFa.Server.Domain.User).Assembly;
    static readonly Assembly Server = typeof(BoSoFa.Server.ServerApp).Assembly;
    static readonly Assembly Daemon = typeof(BoSoFa.Daemon.Cli).Assembly;

    static string[] BoSoFaReferences(Assembly assembly) => assembly.GetReferencedAssemblies()
        .Select(name => name.Name!)
        .Where(name => name.StartsWith("BoSoFa.", StringComparison.Ordinal) || name.StartsWith("bosofa", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void ProtocolReferencesNothingOfBoSoFa() => Assert.Empty(BoSoFaReferences(Protocol));

    [Fact]
    public void DaemonReferencesOnlyTheProtocol()
    {
        Assert.Equal(["BoSoFa.Protocol"], BoSoFaReferences(Daemon));
        Assert.DoesNotContain(Daemon.GetReferencedAssemblies(), name => name.Name!.StartsWith("BTDB", StringComparison.Ordinal));
    }

    [Fact]
    public void DomainDoesNotKnowTheServerHost() => Assert.Equal(["BoSoFa.Protocol"], BoSoFaReferences(Domain));

    [Fact]
    public void ServerDoesNotReferenceTheDaemon() =>
        Assert.Equal(["BoSoFa.Protocol", "BoSoFa.Server.Domain"], BoSoFaReferences(Server));
}
