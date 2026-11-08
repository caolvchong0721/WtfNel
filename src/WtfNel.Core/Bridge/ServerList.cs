using System.Buffers.Binary;
using System.Text;
using WtfNel.Core.Logging;

namespace WtfNel.Core;

/// <summary>
/// Ensures a server entry exists in .minecraft\servers.dat, the multiplayer list. This is the manual
/// fallback for auto-join: if the launcher bridge push does not land and the player ends up at the main
/// menu, the target server is already in the list, so clicking Multiplayer joins it in one step. The file
/// is uncompressed Java NBT; it is parsed and merged in place so an existing list is preserved.
/// </summary>
public static class ServerList
{
    public static void Ensure(string gameDir, string ip, string name)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var path = Path.Combine(gameDir, "servers.dat");
        try
        {
            NbtCompound root = TryRead(path) ?? new NbtCompound();
            if (root.Get("servers") is not NbtList list || list.ElemType != 10)
            {
                list = new NbtList(10);
                root.Set("servers", 9, list);
            }

            bool exists = list.Items.OfType<NbtCompound>()
                .Any(s => s.Get("ip") is string existing &&
                          string.Equals(existing, ip, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                Log.Info($"[servers] 多人列表已含 {ip}");
                return;
            }

            var entry = new NbtCompound();
            entry.Set("name", 8, name);
            entry.Set("ip", 8, ip);
            list.Items.Insert(0, entry);

            Directory.CreateDirectory(gameDir);
            File.WriteAllBytes(path, Write(root));
            Log.Info($"[servers] 已写入多人列表 {name} = {ip}");
        }
        catch (Exception ex)
        {
            Log.Warn("[servers] 写入 servers.dat 失败: " + ex.Message);
        }
    }

    private static NbtCompound? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            int pos = 0;
            if (data.Length == 0 || data[pos++] != 10) return null;
            ReadString(data, ref pos);
            return ReadCompound(data, ref pos);
        }
        catch
        {
            return null;
        }
    }

    private static NbtCompound ReadCompound(byte[] d, ref int pos)
    {
        var c = new NbtCompound();
        while (true)
        {
            byte type = d[pos++];
            if (type == 0) break;
            string name = ReadString(d, ref pos);
            c.Set(name, type, ReadPayload(d, ref pos, type));
        }
        return c;
    }

    private static object ReadPayload(byte[] d, ref int pos, byte type)
    {
        switch (type)
        {
            case 1: return d[pos++];
            case 2: { var v = BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(pos)); pos += 2; return v; }
            case 3: { var v = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)); pos += 4; return v; }
            case 4: { var v = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(pos)); pos += 8; return v; }
            case 5: { var v = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(pos)); pos += 4; return v; }
            case 6: { var v = BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(pos)); pos += 8; return v; }
            case 7:
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)); pos += 4;
                var b = d.AsSpan(pos, len).ToArray(); pos += len; return b;
            }
            case 8: return ReadString(d, ref pos);
            case 9:
            {
                byte elem = d[pos++];
                int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)); pos += 4;
                var list = new NbtList(elem);
                for (int i = 0; i < len; i++) list.Items.Add(ReadPayload(d, ref pos, elem));
                return list;
            }
            case 10: return ReadCompound(d, ref pos);
            case 11:
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)); pos += 4;
                var arr = new int[len];
                for (int i = 0; i < len; i++) { arr[i] = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)); pos += 4; }
                return arr;
            }
            case 12:
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)); pos += 4;
                var arr = new long[len];
                for (int i = 0; i < len; i++) { arr[i] = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(pos)); pos += 8; }
                return arr;
            }
            default: throw new InvalidDataException("未知 NBT 标签 " + type);
        }
    }

    private static string ReadString(byte[] d, ref int pos)
    {
        int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos)); pos += 2;
        var s = Encoding.UTF8.GetString(d, pos, len); pos += len;
        return s;
    }

    private static byte[] Write(NbtCompound root)
    {
        var ms = new MemoryStream();
        ms.WriteByte(10);
        WriteString(ms, "");
        WriteCompound(ms, root);
        return ms.ToArray();
    }

    private static void WriteCompound(Stream s, NbtCompound c)
    {
        foreach (var (name, type, val) in c.Entries)
        {
            s.WriteByte(type);
            WriteString(s, name);
            WritePayload(s, type, val);
        }
        s.WriteByte(0);
    }

    private static void WritePayload(Stream s, byte type, object val)
    {
        Span<byte> buf = stackalloc byte[8];
        switch (type)
        {
            case 1: s.WriteByte((byte)val); break;
            case 2: BinaryPrimitives.WriteInt16BigEndian(buf, (short)val); s.Write(buf[..2]); break;
            case 3: BinaryPrimitives.WriteInt32BigEndian(buf, (int)val); s.Write(buf[..4]); break;
            case 4: BinaryPrimitives.WriteInt64BigEndian(buf, (long)val); s.Write(buf[..8]); break;
            case 5: BinaryPrimitives.WriteSingleBigEndian(buf, (float)val); s.Write(buf[..4]); break;
            case 6: BinaryPrimitives.WriteDoubleBigEndian(buf, (double)val); s.Write(buf[..8]); break;
            case 7:
            {
                var b = (byte[])val;
                BinaryPrimitives.WriteInt32BigEndian(buf, b.Length); s.Write(buf[..4]); s.Write(b);
                break;
            }
            case 8: WriteString(s, (string)val); break;
            case 9:
            {
                var list = (NbtList)val;
                s.WriteByte(list.ElemType);
                BinaryPrimitives.WriteInt32BigEndian(buf, list.Items.Count); s.Write(buf[..4]);
                foreach (var item in list.Items) WritePayload(s, list.ElemType, item);
                break;
            }
            case 10: WriteCompound(s, (NbtCompound)val); break;
            case 11:
            {
                var arr = (int[])val;
                BinaryPrimitives.WriteInt32BigEndian(buf, arr.Length); s.Write(buf[..4]);
                foreach (var v in arr) { BinaryPrimitives.WriteInt32BigEndian(buf, v); s.Write(buf[..4]); }
                break;
            }
            case 12:
            {
                var arr = (long[])val;
                BinaryPrimitives.WriteInt32BigEndian(buf, arr.Length); s.Write(buf[..4]);
                foreach (var v in arr) { BinaryPrimitives.WriteInt64BigEndian(buf, v); s.Write(buf[..8]); }
                break;
            }
            default: throw new InvalidDataException("未知 NBT 标签 " + type);
        }
    }

    private static void WriteString(Stream s, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)bytes.Length);
        s.Write(len);
        s.Write(bytes);
    }

    private sealed class NbtCompound
    {
        public readonly List<(string Name, byte Type, object Val)> Entries = new();

        public object? Get(string name) => Entries.FirstOrDefault(e => e.Name == name).Val;

        public void Set(string name, byte type, object val)
        {
            int i = Entries.FindIndex(e => e.Name == name);
            if (i >= 0) Entries[i] = (name, type, val);
            else Entries.Add((name, type, val));
        }
    }

    private sealed class NbtList
    {
        public readonly byte ElemType;
        public readonly List<object> Items = new();
        public NbtList(byte elemType) => ElemType = elemType;
    }
}
