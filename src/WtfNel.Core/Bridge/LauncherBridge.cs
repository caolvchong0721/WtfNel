using System.Net;
using System.Net.Sockets;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Bridge;

/// <summary>
/// Local bridge endpoint. After launch the game connects here for state sync; as long as this link
/// stays alive the game will not exit on its own. This implementation does three things: accept the
/// connection, read messages frame by frame, and reply to each frame to keep the UI enabled.
/// </summary>
public sealed class LauncherBridge : IAsyncDisposable
{
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _connSeq;

    /// <summary>The join target pushed once login completes (speaking on the launcher's behalf).</summary>
    private readonly (string Host, int Port, string Name)? _autoConnect;

    public LauncherBridge(int port) => _port = port;

    public LauncherBridge(int port, (string Host, int Port, string Name)? autoConnect = null)
    {
        _port = port;
        _autoConnect = autoConnect;
    }

    public async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Log.Info($"[bridge] 监听 127.0.0.1:{_port}");
        _ = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn("[bridge] accept 失败: " + ex.Message); continue; }

            int id = Interlocked.Increment(ref _connSeq);
            Log.Info($"[bridge] 接入 #{id} <- {client.Client.RemoteEndPoint}");
            _ = Task.Run(() => SessionAsync(client, id, ct), ct);
        }
    }

    private async Task SessionAsync(TcpClient client, int id, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            int pushed = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var payload = await Frame.ReadAsync(stream, ct);
                    if (payload is null)
                    {
                        Log.Info($"[bridge] #{id} 对端关闭");
                        return;
                    }

                    int smid = Frame.MessageId(payload);
                    switch (smid)
                    {
                        case MessageIds.KeepAlive:
                        case MessageIds.StateQuery:
                            if (pushed == 0 && _autoConnect is { } target)
                            {
                                pushed = 1;
                                var ok = await PushConnectAsync(client, target.Host, target.Port, target.Name, ct);
                                Log.Info($"[bridge] #{id} -> 回 1799 进服 {target.Host}:{target.Port} ok={ok}");
                                _ = RepushAsync(client, id, ct);
                            }
                            else
                            {
                                await ReplyAsync(stream, payload, ct);
                            }
                            break;
                        default:
                            await ReplyAsync(stream, payload, ct);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { Log.Info($"[bridge] #{id} 连接中断"); }
            catch (Exception ex) { Log.Warn($"[bridge] #{id} 异常: {ex.GetType().Name} {ex.Message}"); }
        }
        Log.Info($"[bridge] #{id} 结束");
    }

    /// <summary>Replies to a frame with the same message id and the original payload (currently an echo ack).</summary>
    private static async Task ReplyAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        try { await Frame.WriteAsync(stream, payload, ct); }
        catch (Exception ex) { Log.Warn("[bridge] 应答失败: " + ex.Message); }
    }

    /// <summary>Pushes the join command once more after a short delay, covering the case where the first push
    /// arrives before the client reaches a state where it can act on it.</summary>
    private async Task RepushAsync(TcpClient client, int id, CancellationToken ct)
    {
        if (_autoConnect is not { } target) return;
        try
        {
            await Task.Delay(3500, ct);
            if (!client.Connected) return;
            var ok = await PushConnectAsync(client, target.Host, target.Port, target.Name, ct);
            Log.Info($"[bridge] #{id} -> 1799 重推 {target.Host}:{target.Port} ok={ok}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug("[bridge] 重推失败: " + ex.Message); }
    }

    /// <summary>Pushes a "join server" command to the connection.</summary>
    public async Task<bool> PushConnectAsync(TcpClient client, string host, int port, string name, CancellationToken ct)
    {
        try
        {
            var body = new List<byte>();
            body.Add((byte)(MessageIds.ConnectServer >> 8));
            body.Add((byte)(MessageIds.ConnectServer & 0xFF));
            AppendString(body, host);
            AppendInt32(body, port);
            AppendString(body, name);
            await Frame.WriteAsync(client.GetStream(), body.ToArray(), ct);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("[bridge] 推送失败: " + ex.Message);
            return false;
        }
    }

    private static void AppendInt32(List<byte> b, int v)
    {
        b.Add((byte)(v & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF));
        b.Add((byte)((v >> 24) & 0xFF));
    }

    private static void AppendString(List<byte> b, string s)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(s);
        b.Add((byte)(raw.Length & 0xFF));
        b.Add((byte)((raw.Length >> 8) & 0xFF));
        b.AddRange(raw);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
