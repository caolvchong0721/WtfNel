using System.Net;
using System.Net.NetworkInformation;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Injection;

/// <summary>
/// Finds the port the proxy payload actually listens on by diffing the kernel's TCP listener table
/// before and after injection, instead of trusting a hand-off file the payload may never write.
/// </summary>
public static class ProxyPortProbe
{
    public const int BasePort = 13337;
    public const int Span = 32;

    public static HashSet<int> Snapshot() => Listening();

    public static int? WaitForNew(HashSet<int> before, int timeoutMs = 15000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (int port in Listening())
            {
                if (port < BasePort || port >= BasePort + Span) continue;
                if (before.Contains(port)) continue;
                return port;
            }

            Thread.Sleep(250);
        }

        return null;
    }

    public static bool IsListening(int port) => Listening().Contains(port);

    private static HashSet<int> Listening()
    {
        var ports = new HashSet<int>();
        try
        {
            foreach (IPEndPoint endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Address.Equals(IPAddress.Any)
                    || endpoint.Address.Equals(IPAddress.IPv6Any)
                    || IPAddress.IsLoopback(endpoint.Address))
                    ports.Add(endpoint.Port);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[proxy] 枚举监听端口失败：" + ex.GetBaseException().Message);
        }

        return ports;
    }
}
