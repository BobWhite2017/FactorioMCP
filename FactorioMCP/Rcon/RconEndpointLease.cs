using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FactorioMCP.Rcon;

/// <summary>One local-user MCP controller per resolved RCON endpoint, released by the OS on exit.</summary>
internal sealed class RconEndpointLease(FileStream handle) : IDisposable
{
    internal static RconEndpointLease Acquire(string host, int port, string? directory = null)
    {
        var addresses = Dns.GetHostAddresses(host.Trim().Trim('[', ']'));
        // A dual-stack hostname must not let IPv4 and IPv6 clients claim different leases.
        var keys = addresses.Select(address => IPAddress.IsLoopback(address) ? "loopback"
            : address.MapToIPv6().ToString()).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
            throw new InvalidOperationException($"Cannot resolve RCON host '{host}'.");

        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FactorioMCP", "locks");
        Directory.CreateDirectory(directory);
        var handles = new List<FileStream>();
        try
        {
            foreach (var key in keys)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}:{port}")));
                handles.Add(new FileStream(Path.Combine(directory, hash + ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None));
            }
            return new RconEndpointLease(handles[0]) { _additionalHandles = handles.Skip(1).ToArray() };
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
        {
            foreach (var stream in handles) stream.Dispose();
            throw new InvalidOperationException(
                $"Another local FactorioMCP instance already controls RCON endpoint {host}:{port}. " +
                "Use one controlling MCP client per endpoint. Close the other MCP connection before retrying; " +
                "this instance has not connected or initialized shared bridge state.", ex);
        }
        catch
        {
            foreach (var stream in handles) stream.Dispose();
            throw;
        }
    }

    private FileStream[] _additionalHandles = [];

    public void Dispose()
    {
        handle.Dispose();
        foreach (var stream in _additionalHandles) stream.Dispose();
    }
}
