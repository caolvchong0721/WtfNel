using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Auth.Yggdrasil;

/// <summary>
/// NetEase join-server authentication (Yggdrasil over TCP). The flow matches FandNEL's NetEaseConnection:
/// receive the 272-byte context → send the handshake → status 0 → send the type-9 join packet over the
/// ChaCha8 channel → a leading 0 byte means success. The difference is that the handshake and join packets
/// are generated locally by <see cref="YggdrasilGenerator"/> rather than by a remote service.
/// </summary>
public sealed class YggdrasilClient(YggdrasilData data, string? fixedServer = null)
{
    private const string AuthServerListUrl = "https://x19.update.netease.com/authserver.list";
    private static readonly byte[] ChaChaNonce = "163 NetEase\n"u8.ToArray();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly YggdrasilGenerator _generator = new(data);

    public async Task JoinServerAsync(GameProfile profile, string serverId, CancellationToken ct = default)
    {
        var (host, port) = await ResolveServerAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;

        using var client = new TcpClient();
        using var _ = ct.Register(client.Dispose);
        await client.ConnectAsync(host, port, ct);
        var stream = client.GetStream();

        var context = await stream.ReadInt16FrameAsync(ct);
        if (context.Length < 272) throw new InvalidDataException($"认证上下文长度异常: {context.Length}");
        var loginSeed = context[..16];
        var signContent = context[16..272];

        await stream.WriteAsync(_generator.GenerateInitializeMessage(profile, loginSeed, signContent), ct);
        var status = await stream.ReadInt16FrameAsync(ct);
        if (status.Length < 1 || status[0] != 0)
            throw new InvalidOperationException($"认证握手被拒绝: 0x{(status.Length > 0 ? status[0] : 0xFF):X2}（多半是 CrcSalt/启动器版本/userToken 不匹配）");

        var authToken = profile.AuthToken;
        var packer = new ChaChaPacker(authToken.Concat(loginSeed), ChaChaNonce, encryption: true);
        var unpacker = new ChaChaPacker(loginSeed.Concat(authToken), ChaChaNonce, encryption: false);

        await stream.WriteAsync(packer.Pack(9, _generator.GenerateJoinMessage(profile, serverId, loginSeed)), ct);
        var (type, result) = unpacker.Unpack(await stream.ReadInt16FrameAsync(ct));
        if (type != 9 || result.Length == 0 || result[0] != 0)
            throw new InvalidOperationException($"进服认证失败: type={type} code=0x{(result.Length > 0 ? result[0] : 0xFF):X2}");
    }

    private async Task<(string Host, int Port)> ResolveServerAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(fixedServer))
        {
            var idx = fixedServer.LastIndexOf(':');
            return (fixedServer[..idx], int.Parse(fixedServer[(idx + 1)..]));
        }
        var list = await Http.GetFromJsonAsync<AuthServer[]>(AuthServerListUrl, ct);
        var ok = list?.Where(s => !string.IsNullOrWhiteSpace(s.Ip) && s.Port is > 0 and < 65536).ToArray();
        if (ok is not { Length: > 0 }) throw new InvalidDataException("认证服务器列表为空");
        var pick = ok[Random.Shared.Next(ok.Length)];
        Log.Info($"[ygg] 认证服务器 {pick.Ip}:{pick.Port}");
        return (pick.Ip, pick.Port);
    }

    private sealed record AuthServer([property: JsonPropertyName("IP")] string Ip, [property: JsonPropertyName("Port")] int Port);
}
