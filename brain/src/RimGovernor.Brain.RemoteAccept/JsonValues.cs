using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>
/// Streams the top-level JSON values of a file (one document, or JSON lines, or any whitespace-separated run) without
/// holding the file in memory: Go's <c>json.Decoder</c> loop. Each value is checked for duplicate keys (case-folded) as
/// <see cref="StrictJson.CheckUnique"/> does; malformed JSON anywhere throws.
/// </summary>
internal static class JsonValues
{
    private const int InitialBuffer = 1 << 20;

    /// <summary>Yields each value as a node (<paramref name="parse"/>) or null (only validated).</summary>
    public static IEnumerable<JsonNode?> Enumerate(string path, bool parse)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[InitialBuffer];
        int start = 0, end = 0;
        var eof = false;
        while (true)
        {
            while (start < end && IsWhitespace(buf[start]))
            {
                start++;
            }

            if (start == end)
            {
                if (eof)
                {
                    yield break;
                }

                start = end = 0;
                (end, eof) = Fill(file, buf, end);
                continue;
            }

            int consumed;
            try
            {
                consumed = TryTake(buf.AsSpan(start, end - start), eof);
            }
            catch (JsonException e)
            {
                throw new RemoteAcceptException(e.Message, e);
            }

            if (consumed > 0)
            {
                var slice = buf.AsSpan(start, consumed);
                StrictJson.CheckUnique(slice, allowMultiple: false);
                JsonNode? node = null;
                if (parse)
                {
                    try
                    {
                        node = JsonNode.Parse(slice, null, new JsonDocumentOptions { MaxDepth = 1000 });
                    }
                    catch (JsonException e)
                    {
                        throw new RemoteAcceptException(e.Message, e);
                    }
                }

                start += consumed;
                yield return node;
                continue;
            }

            if (eof)
            {
                throw new RemoteAcceptException("unexpected end of JSON input");
            }

            if (start > 0)
            {
                Buffer.BlockCopy(buf, start, buf, 0, end - start);
                end -= start;
                start = 0;
            }

            if (end == buf.Length)
            {
                Array.Resize(ref buf, buf.Length * 2);
            }

            (end, eof) = Fill(file, buf, end);
        }
    }

    /// <summary>Bytes of the first complete value in <paramref name="data"/>, or 0 when more input is needed.</summary>
    private static int TryTake(ReadOnlySpan<byte> data, bool final)
    {
        var options = new JsonReaderOptions { MaxDepth = 1000, AllowMultipleValues = true };
        var reader = new Utf8JsonReader(data, final, new JsonReaderState(options));
        if (!JsonDocument.TryParseValue(ref reader, out var doc))
        {
            return 0;
        }

        doc.Dispose();
        return (int)reader.BytesConsumed;
    }

    private static (int End, bool Eof) Fill(FileStream file, byte[] buf, int end)
    {
        var n = file.Read(buf, end, buf.Length - end);
        return n == 0 ? (end, true) : (end + n, false);
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
