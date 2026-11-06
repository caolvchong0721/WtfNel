using System.Net;
using System.Text;

namespace WtfNel.Core.Bridge;

/// <summary>
/// Frame format on the local bridge port: a 2-byte little-endian length followed by the payload.
/// The first two bytes of the payload are the big-endian message id.
/// </summary>
public static class Frame
{
    public const int HeaderSize = 2;
    public const int MaxPayload = 1 << 20;

    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[2];
        if (!await ReadExactAsync(s, head, ct)) return null;
        int len = head[0] | (head[1] << 8);
        if (len <= 0 || len > MaxPayload) return null;
        var payload = new byte[len];
        if (!await ReadExactAsync(s, payload, ct)) return null;
        return payload;
    }

    public static async Task WriteAsync(Stream s, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var head = new byte[2];
        head[0] = (byte)(payload.Length & 0xFF);
        head[1] = (byte)((payload.Length >> 8) & 0xFF);
        await s.WriteAsync(head, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    public static int MessageId(ReadOnlySpan<byte> payload) =>
        payload.Length < 2 ? -1 : (payload[0] << 8) | payload[1];

    public static ushort ReadU16(ReadOnlySpan<byte> b, int off) => (ushort)((b[off + 1] << 8) | b[off]);
    public static uint ReadU32(ReadOnlySpan<byte> b, int off) =>
        (uint)((b[off + 3] << 24) | (b[off + 2] << 16) | (b[off + 1] << 8) | b[off]);

    public static string HexDump(ReadOnlySpan<byte> data, int max = 64)
    {
        var sb = new StringBuilder();
        int n = Math.Min(data.Length, max);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2")).Append(' ');
        if (data.Length > max) sb.Append("... (").Append(data.Length).Append("B)");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Known message ids, named after the numbers observed in runtime logs.</summary>
public static class MessageIds
{
    public const int Login = 0;
    public const int Ping = 0x1202;
    public const int KeepAlive = 0x0501;
    public const int ConnectServer = 0x0707;
    public const int TokenUpdate = 5121;
    public const int StateQuery = 2050;
}
