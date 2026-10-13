using System.Buffers.Binary;
using System.IO.Compression;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>CRC-32 (IEEE, as PNG and zip use it); the BCL has none outside System.IO.Hashing.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Continues <paramref name="crc"/> over <paramref name="data"/>; start from 0.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>
/// The public-artifact PNG boundary (Go decoded the frame with <c>image/png</c> and re-encoded it to strip metadata and
/// trailing payloads). The C# runtime has no PNG codec, so this validates the container and re-emits a clean file:
/// signature, every chunk's length and CRC, IHDR (dimensions 1..4096, legal depth/colour/interlace combination),
/// PLTE/tRNS shape, contiguous IDAT, IEND, then the zlib stream is inflated through <see cref="ZLibStream"/> to prove it
/// is complete, checksum-valid and exactly the size the header implies with every scanline filter type legal. The output
/// copies only the critical IHDR, PLTE, tRNS (transparency) and a freshly deflated single IDAT plus IEND; every other
/// ancillary chunk (text, time, ICC, EXIF, private) and everything after IEND is dropped. Pixels are not decoded (filters
/// are not reversed and palette indices are not range-checked); an unknown critical chunk is refused, where Go ignored it.
/// </summary>
internal static class PngSanitizer
{
    private const int MaxDimension = 4096;
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 =
    [
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    ];

    /// <summary>Validates <paramref name="png"/> and returns the cleaned re-encoding; throws <see cref="RemoteAcceptException"/> when it is not a valid, bounded PNG.</summary>
    public static byte[] Sanitize(ReadOnlySpan<byte> png, string path)
    {
        try
        {
            return SanitizeCore(png);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            throw new RemoteAcceptException($"invalid or oversized rendered PNG {path}: {e.Message}", e);
        }
        catch (RemoteAcceptException e)
        {
            throw new RemoteAcceptException($"invalid or oversized rendered PNG {path}: {e.Message}", e);
        }
    }

    private static byte[] SanitizeCore(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new RemoteAcceptException("bad signature");
        }

        var pos = Signature.Length;
        byte[]? ihdr = null;
        byte[]? plte = null;
        byte[]? trns = null;
        var idat = new MemoryStream();
        var sawIdat = false;
        var idatClosed = false;
        var sawEnd = false;
        int width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
        while (!sawEnd)
        {
            if (png.Length - pos < 12)
            {
                throw new RemoteAcceptException("truncated chunk");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            if (length > 0x7FFFFFFF || (long)length + 12 > png.Length - pos)
            {
                throw new RemoteAcceptException("bad chunk length");
            }

            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 8 + (int)length)..]);
            if (Crc32.Update(Crc32.Update(0, type), data) != crc)
            {
                throw new RemoteAcceptException("invalid checksum");
            }

            foreach (var ch in type)
            {
                if (!(ch is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                {
                    throw new RemoteAcceptException("bad chunk type");
                }
            }

            pos += 12 + (int)length;
            var name = System.Text.Encoding.ASCII.GetString(type);
            if (ihdr is null && name != "IHDR")
            {
                throw new RemoteAcceptException("IHDR must be first");
            }

            if (name != "IDAT" && sawIdat)
            {
                idatClosed = true;
            }

            switch (name)
            {
                case "IHDR":
                    if (ihdr is not null || data.Length != 13)
                    {
                        throw new RemoteAcceptException("bad IHDR");
                    }

                    ihdr = data.ToArray();
                    var w = BinaryPrimitives.ReadUInt32BigEndian(data);
                    var h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (w < 1 || h < 1 || w > MaxDimension || h > MaxDimension)
                    {
                        throw new RemoteAcceptException("dimensions out of range");
                    }

                    width = (int)w;
                    height = (int)h;
                    depth = data[8];
                    colour = data[9];
                    interlace = data[12];
                    if (data[10] != 0 || data[11] != 0 || interlace > 1 || !LegalDepth(colour, depth))
                    {
                        throw new RemoteAcceptException("unsupported IHDR");
                    }

                    break;
                case "PLTE":
                    if (plte is not null || sawIdat || colour is 0 or 4 || data.Length % 3 != 0 || data.Length == 0 || data.Length > 768
                        || (colour == 3 && data.Length / 3 > (1 << depth)))
                    {
                        throw new RemoteAcceptException("bad PLTE");
                    }

                    plte = data.ToArray();
                    break;
                case "tRNS":
                    if (trns is not null || sawIdat || !TransparencyLegal(colour, data.Length, plte))
                    {
                        throw new RemoteAcceptException("bad tRNS");
                    }

                    trns = data.ToArray();
                    break;
                case "IDAT":
                    if (idatClosed || (colour == 3 && plte is null))
                    {
                        throw new RemoteAcceptException("bad IDAT sequence");
                    }

                    sawIdat = true;
                    idat.Write(data);
                    break;
                case "IEND":
                    if (length != 0 || !sawIdat)
                    {
                        throw new RemoteAcceptException("bad IEND");
                    }

                    sawEnd = true;
                    break;
                default:
                    if ((type[0] & 0x20) == 0)
                    {
                        throw new RemoteAcceptException($"unknown critical chunk {name}");
                    }

                    break; // ancillary: dropped
            }
        }

        // Anything after IEND is a trailing payload and is not copied.
        var raw = Inflate(idat.ToArray(), width, height, depth, colour, interlace);
        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", ihdr!);
        if (plte is not null)
        {
            WriteChunk(output, "PLTE", plte);
        }

        if (trns is not null)
        {
            WriteChunk(output, "tRNS", trns);
        }

        using (var deflated = new MemoryStream())
        {
            using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw);
            }

            WriteChunk(output, "IDAT", deflated.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static bool LegalDepth(int colour, int depth) => colour switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        _ => false,
    };

    private static bool TransparencyLegal(int colour, int length, byte[]? plte) => colour switch
    {
        0 => length == 2,
        2 => length == 6,
        3 => plte is not null && length >= 1 && length <= plte.Length / 3,
        _ => false,
    };

    private static int Channels(int colour) => colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };

    private static byte[] Inflate(byte[] compressed, int width, int height, int depth, int colour, int interlace)
    {
        var bits = Channels(colour) * depth;
        var rows = new List<int>();
        if (interlace == 0)
        {
            var rowBytes = ((width * bits) + 7) / 8;
            for (var y = 0; y < height; y++)
            {
                rows.Add(rowBytes);
            }
        }
        else
        {
            foreach (var (x, y, dx, dy) in Adam7)
            {
                var pw = width > x ? (width - x + dx - 1) / dx : 0;
                var ph = height > y ? (height - y + dy - 1) / dy : 0;
                if (pw == 0 || ph == 0)
                {
                    continue;
                }

                var rowBytes = ((pw * bits) + 7) / 8;
                for (var r = 0; r < ph; r++)
                {
                    rows.Add(rowBytes);
                }
            }
        }

        long total = 0;
        foreach (var r in rows)
        {
            total += r + 1L;
        }

        var raw = new byte[total];
        using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var filled = 0;
        while (filled < raw.Length)
        {
            var n = z.Read(raw, filled, raw.Length - filled);
            if (n == 0)
            {
                throw new RemoteAcceptException("not enough pixel data");
            }

            filled += n;
        }

        if (z.ReadByte() >= 0)
        {
            throw new RemoteAcceptException("too much pixel data");
        }

        var offset = 0;
        foreach (var r in rows)
        {
            if (raw[offset] > 4)
            {
                throw new RemoteAcceptException("bad filter type");
            }

            offset += r + 1;
        }

        return raw;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, head[4..]);
        output.Write(head);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Update(Crc32.Update(0, head[4..]), data));
        output.Write(crc);
    }
}
