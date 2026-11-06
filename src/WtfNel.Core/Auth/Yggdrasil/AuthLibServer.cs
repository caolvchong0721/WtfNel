using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Auth.Yggdrasil;

/// <summary>
/// The client authlib's local auth port (launch argument -DlauncherControlPort).
/// Request: gameId(utf8) / userId(utf8) / serverId(utf16le), each field as [len:4 LE][bytes].
/// Response: uint32 LE, 0 = success. The protocol matches FandNEL's AuthLibProtocol.
/// On each request it runs <see cref="YggdrasilClient.JoinServerAsync"/> locally.
/// </summary>
public sealed class AuthLibServer : IAsyncDisposable
{
    public delegate Task<GameProfile> ProfileFactory(string gameId, int userId, CancellationToken ct);

    private readonly int _port;
    private readonly YggdrasilClient _client;
    private readonly ProfileFactory _profiles;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public AuthLibServer(int port, YggdrasilClient client, ProfileFactory profiles)
    {
        _port = port;
        _client = client;
        _profiles = profiles;
    }

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        Log.Info($"[authlib] 监听 127.0.0.1:{Port}");
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { Log.Warn("[authlib] accept 失败: " + ex.Message); continue; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            uint result = 1;
            try
            {
                var gameId = Encoding.UTF8.GetString(await ReadFieldAsync(stream, ct));
                var userId = Encoding.UTF8.GetString(await ReadFieldAsync(stream, ct));
                var serverId = Encoding.Unicode.GetString(await ReadFieldAsync(stream, ct));
                Log.Info($"[authlib] 进服认证请求 gameId={gameId} userId={userId} serverId={serverId}");

                var profile = await _profiles(gameId, int.Parse(userId), ct);
                await _client.JoinServerAsync(profile, serverId, ct);
                result = 0;
                Log.Info("[authlib] 认证成功");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Error("[authlib] 认证失败: " + ex.Message); }

            var resp = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(resp, result);
            try { await stream.WriteAsync(resp, ct); } catch { }
        }
    }

    private static async Task<byte[]> ReadFieldAsync(NetworkStream s, CancellationToken ct)
    {
        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len is < 0 or > 1 << 20) throw new InvalidDataException("authlib 字段长度无效");
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
