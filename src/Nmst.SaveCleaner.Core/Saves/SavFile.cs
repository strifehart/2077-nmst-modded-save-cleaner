using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>
/// Reader/writer for a Cyberpunk 2077 sav.dat: a "VASC" header, an "FZLC" chunk table, "4ZLX" LZ4 chunks and an
/// "EDON" node table at the end. Node offsets address the logical stream = file header (up to the first chunk)
/// followed by every chunk decompressed in order.
/// </summary>
public sealed class SavFile
{
    public const string ModdingSystemNode = "ModdingSystem";

    public sealed record Chunk(int TableEntryOffset, int FileOffset, int CompressedSize, int DecompressedSize, int LogicalOffset);
    public sealed record Node(int Index, string Name, int Next, int Child, int Offset, int Size);

    private readonly byte[] _file;

    public IReadOnlyList<Chunk> Chunks { get; }
    public IReadOnlyList<Node> Nodes { get; }
    public int DataStart { get; }
    public int TableOffset { get; }

    private SavFile(byte[] file, List<Chunk> chunks, List<Node> nodes, int dataStart, int tableOffset)
    {
        _file = file; Chunks = chunks; Nodes = nodes; DataStart = dataStart; TableOffset = tableOffset;
    }

    public static SavFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static SavFile Parse(byte[] f)
    {
        if (f.Length < 16 || Encoding.ASCII.GetString(f, 0, 4) != "VASC") throw new InvalidDataException("not a sav.dat (missing VASC header)");
        int fz = IndexOf(f, "FZLC"u8);
        if (fz < 0) throw new InvalidDataException("missing FZLC chunk table");
        int count = ReadI32(f, fz + 4);
        if (count <= 0 || fz + 8 + count * 12 > f.Length) throw new InvalidDataException("bad chunk count " + count);

        var chunks = new List<Chunk>(count);
        int logical = -1;
        for (int i = 0; i < count; i++)
        {
            int e = fz + 8 + i * 12;
            int off = ReadI32(f, e), csize = ReadI32(f, e + 4), dsize = ReadI32(f, e + 8);
            if (logical < 0) logical = off;
            if (off < 0 || off + csize > f.Length || Encoding.ASCII.GetString(f, off, 4) != "4ZLX")
                throw new InvalidDataException($"bad chunk {i} at 0x{off:X}");
            chunks.Add(new Chunk(e, off, csize, dsize, logical));
            logical += dsize;
        }

        int tableOff = ReadI32(f, f.Length - 8);
        if (tableOff < 0 || tableOff + 4 > f.Length || Encoding.ASCII.GetString(f, tableOff, 4) != "EDON")
            throw new InvalidDataException("missing EDON node table");
        return new SavFile(f, chunks, ParseNodes(f, tableOff), chunks[0].FileOffset, tableOff);
    }

    public Node? FindNode(string name) => Nodes.FirstOrDefault(n => n.Name == name);

    /// <summary>Reads bytes from the logical stream, decompressing only the chunks that cover the range.</summary>
    public byte[] ReadLogical(int offset, int length)
    {
        var result = new byte[length];
        int done = 0;
        if (offset < DataStart)
        {
            int n = Math.Min(length, DataStart - offset);
            Array.Copy(_file, offset, result, 0, n);
            done = n;
        }
        foreach (var c in Chunks)
        {
            if (done == length) break;
            int pos = offset + done;
            if (pos < c.LogicalOffset || pos >= c.LogicalOffset + c.DecompressedSize) continue;
            var raw = Decompress(c);
            int n = Math.Min(length - done, c.LogicalOffset + c.DecompressedSize - pos);
            Array.Copy(raw, pos - c.LogicalOffset, result, done, n);
            done += n;
        }
        if (done != length) throw new InvalidDataException($"range 0x{offset:X}+{length} is outside the data");
        return result;
    }

    /// <summary>The full logical stream (header + every decompressed chunk).</summary>
    public byte[] ReadAllLogical()
    {
        var last = Chunks[^1];
        return ReadLogical(0, last.LogicalOffset + last.DecompressedSize);
    }

    public byte[] Decompress(Chunk c)
    {
        int declared = ReadI32(_file, c.FileOffset + 4);
        if (declared != c.DecompressedSize) throw new InvalidDataException($"chunk at 0x{c.FileOffset:X}: size {declared} != table {c.DecompressedSize}");
        var raw = new byte[c.DecompressedSize];
        int n = LZ4Codec.Decode(_file, c.FileOffset + 8, c.CompressedSize - 8, raw, 0, raw.Length);
        if (n != raw.Length) throw new InvalidDataException($"chunk at 0x{c.FileOffset:X} decompressed to {n}, expected {raw.Length}");
        return raw;
    }

    /// <summary>
    /// Returns a new sav.dat with one logical byte changed. Only the chunk that holds it is recompressed; the chunk
    /// table and the node-table pointer are shifted by the size difference. The node table itself is untouched.
    /// </summary>
    public byte[] WithByte(int logicalOffset, byte value)
    {
        if (logicalOffset < DataStart) throw new ArgumentOutOfRangeException(nameof(logicalOffset), "byte is in the uncompressed header");
        int ci = -1;
        for (int i = 0; i < Chunks.Count; i++)
            if (logicalOffset >= Chunks[i].LogicalOffset && logicalOffset < Chunks[i].LogicalOffset + Chunks[i].DecompressedSize) { ci = i; break; }
        if (ci < 0) throw new ArgumentOutOfRangeException(nameof(logicalOffset));

        var c = Chunks[ci];
        var raw = Decompress(c);
        raw[logicalOffset - c.LogicalOffset] = value;

        var comp = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        int clen = LZ4Codec.Encode(raw, 0, raw.Length, comp, 0, comp.Length, LZ4Level.L09_HC);
        if (clen <= 0) throw new InvalidOperationException("LZ4 compression failed");
        var check = new byte[raw.Length];
        if (LZ4Codec.Decode(comp, 0, clen, check, 0, check.Length) != raw.Length || !check.AsSpan().SequenceEqual(raw))
            throw new InvalidOperationException("LZ4 round-trip check failed");

        int newCsize = clen + 8;
        int delta = newCsize - c.CompressedSize;
        using var ms = new MemoryStream(_file.Length + Math.Max(delta, 0));

        var prefix = _file.AsSpan(0, DataStart).ToArray();
        WriteI32(prefix, c.TableEntryOffset + 4, newCsize);
        for (int i = ci + 1; i < Chunks.Count; i++) WriteI32(prefix, Chunks[i].TableEntryOffset, Chunks[i].FileOffset + delta);
        ms.Write(prefix);
        ms.Write(_file, DataStart, c.FileOffset - DataStart);
        Span<byte> hdr = stackalloc byte[8];
        "4ZLX"u8.CopyTo(hdr);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], raw.Length);
        ms.Write(hdr);
        ms.Write(comp, 0, clen);
        ms.Write(_file, c.FileOffset + c.CompressedSize, TableOffset - (c.FileOffset + c.CompressedSize));
        var table = _file.AsSpan(TableOffset).ToArray();
        WriteI32(table, table.Length - 8, TableOffset + delta);
        ms.Write(table);
        return ms.ToArray();
    }

    private static List<Node> ParseNodes(byte[] f, int off)
    {
        int p = off + 4;
        int n = ReadVarInt(f, ref p);
        var nodes = new List<Node>(n);
        for (int i = 0; i < n; i++)
        {
            int len = Math.Abs(ReadVarInt(f, ref p));
            string name = Encoding.Latin1.GetString(f, p, len);
            p += len;
            nodes.Add(new Node(i, name, ReadI32(f, p), ReadI32(f, p + 4), ReadI32(f, p + 8), ReadI32(f, p + 12)));
            p += 16;
        }
        return nodes;
    }

    // CR2W packed int: bit 7 of the first byte = sign, bit 6 = more bytes follow, 6 value bits, then 7 per byte.
    private static int ReadVarInt(byte[] buf, ref int p)
    {
        byte b = buf[p++];
        bool neg = (b & 0x80) != 0;
        int val = b & 0x3F;
        if ((b & 0x40) != 0)
        {
            int shift = 6;
            do { b = buf[p++]; val |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        }
        return neg ? -val : val;
    }

    private static int IndexOf(byte[] f, ReadOnlySpan<byte> needle) => f.AsSpan().IndexOf(needle);
    private static int ReadI32(byte[] f, int p) => BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(p, 4));
    private static void WriteI32(byte[] f, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(p, 4), v);
}
