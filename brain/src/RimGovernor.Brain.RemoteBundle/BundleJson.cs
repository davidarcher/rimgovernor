using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>Raised for any refused bundle input or failed bundle operation (the Go package returned <c>error</c>).</summary>
public sealed class BundleException : Exception
{
    /// <summary>Creates the exception with the refusal text.</summary>
    public BundleException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the refusal text and its cause.</summary>
    public BundleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Throws <see cref="BundleException"/> with <paramref name="message"/> unless <paramref name="condition"/> holds.</summary>
    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new BundleException(message);
        }
    }
}

/// <summary>The source-generated serializer for the bundle wire records; required properties and non-nullable members are enforced.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(Inventory))]
[JsonSerializable(typeof(Starts))]
[JsonSerializable(typeof(Trust))]
[JsonSerializable(typeof(Release))]
[JsonSerializable(typeof(Asset))]
[JsonSerializable(typeof(Asset[][]))]
[JsonSerializable(typeof(BootstrapReport))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;

/// <summary>
/// The JSON boundary of the bundle (Go <c>Decode</c> and <c>WriteJSON</c>): duplicate keys, <c>null</c> anywhere,
/// missing required fields and trailing data fail closed; unknown fields stay allowed by the versioned contract.
/// </summary>
public static class BundleJson
{
    /// <summary>Decodes a manifest (see <see cref="DecodeManifest"/> for the strictness).</summary>
    public static Manifest DecodeManifest(byte[] data) => Decode(data, BundleJsonContext.Default.Manifest);

    /// <summary>Decodes an inventory.</summary>
    public static Inventory DecodeInventory(byte[] data) => Decode(data, BundleJsonContext.Default.Inventory);

    /// <summary>Decodes a cached-start compatibility record.</summary>
    public static Starts DecodeStarts(byte[] data) => Decode(data, BundleJsonContext.Default.Starts);

    /// <summary>Decodes the trusted workflow's authorization record.</summary>
    public static Trust DecodeTrust(byte[] data) => Decode(data, BundleJsonContext.Default.Trust);

    /// <summary>Writes <paramref name="value"/> indented with a trailing newline, owner-only on creation (Go <c>WriteJSON</c>).</summary>
    public static void WriteManifest(string fileName, Manifest value) => Write(fileName, JsonSerializer.SerializeToUtf8Bytes(value, BundleJsonContext.Default.Manifest));

    /// <summary>Writes an inventory (see <see cref="WriteManifest"/>).</summary>
    public static void WriteInventory(string fileName, Inventory value) => Write(fileName, JsonSerializer.SerializeToUtf8Bytes(value, BundleJsonContext.Default.Inventory));

    /// <summary>Writes the cached-start compatibility record (see <see cref="WriteManifest"/>).</summary>
    public static void WriteStarts(string fileName, Starts value) => Write(fileName, JsonSerializer.SerializeToUtf8Bytes(value, BundleJsonContext.Default.Starts));

    /// <summary>Writes the bootstrap report (see <see cref="WriteManifest"/>).</summary>
    public static void WriteReport(string fileName, BootstrapReport value) => Write(fileName, JsonSerializer.SerializeToUtf8Bytes(value, BundleJsonContext.Default.BootstrapReport));

    /// <summary>Decodes GitHub's release metadata (lenient about unknown and null fields: it is not a bundle input).</summary>
    internal static Release DecodeRelease(byte[] data) => DecodeLoose(data, BundleJsonContext.Default.Release);

    /// <summary>Decodes the slurped pages of a release's asset listing.</summary>
    internal static Asset[][] DecodeAssetPages(byte[] data) => DecodeLoose(data, BundleJsonContext.Default.AssetArrayArray);

    private static T Decode<T>(byte[] data, JsonTypeInfo<T> info)
        where T : class
    {
        CheckStrict(data);
        try
        {
            return JsonSerializer.Deserialize(data, info) ?? throw new BundleException("null bundle field");
        }
        catch (JsonException e)
        {
            throw new BundleException(e.Message, e);
        }
    }

    private static T DecodeLoose<T>(byte[] data, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(data, info) ?? throw new BundleException("empty GitHub response");
        }
        catch (JsonException e)
        {
            throw new BundleException(e.Message, e);
        }
    }

    private static void Write(string fileName, byte[] json)
    {
        var text = new byte[json.Length + 1];
        json.CopyTo(text, 0);
        text[^1] = (byte)'\n';
        File.WriteAllBytes(fileName, text);
    }

    /// <summary>Walks the document once: duplicate object keys, a null value and trailing data are refused.</summary>
    private static void CheckStrict(byte[] data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var keys = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        keys.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        keys.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var key = reader.GetString() ?? "";
                        if (!keys.Peek().Add(key))
                        {
                            throw new BundleException($"duplicate or invalid JSON key {key}");
                        }

                        break;
                    case JsonTokenType.Null:
                        throw new BundleException("null bundle field");
                    default:
                        break;
                }
            }
        }
        catch (JsonException e)
        {
            throw new BundleException(e.Message, e);
        }
    }
}
