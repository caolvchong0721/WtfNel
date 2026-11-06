using System.Net;
using System.Net.Sockets;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Proxy;

/// <summary>
/// Forwarding layer between the game and the server. The game connects to a local port and this process
/// relays the byte stream verbatim to the real server. Alongside, a packet sequence is parsed for
/// observation only; a parse failure just disables observation and never affects forwarding.
/// </summary>
public sealed class GameRelay : IAsyncDisposable
{
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly int _localPort;
    private readonly bool _verbose;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _connSeq;

    public GameRelay(string remoteHost, int remotePort, int localPort, bool verbose)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _localPort = localPort;
        _verbose = verbose;
    }

    public async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, _localPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Log.Info($"[relay] 本地 127.0.0.1:{_localPort} -> {_remoteHost}:{_remotePort}");
        _ = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient local;
            try { local = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn("[relay] accept 失败: " + ex.Message); continue; }

            int id = Interlocked.Increment(ref _connSeq);
            _ = Task.Run(() => SessionAsync(local, id, ct), ct);
        }
    }

    private async Task SessionAsync(TcpClient local, int id, CancellationToken ct)
    {
        using (local)
        using (var remote = new TcpClient())
        {
            local.NoDelay = true;
            remote.NoDelay = true;
            try
            {
                await remote.ConnectAsync(_remoteHost, _remotePort, ct);
            }
            catch (Exception ex)
            {
                Log.Warn($"[relay] #{id} 连接目标失败: {ex.Message}");
                return;
            }

            Log.Info($"[relay] #{id} 建立 {local.Client.RemoteEndPoint} <-> {_remoteHost}:{_remotePort}");

            var tapC2S = new PacketTap("C2S", id, _verbose);
            var tapS2C = new PacketTap("S2C", id, _verbose);

            var up = PumpAsync(local.GetStream(), remote.GetStream(), tapC2S, ct);
            var down = PumpAsync(remote.GetStream(), local.GetStream(), tapS2C, ct);

            await Task.WhenAny(up, down);
            try { local.Close(); } catch { }
            try { remote.Close(); } catch { }
            await Task.WhenAll(up, down).ConfigureAwait(false);

            tapC2S.Summary();
            tapS2C.Summary();
            Log.Info($"[relay] #{id} 结束");
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, PacketTap tap, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await from.ReadAsync(buf, ct);
                if (n <= 0) break;
                tap.Feed(buf.AsSpan(0, n));
                await to.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Log.Warn($"[relay] 转发异常: {ex.GetType().Name} {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Passive observer: splits packets by Minecraft's varint length prefix and records their ids.
/// Read-only, never alters a byte; if the stream is encrypted and framing fails, observation disables itself.
/// </summary>
public sealed class PacketTap
{
    private readonly string _tag;
    private readonly int _session;
    private readonly bool _verbose;
    private readonly List<byte> _buf = new(64 * 1024);
    private bool _disabled;
    private int _frames;
    private readonly Dictionary<int, int> _histogram = new();

    public PacketTap(string tag, int session, bool verbose)
    {
        _tag = tag;
        _session = session;
        _verbose = verbose;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_disabled) return;
        foreach (var b in data) _buf.Add(b);

        while (true)
        {
            if (!TryReadVarInt(_buf, 0, out int length, out int lenBytes)) return;
            if (length < 0 || length > 8 * 1024 * 1024) { _disabled = true; return; }
            if (_buf.Count < lenBytes + length) return;

            int id = -1;
            if (length >= 1 && TryReadVarInt(_buf, lenBytes, out int pid, out _))
                id = pid;

            _frames++;
            if (id >= 0)
            {
                _histogram.TryGetValue(id, out int c);
                _histogram[id] = c + 1;
                if (_verbose && _frames <= 200)
                    Log.Debug($"[tap:{_tag}#{_session}] frame={_frames} id=0x{id:x2} len={length}");
            }

            _buf.RemoveRange(0, lenBytes + length);

            if (id < 0) { _disabled = true; return; }
        }
    }

    public void Summary()
    {
        if (_frames == 0) return;
        var top = _histogram.OrderByDescending(kv => kv.Value).Take(8)
            .Select(kv => $"0x{kv.Key:x2}×{kv.Value}");
        Log.Info($"[tap:{_tag}#{_session}] 观察 {_frames} 帧，主包号 {string.Join(", ", top)}");
    }

    private static bool TryReadVarInt(List<byte> buf, int off, out int value, out int bytes)
    {
        value = 0; bytes = 0;
        int num = 0;
        while (true)
        {
            if (off + num >= buf.Count) return false;
            byte b = buf[off + num];
            value |= (b & 0x7F) << (7 * num);
            num++;
            if ((b & 0x80) == 0) break;
            if (num > 5) { return false; }
        }
        bytes = num;
        return true;
    }
}
