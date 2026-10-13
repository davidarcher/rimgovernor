using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A report's "world" block (Go <c>WorldRecord</c>): what decided the world a case ran on, so a failure can be read
/// against the roll that produced it and a run reproduced (-seed). The installed build's hashes stay under package_files.
/// </summary>
public sealed record WorldRecord
{
    /// <summary>
    /// The world seed: the one the debug start drew or was pinned to, the scenario's, or the loaded save's seedString.
    /// Empty when nothing knows it (a production build's quick start with no save).
    /// </summary>
    public string Seed { get; init; } = "";

    /// <summary>Says the seed was given (DebugStart.Seed, -seed) rather than drawn.</summary>
    public bool Pinned { get; init; }

    /// <summary>The save the game loaded (a cached debug start, a Save start, a resumed checkpoint).</summary>
    public string Save { get; init; } = "";

    /// <summary>The hash of the save's file as it was loaded.</summary>
    public string SaveSha256 { get; init; } = "";

    /// <summary>The start's fixture op.</summary>
    public string Fixture { get; init; } = "";

    /// <summary>The hash over the op and its arguments.</summary>
    public string FixtureHash { get; init; } = "";

    /// <summary>The report block: snake_case members, empty ones omitted.</summary>
    public JsonObject ToJson()
    {
        var block = new JsonObject();
        Put(block, "seed", Seed);
        if (Pinned)
        {
            block["pinned"] = true;
        }

        Put(block, "save", Save);
        Put(block, "save_sha256", SaveSha256);
        Put(block, "fixture", Fixture);
        Put(block, "fixture_hash", FixtureHash);
        return block;
    }

    private static void Put(JsonObject block, string key, string value)
    {
        if (value.Length > 0)
        {
            block[key] = value;
        }
    }
}

/// <summary>Builds the report's world block (Go <c>RecordWorld</c>, <c>FixtureHash</c>, <c>WorldOf</c>).</summary>
public static class WorldRecords
{
    /// <summary>
    /// Builds the record for <paramref name="start"/> once it has loaded: the save's file is hashed and its seed string
    /// read, and a Fixture's op and arguments hashed. A save that cannot be read leaves the hash and seed to what the
    /// start knew; the record never fails a run.
    /// </summary>
    public static WorldRecord Record(Config? config, Start start, WorldSource source)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(source);
        var record = new WorldRecord { Seed = source.Seed, Pinned = source.Pinned, Save = source.Save };
        if (source.Save.Length > 0 && config is not null)
        {
            try
            {
                var data = File.ReadAllBytes(config.ProfileSave(source.Save));
                var seed = SaveSeed(data);
                record = record with
                {
                    SaveSha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
                    Seed = seed.Length > 0 ? seed : record.Seed,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The record never fails a run: the start's own knowledge stands.
            }
        }

        if (start is FixtureStart fixture)
        {
            record = record with { Fixture = fixture.Op, FixtureHash = FixtureHash(fixture.Op, fixture.Args) };
        }

        return record;
    }

    /// <summary>The seedString of a .rws, empty when it has none.</summary>
    internal static string SaveSeed(byte[] data)
    {
        // The seed sits in the header; decoding the whole save to find it would cost a multi-megabyte string.
        var text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 1024 * 1024));
        const string open = "<seedString>";
        const string close = "</seedString>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? "" : text[start..end].Trim();
    }

    /// <summary>
    /// The short hash over a fixture op and its arguments (their canonical JSON), so two runs' preconditions compare by
    /// one token.
    /// </summary>
    public static string FixtureHash(string op, JsonObject? args)
    {
        var text = new StringBuilder();
        text.Append("{\"op\":").Append(JsonValue.Create(op).ToJsonString());
        if (args is { Count: > 0 })
        {
            text.Append(",\"args\":").Append(Dyn.Canonical(args));
        }

        text.Append('}');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>Reads a report's "world" block; null when it has none.</summary>
    public static WorldRecord? WorldOf(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report["world"] is not JsonObject block)
        {
            return null;
        }

        return new WorldRecord
        {
            Seed = block["seed"].AsString(),
            Pinned = block["pinned"].AsBool() == true,
            Save = block["save"].AsString(),
            SaveSha256 = block["save_sha256"].AsString(),
            Fixture = block["fixture"].AsString(),
            FixtureHash = block["fixture_hash"].AsString(),
        };
    }
}
