using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;

namespace WtfNel.Core.Auth.Yggdrasil;

/// <summary>Auth server version parameters. CrcSalt is bound to the launcher version.</summary>
public sealed record YggdrasilData(string LauncherVersion, string Channel, string CrcSalt);

/// <summary>The game-side information needed for one join-server authentication.</summary>
public sealed class GameProfile
{
    public required string GameId { get; init; }
    public required string GameVersion { get; init; }
    public required string BootstrapMd5 { get; init; }
    public required string DatFileMd5 { get; init; }
    /// <summary>Serialized {"mods":[...]}, which must match what the API returns.</summary>
    public required string ModInfo { get; init; }
    public required int UserId { get; init; }
    public required string UserToken { get; init; }

    private static readonly byte[] TokenKey = [172, 36, 156, 105, 199, 44, 179, 180, 78, 192, 204, 108, 84, 58, 129, 149];

    public int AuthId => Skip32.Encrypt(UserId, "SaintSteve"u8.ToArray());

    public byte[] AuthToken
    {
        get
        {
            var raw = Encoding.ASCII.GetBytes(UserToken);
            if (raw.Length != 16) throw new InvalidOperationException("userToken 必须是 16 个 ASCII 字符");
            return raw.Xor(TokenKey);
        }
    }
}

/// <summary>Local generation of the handshake and join packets, ported from Codexus.OpenSDK.Generator.YggdrasilGenerator.</summary>
public sealed class YggdrasilGenerator(YggdrasilData data)
{
    private const string PrcCheck = "[]";

    private static readonly byte[] McVersionSalt = [1, 0, 4, 128, 210, 62, 247, 17, 1];
    private static readonly byte[] TcpSalt = [47, 132, 174, 163, 153, 33, 41, 38, 218, 191, 149, 163, 171, 175, 55, 224];

    private static readonly AsymmetricKeyParameter PublicKey = Rsa.LoadPublicKey("MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA4HJFrYdVTeoSvH6qsnJElfXuf7FnxxFQdz3gRCs66LDrZfaoGoWt2e/aGIOv8uGHliBWnZP42Ike9Qf5aiYVtQ4mlj2bXZjifHG35LlS1Bq6yCA6k1WevWcrGWOuLzny3jo8Wbdi0lIFMTT2hN98sF2k4YcvyE9zhqxfRNFGVI5kLyxm9CeTKAXGBU5mw3yQWJ8cPRR4866jpGGOhBWlJdilWt2NES9bid8SbhTT55wqumnVO5J5/DaMyTgKIQngH7NyZQljAhdK5I23dzpGop322n2eQ+mTNLuquwU453o1cbyQobgC6vh5/F1QT2INBR2qYCnRzzJ6hrhE5kIMZwIDAQAB");

    private static readonly AsymmetricKeyParameter PrivateKey = Rsa.LoadPrivateKey("MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQDobJGIddxcs08xzTIVFanc4/84J1DbxiW7wLokIrap3txzyyMXj+AcDa8jLopLJkMg9rZLzL50Dwp+hmTTgiIcjkM1DVREsRBltzqjVNyiYPL2VGHLn+/eEivjhLNWUMcWrlAoJ5JJBi22oGPLlKDVJpg33JPI5nPZpwufdB0ecn2V0CAeeGyswQyAaqIjoiYOoP3HipjEYGQHp1RsADf4ozGRgv+2HGiSOyhlv00ixnF0nRUTzVh18ka0N3LdLMpAN/YAPkO8tmoWp0asyU3X+nyZFd29povvrRy4rL1lYmo8jdpfpSL+Yk+9+RybjBAhXRx6uDoBxaa2kwE4fyrrAgMBAAECggEAWT42szruHfoLkofDjyz+R/6TZLBT788pdeoOjwl1McyMwTlihA2Oc7cdZFjeaPSMGgAhBwHarx2HXgWkeUIibuyBCcHQdX+3WBb+wPA4t3CaWdMUqecDZzV6/KVbZu0lRKQxyvlGxhtFOjZjmyu6hZ2IHQrpA97Y5N2rLNKcy69W+QYJZappmBfbVgWM0NRmgmpg6siQ0Cm6Ryil3SBAPBVv+EUPiD9jdXbtVq7VwN4YmwUGScp2Fib1oUnqEAja1hfihVnRFQ246nKXhIc/YVrNmwBrxAVwaaPFRka6XjKkSF0WVbpqQLXhbY1fS8FoXGpVhiF6o4rTQJbpQxhQgQKBgQD7XysfPNt9G63gTrkZvjvEk5LKsRG42MAYEkuzxEal9E/AQv0jJrk0f1WO09hCcYQeaOQXhM9mezNQv5jXEnmepXqM3NTw1Di5yh3uvjSXQjdUt+7haNw+QjggBqZxyQZjtadYairSzfmWe7OwJIkCmdgaJKxm4qMExk9kUgApGwKBgQDstBdJHU/KEBqVpsIlu185vFFuaAvxiHjXHqGwytJMQ/5aVqaphIiQCAxaEogPSzPSm28UHVQiZeFO059EKOpSJscW4pV95Dr5BAbHuYecacqnZKbQqb69//Cfpne9tGYXlmP6QnYfPoc4wYfTfPyU2x3KtDhVxtEDutpSNU1ycQKBgBabWn92s66uvJZ9vfvotetZ8ku0XQmoxK3lh1Vlg40NSdbar3Vn2CQ2h3VO7BYdq2oouMq8sQJgdh7+/DnreXChJUJh4ey+yVM8MDD2fjhURjGiUSOIkLYwsmd+8Z0uHRr+jUxQUAWhbJ7yBRkEUCYhu+OuBKtEGrElPKKjFUydAoGAQLj9pQBe0OGWY1U1wRt67k6P9aB9o42tfSTjEXRkDHaLFiibab7TmI6a0gY/Le9iPDREKzvZxY4WDXfQFNMbP1tbFObf+Yxuk6iGMhaI/jvvLdZXxrajcVCKex0JoNWzFMAKlmOV6PUwBFTmzu1eI1XGz6Z3wPycKmjtSY1JoAECgYBBOfaUDMaG1xLzv+q1jPPs2U4lXPK2BXFE5RaliUGC+LIQREXPishII2LYFW3gtXj5QWfIGq6x0d6ca6Bja2vYRDDe5tlT/2VbZahiHpb2PYL/2WgeoHl7sT9Bb/nsKyo85Sv+doop6huy4+aeTiQHgrGR9JYMVBSIx6P8Tt5phA==");

    public YggdrasilData Data { get; } = data;

    /// <summary>Handshake: authId + AES(loginSeed) + version/channel + RSA signature + game version. Returns the full frame with a 2-byte length prefix.</summary>
    public byte[] GenerateInitializeMessage(GameProfile profile, byte[] loginSeed, byte[] signContent)
    {
        int authId = profile.AuthId;
        byte[] seed = AesEcbNoPadding(loginSeed, profile.AuthToken);
        byte[] signHash = SHA256.HashData(BuildSign(profile, authId, seed));

        byte[] plain = Rsa.Pkcs1(PublicKey, signContent, forEncryption: false);
        if (plain.Length < 51) throw new InvalidDataException("签名内容长度无效");
        byte[] clientKey = plain[..19];
        if (!plain.AsSpan(19, 32).SequenceEqual(SHA256.HashData(loginSeed)))
            throw new InvalidDataException("服务端签名校验失败");
        byte[] sign = Rsa.Pkcs1(PrivateKey, clientKey.Concat(signHash), forEncryption: true);

        using var body = new MemoryStream();
        body.WriteInt(authId);
        body.Write(seed);
        body.WriteShortString(Data.LauncherVersion, littleEndian: false);
        body.WriteByteString(Data.Channel);
        body.Write(TcpSalt);
        body.WriteShortBytes(sign);
        body.WriteByteString(profile.GameVersion);
        body.Write(McVersionSalt);

        using var frame = new MemoryStream();
        frame.WriteShort((int)body.Length);
        body.WriteTo(frame);
        return frame.ToArray();
    }

    /// <summary>The join message (the plaintext payload of a type-9 packet).</summary>
    public byte[] GenerateJoinMessage(GameProfile profile, string serverId, byte[] loginSeed)
    {
        int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var ms = new MemoryStream();
        ms.WriteLong(long.Parse(profile.GameId));
        ms.WriteByteString(serverId);
        ms.WriteByteString(Data.LauncherVersion);
        ms.WriteByteString(profile.GameVersion);
        ms.WriteInt(now);
        ms.Write(BuildHashData(profile, now, loginSeed));
        ms.WriteShortString(profile.ModInfo);
        ms.WriteShortString(PrcCheck);
        ms.WriteShort(0);
        ms.WriteByteString(Data.Channel);
        return ms.ToArray();
    }

    private byte[] BuildSign(GameProfile profile, int authId, byte[] seed)
    {
        using var ms = new MemoryStream();
        ms.WriteInt(authId);
        ms.Write(seed);
        ms.Write(Encoding.UTF8.GetBytes(Data.LauncherVersion));
        ms.Write(Encoding.UTF8.GetBytes(Data.Channel));
        ms.Write(Encoding.UTF8.GetBytes(Data.CrcSalt));
        ms.Write(Encoding.UTF8.GetBytes(profile.GameVersion));
        ms.Write(McVersionSalt);
        return ms.ToArray();
    }

    private byte[] BuildHashData(GameProfile profile, int time, byte[] loginSeed)
    {
        string s = $"{Data.LauncherVersion}{profile.GameVersion}{time}{Data.CrcSalt}{profile.ModInfo}{profile.BootstrapMd5}{profile.DatFileMd5}{PrcCheck}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(s).Concat(BitConverter.GetBytes(profile.UserId)).Concat(loginSeed));
    }

    private static byte[] AesEcbNoPadding(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, PaddingMode.None);
    }
}
