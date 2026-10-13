using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>A request body that fails the strict syntax rules; the route answers 400 without saying which.</summary>
internal sealed class StrictJsonException(string message) : Exception(message);

/// <summary>
/// The strict decoder of player mutation bodies (Go <c>buildingRequest</c>). It establishes syntax and typed
/// intent only: at most <see cref="Limit"/> bytes, valid UTF-8, no lone surrogate escape, no duplicate key at any
/// depth, nesting of at most 8, nothing after the value, exactly the named keys and none of them null.
/// </summary>
internal static class StrictJson
{
    internal const int Limit = 8192;

    private const int MaxDepth = 8;

    internal static Dictionary<string, JsonElement> Object(byte[] body, params string[] keys) => Fields(Parse(body), keys);

    /// <summary>The root value of <paramref name="body"/> under the syntax rules above; the caller checks its shape.</summary>
    internal static JsonElement Parse(byte[] body)
    {
        if (body.Length > Limit)
        {
            throw new StrictJsonException("request exceeds byte limit");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new StrictJsonException("request is not valid UTF-8");
        }

        if (!ScalarStrings(text))
        {
            throw new StrictJsonException("request contains a lone surrogate escape");
        }

        try
        {
            CheckUnique(body);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxDepth });
            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new StrictJsonException(e.Message);
        }
    }

    /// <summary>The members of <paramref name="element"/>, which must be an object with exactly <paramref name="keys"/>, none null.</summary>
    internal static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new StrictJsonException("expected an object");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        if (fields.Count != keys.Length)
        {
            throw new StrictJsonException("unexpected or missing fields");
        }

        foreach (var key in keys)
        {
            if (!fields.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                throw new StrictJsonException("required field missing or null");
            }
        }

        return fields;
    }

    internal static string String(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new StrictJsonException("expected a string");
        }

        return element.GetString() ?? throw new StrictJsonException("expected a string");
    }

    internal static int Int32(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new StrictJsonException("expected a 32-bit integer");
        }

        return value;
    }

    /// <summary>A canonical decimal uint64 string: digits only, no sign, no leading zero.</summary>
    internal static ulong CanonicalUInt64(JsonElement element)
    {
        var text = String(element);
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || text != value.ToString(CultureInfo.InvariantCulture))
        {
            throw new StrictJsonException("expected a canonical decimal uint64 string");
        }

        return value;
    }

    /// <summary>A canonical, non-negative decimal int64 string.</summary>
    internal static long CanonicalNonNegativeInt64(JsonElement element)
    {
        var text = String(element);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || value < 0
            || text != value.ToString(CultureInfo.InvariantCulture))
        {
            throw new StrictJsonException("expected a canonical non-negative decimal int64 string");
        }

        return value;
    }

    private static void CheckUnique(byte[] body)
    {
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = MaxDepth });
        var seen = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    seen.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    seen.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!seen.Peek().Add(reader.GetString()!))
                    {
                        throw new StrictJsonException("duplicate JSON key");
                    }

                    break;
            }
        }
    }

    // A decoder that replaces lone surrogate escapes would change an identifier silently; reject them first.
    // Ordinary escaped backslashes stay literal.
    private static bool ScalarStrings(string data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != '\\')
            {
                continue;
            }

            i++;
            if (i >= data.Length)
            {
                return false;
            }

            if (data[i] != 'u')
            {
                continue;
            }

            if (i + 4 >= data.Length || !ushort.TryParse(data.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }

            i += 4;
            if (n is >= 0xDC00 and <= 0xDFFF)
            {
                return false;
            }

            if (n is < 0xD800 or > 0xDBFF)
            {
                continue;
            }

            if (i + 6 >= data.Length || data[i + 1] != '\\' || data[i + 2] != 'u'
                || !ushort.TryParse(data.AsSpan(i + 3, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low)
                || low is < 0xDC00 or > 0xDFFF)
            {
                return false;
            }

            i += 6;
        }

        return true;
    }
}
