using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace WtfNel.Core.Auth.Yggdrasil;

/// <summary>Skip32 block cipher (used for userId → authId and the character uuid), ported from Codexus.OpenSDK.</summary>
public static class Skip32
{
    private static readonly byte[] FTable =
    [
        163, 215, 9, 131, 248, 72, 246, 244, 179, 33, 21, 120, 153, 177, 175, 249, 231, 45, 77, 138,
        206, 76, 202, 46, 82, 149, 217, 30, 78, 56, 68, 40, 10, 223, 2, 160, 23, 241, 96, 104,
        18, 183, 122, 195, 233, 250, 61, 83, 150, 132, 107, 186, 242, 99, 154, 25, 124, 174, 229, 245,
        247, 22, 106, 162, 57, 182, 123, 15, 193, 147, 129, 27, 238, 180, 26, 234, 208, 145, 47, 184,
        85, 185, 218, 133, 63, 65, 191, 224, 90, 88, 128, 95, 102, 11, 216, 144, 53, 213, 192, 167,
        51, 6, 101, 105, 69, 0, 148, 86, 109, 152, 155, 118, 151, 252, 178, 194, 176, 254, 219, 32,
        225, 235, 214, 228, 221, 71, 74, 29, 66, 237, 158, 110, 73, 60, 205, 67, 39, 210, 7, 212,
        222, 199, 103, 24, 137, 203, 48, 31, 141, 198, 143, 170, 200, 116, 220, 201, 93, 92, 49, 164,
        112, 136, 97, 44, 159, 13, 43, 135, 80, 130, 84, 100, 38, 125, 3, 64, 52, 75, 28, 115,
        209, 196, 253, 59, 204, 251, 127, 171, 230, 62, 91, 165, 173, 4, 35, 156, 20, 81, 34, 240,
        41, 121, 113, 126, 255, 140, 14, 226, 12, 239, 188, 114, 117, 111, 55, 161, 236, 211, 142, 98,
        139, 134, 16, 232, 8, 119, 17, 190, 146, 79, 36, 197, 50, 54, 157, 207, 243, 166, 187, 172,
        94, 108, 169, 19, 87, 37, 181, 227, 189, 168, 58, 1, 5, 89, 42, 70,
    ];

    private static int G(byte[] key, int k, int w)
    {
        int g1 = w >> 8, g2 = w & 0xFF;
        int g3 = FTable[g2 ^ key[4 * k % 10]] ^ g1;
        int g4 = FTable[g3 ^ key[(4 * k + 1) % 10]] ^ g2;
        int g5 = FTable[g4 ^ key[(4 * k + 2) % 10]] ^ g3;
        int g6 = FTable[g5 ^ key[(4 * k + 3) % 10]] ^ g4;
        return (g5 << 8) + g6;
    }

    public static int Encrypt(int value, byte[] key)
    {
        int wl = (((value >> 24) & 0xFF) << 8) + ((value >> 16) & 0xFF);
        int wr = (((value >> 8) & 0xFF) << 8) + (value & 0xFF);
        int k = 0;
        for (int i = 0; i < 12; i++)
        {
            wr ^= G(key, k, wl) ^ k; k++;
            wl ^= G(key, k, wr) ^ k; k++;
        }
        return ((wr >> 8) << 24) | ((wr & 0xFF) << 16) | ((wl >> 8) << 8) | (wl & 0xFF);
    }

    private static readonly byte[] RoleKey = "SaintSteve"u8.ToArray();

    /// <summary>The character uuid (--uuid): md5(roleName) with its last 4 bytes replaced by Skip32(userId), then marked as v4. Same as FandNEL's GenerateRoleUuid.</summary>
    public static string RoleUuid(string roleName, int userId)
    {
        var b = MD5.HashData(Encoding.UTF8.GetBytes(roleName));
        BitConverter.GetBytes(Encrypt(userId, RoleKey)).CopyTo(b, 12);
        b[6] = (byte)((b[6] & 0x0F) | 0x40);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return Convert.ToHexString(b);
    }
}

/// <summary>Encrypt/decrypt for the -DToken launch argument: AES-128-CBC / zero padding, plaintext = 8 random + userToken(16) + 8 random.</summary>
public static class LaunchToken
{
    private static readonly byte[] Key = "debbde3548928fab"u8.ToArray();
    private static readonly byte[] Iv = "afd4c5c5a7c456a1"u8.ToArray();

    private static Aes Create()
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.Zeros;
        aes.Key = Key;
        aes.IV = Iv;
        return aes;
    }

    public static string Encrypt(string userToken)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string rnd() => RandomNumberGenerator.GetString(chars, 8);
        var plain = Encoding.ASCII.GetBytes(rnd() + userToken + rnd());
        using var aes = Create();
        return Convert.ToHexString(aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length));
    }

    /// <summary>Recovers the 16-char userToken from -DToken.</summary>
    public static string Decrypt(string hex)
    {
        var cipher = Convert.FromHexString(hex);
        using var aes = Create();
        var plain = Encoding.ASCII.GetString(aes.CreateDecryptor().TransformFinalBlock(cipher, 0, cipher.Length)).TrimEnd('\0');
        if (plain.Length < 32) throw new InvalidDataException("-DToken 解密结果长度异常");
        return plain.Substring(8, plain.Length - 16);
    }
}

/// <summary>The user-token request-header signature for X19 HTTP APIs (ported from FandNEL's TokenUtil).</summary>
public static class HttpToken
{
    public static Dictionary<string, string> Compute(string path, string body, string userId, string userToken)
    {
        path = path.StartsWith('/') ? path : "/" + path;
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(Md5Hex(Encoding.UTF8.GetBytes(userToken))));
        ms.Write(Encoding.UTF8.GetBytes(body));
        ms.Write("0eGsBkhl"u8);
        ms.Write(Encoding.UTF8.GetBytes(path));
        string digest = Md5Hex(ms.ToArray());

        var bin = new StringBuilder();
        foreach (char c in digest) bin.Append(Convert.ToString(c, 2).PadLeft(8, '0'));
        string b = bin.ToString();
        string rotated = b[6..] + b[..6];

        var bytes = Encoding.UTF8.GetBytes(digest);
        for (int i = 0; i < rotated.Length / 8; i++)
        {
            byte mask = 0;
            for (int j = 0; j < 8; j++)
                if (rotated[i * 8 + 7 - j] == '1') mask |= (byte)(1 << j);
            bytes[i] ^= mask;
        }
        string token = (Convert.ToBase64String(bytes, 0, 12) + "1").Replace('+', 'm').Replace('/', 'o');
        return new Dictionary<string, string> { ["user-id"] = userId, ["user-token"] = token };
    }

    private static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
}

public static class Rsa
{
    public static AsymmetricKeyParameter LoadPublicKey(string b64) => PublicKeyFactory.CreateKey(Convert.FromBase64String(b64));
    public static AsymmetricKeyParameter LoadPrivateKey(string b64) => PrivateKeyFactory.CreateKey(Convert.FromBase64String(b64));

    /// <summary>Block-wise PKCS#1; private-key encrypt / public-key decrypt is the "signature" usage, matching BouncyCastle semantics.</summary>
    public static byte[] Pkcs1(AsymmetricKeyParameter key, byte[] data, bool forEncryption)
    {
        var engine = new Pkcs1Encoding(new RsaEngine());
        engine.Init(forEncryption, key);
        int block = engine.GetInputBlockSize();
        using var ms = new MemoryStream();
        for (int i = 0; i < data.Length; i += block)
        {
            var outp = engine.ProcessBlock(data, i, Math.Min(block, data.Length - i));
            ms.Write(outp);
        }
        return ms.ToArray();
    }
}

/// <summary>ChaCha8 for the auth server TCP channel (RFC7539 layout, 12-byte nonce).</summary>
public sealed class ChaChaPacker : ChaCha7539Engine
{
    public override string AlgorithmName => $"ChaCha{rounds}";

    public ChaChaPacker(byte[] key, byte[] iv, bool encryption, int rounds = 8)
    {
        this.rounds = rounds;
        Init(encryption, new ParametersWithIV(new KeyParameter(key), iv));
    }

    /// <summary>[len:2][crc32:4][type:1][0x88 x3][data]; everything after len is encrypted.</summary>
    public byte[] Pack(byte type, byte[] data)
    {
        var p = new byte[data.Length + 10];
        BitConverter.GetBytes((short)(p.Length - 2)).CopyTo(p, 0);
        p[6] = type; p[7] = 0x88; p[8] = 0x88; p[9] = 0x88;
        data.CopyTo(p, 10);
        Crc32.Hash(p.AsSpan(6)).CopyTo(p, 2);
        ProcessBytes(p, 2, p.Length - 2, p, 2);
        return p;
    }

    public (byte Type, byte[] Data) Unpack(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("认证响应过短");
        ProcessBytes(data, 0, data.Length, data, 0);
        var crc = Crc32.Hash(data.AsSpan(4));
        if (!crc.AsSpan().SequenceEqual(data.AsSpan(0, 4))) throw new InvalidDataException("认证响应校验失败");
        return (data[4], data[8..]);
    }
}

internal static class BinExt
{
    public static byte[] Concat(this byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

    public static byte[] Xor(this byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("长度不一致");
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    public static void WriteInt(this MemoryStream s, int v) => s.Write(BitConverter.GetBytes(v));
    public static void WriteLong(this MemoryStream s, long v) => s.Write(BitConverter.GetBytes(v));

    public static void WriteShort(this MemoryStream s, int v, bool littleEndian = true)
    {
        var b = BitConverter.GetBytes((short)v);
        if (!littleEndian) Array.Reverse(b);
        s.Write(b);
    }

    public static void WriteByteString(this MemoryStream s, string v)
    {
        var b = Encoding.UTF8.GetBytes(v);
        s.WriteByte((byte)b.Length);
        s.Write(b);
    }

    public static void WriteShortString(this MemoryStream s, string v, bool littleEndian = true)
    {
        var b = Encoding.UTF8.GetBytes(v);
        s.WriteShort(b.Length, littleEndian);
        s.Write(b);
    }

    public static void WriteShortBytes(this MemoryStream s, byte[] b)
    {
        s.WriteShort(b.Length);
        s.Write(b);
    }

    /// <summary>Reads [len:2 LE][payload].</summary>
    public static async Task<byte[]> ReadInt16FrameAsync(this Stream s, CancellationToken ct)
    {
        var head = new byte[2];
        await s.ReadExactlyAsync(head, ct);
        short len = BitConverter.ToInt16(head);
        if (len < 0) throw new InvalidDataException("帧长度为负");
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }
}
