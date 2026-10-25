using FactorioMCP.Rcon;
using System.Net;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class RconEndpointLeaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "factorio-lease-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string> LoopbackAliases => new()
    {
        { "localhost", "127.0.0.1" },
        { "127.0.0.1", IPAddress.IPv6Loopback.ToString() },
        { IPAddress.IPv6Loopback.ToString(), $"[{IPAddress.IPv6Loopback}]" }
    };

    [Theory]
    [MemberData(nameof(LoopbackAliases))]
    public void SecondControllerForLoopbackAlias_IsRejected(string first, string second)
    {
        using var owner = RconEndpointLease.Acquire(first, 27015, _directory);

        var error = Assert.Throws<InvalidOperationException>(() =>
            RconEndpointLease.Acquire(second, 27015, _directory));

        Assert.Contains("already controls", error.Message);
        Assert.Contains("has not connected", error.Message);
    }

    [Fact]
    public void DifferentPorts_CanHaveIndependentControllers()
    {
        using var first = RconEndpointLease.Acquire("localhost", 27015, _directory);
        using var second = RconEndpointLease.Acquire("localhost", 27016, _directory);
    }

    [Fact]
    public void RconClient_DisposalReleasesControlLease()
    {
        using var first = new RconClient();
        using var second = new RconClient();
        first.AcquireControlLease("localhost", 27015, _directory);
        Assert.Throws<InvalidOperationException>(() =>
            second.AcquireControlLease("127.0.0.1", 27015, _directory));
        Assert.False(second.IsConnected);

        first.Dispose();
        second.AcquireControlLease("127.0.0.1", 27015, _directory);
    }

    [Fact]
    public void ReleasedLease_CanBeReacquiredWithoutDeletingLockFile()
    {
        RconEndpointLease.Acquire("localhost", 27015, _directory).Dispose();
        using var next = RconEndpointLease.Acquire("127.0.0.1", 27015, _directory);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.GetFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
