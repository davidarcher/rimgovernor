using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>How much of each payload the evidence files keep.</summary>
public enum EvidenceMode
{
    /// <summary>Keeps a payload up to <see cref="EvidenceBudget.EvidencePayloadCap"/> bytes and marks the row truncated past it; the full payload is dropped.</summary>
    Capped,

    /// <summary>Also writes every row that overflowed the cap, untouched, to <c>&lt;output&gt;/full/&lt;same file name&gt;</c>.</summary>
    Full,
}

/// <summary>
/// The evidence budget (Go <c>evidencebudget.go</c>): a row's payload (a native reply envelope under "result", a
/// service response under "response") is kept inline up to <see cref="EvidencePayloadCap"/> and past it replaced by
/// its preview, size and hash; the untouched row survives only under <see cref="EvidenceMode.Full"/>, in
/// <c>&lt;output&gt;/full/</c>. The artifact manifest hashes the capped rows and the files the report names, never
/// the log ring, service database or logs.
/// </summary>
public static class EvidenceBudget
{
    /// <summary>Overrides the evidence mode ("capped" or "full") for a process whose runner sets no -evidence flag.</summary>
    public const string EvidenceEnv = "RIMGOVERNOR_ACCEPT_EVIDENCE";

    /// <summary>The largest payload an evidence row keeps inline: the OTLP log ring's payload cap, so a capped row and the row for the same call agree on what survived.</summary>
    public const int EvidencePayloadCap = 256 << 10;

    /// <summary>The directory under an output where <see cref="EvidenceMode.Full"/> writes the overflowing rows.</summary>
    public const string FullEvidenceDir = "full";

    private static readonly string[] EvidencePayloadKeys = ["result", "response"];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lock ModeLock = new();
    private static EvidenceMode? s_mode;

    /// <summary>Sets the process-wide evidence mode; null restores the default (<see cref="EvidenceEnv"/>, else capped).</summary>
    public static void SetEvidenceMode(EvidenceMode? mode)
    {
        lock (ModeLock)
        {
            s_mode = mode;
        }
    }

    /// <summary>Parses "capped" or "full" (a -evidence flag value); empty is null (the default); anything else throws.</summary>
    public static EvidenceMode? ParseEvidenceMode(string text) => text switch
    {
        "" => null,
        "capped" => EvidenceMode.Capped,
        "full" => EvidenceMode.Full,
        _ => throw new AcceptanceException($"evidence mode must be \"capped\" or \"full\", got \"{text}\""),
    };

    /// <summary>The mode <see cref="SetEvidenceMode"/> chose, else <see cref="EvidenceEnv"/> when it names a valid mode, else capped.</summary>
    public static EvidenceMode CurrentEvidenceMode()
    {
        lock (ModeLock)
        {
            if (s_mode is { } chosen)
            {
                return chosen;
            }
        }

        return Environment.GetEnvironmentVariable(EvidenceEnv) switch
        {
            "full" => EvidenceMode.Full,
            _ => EvidenceMode.Capped,
        };
    }

    /// <summary>
    /// Returns <paramref name="row"/> with every payload key's value over <see cref="EvidencePayloadCap"/> bytes
    /// replaced by <c>&lt;key&gt;_preview</c> (its first cap bytes as a string, since the cut is not valid JSON),
    /// <c>&lt;key&gt;_bytes</c> and <c>&lt;key&gt;_sha256</c>, plus <c>truncated: true</c>, and whether anything was
    /// cut. An uncut row is returned as is; the input is never changed.
    /// </summary>
    public static (JsonObject Row, bool Cut) CapEvidenceRow(JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        JsonObject? capped = null;
        foreach (var key in EvidencePayloadKeys)
        {
            if (!row.TryGetPropertyValue(key, out var value) || value is null)
            {
                continue;
            }

            var payload = Encoding.UTF8.GetBytes(value.ToJsonString());
            if (payload.Length <= EvidencePayloadCap)
            {
                continue;
            }

            if (capped is null)
            {
                capped = [];
                foreach (var (k, v) in row)
                {
                    capped[k] = v?.DeepClone();
                }

                capped["truncated"] = true;
            }

            capped.Remove(key);
            capped[key + "_preview"] = Encoding.UTF8.GetString(payload, 0, EvidencePayloadCap);
            capped[key + "_bytes"] = payload.Length;
            capped[key + "_sha256"] = Convert.ToHexStringLower(SHA256.HashData(payload));
        }

        return capped is null ? (row, false) : (capped, true);
    }

    /// <summary>Writes one evidence row to <paramref name="path"/>, capped; under <see cref="EvidenceMode.Full"/> a cut row is also written untouched to <c>&lt;dir&gt;/full/&lt;name&gt;</c>. Write failures are dropped, as in Go.</summary>
    public static void WriteEvidence(string path, JsonObject row)
    {
        var (capped, cut) = CapEvidenceRow(row);
        if (cut && CurrentEvidenceMode() == EvidenceMode.Full)
        {
            try
            {
                var fullDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", FullEvidenceDir);
                Directory.CreateDirectory(fullDir);
                WriteJsonFile(Path.Combine(fullDir, Path.GetFileName(path)), row);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        WriteJsonFile(path, capped);
    }

    private static void WriteJsonFile(string path, JsonNode value)
    {
        try
        {
            File.WriteAllText(path, value.ToJsonString(WriteOptions), new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether <paramref name="relative"/> (slash-separated, relative to a case or suite output) is a capped evidence file: a .json file outside <see cref="FullEvidenceDir"/>.</summary>
    public static bool IsEvidenceFile(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        if (!relative.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        return !relative.Split('/').Contains(FullEvidenceDir, StringComparer.Ordinal);
    }

    /// <summary>The slash-separated paths of the files under <paramref name="output"/> that the report's string values name, relative to output or absolute.</summary>
    public static HashSet<string> ReferencedFiles(Report report, string output)
    {
        ArgumentNullException.ThrowIfNull(report);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var absOutput = Path.GetFullPath(output);
        Walk(report.Data, absOutput, found);
        return found;
    }

    private static void Walk(JsonNode? value, string absOutput, HashSet<string> found)
    {
        switch (value)
        {
            case JsonObject map:
                foreach (var (_, element) in map)
                {
                    Walk(element, absOutput, found);
                }

                break;
            case JsonArray array:
                foreach (var element in array)
                {
                    Walk(element, absOutput, found);
                }

                break;
            case JsonValue json when json.TryGetValue<string>(out var text):
                NoteFile(text, absOutput, found);
                break;
            default:
                break;
        }
    }

    private static void NoteFile(string text, string absOutput, HashSet<string> found)
    {
        if (text.Length == 0 || text.AsSpan().IndexOfAny("\n\"{}") >= 0 || text.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return;
        }

        try
        {
            var candidate = Path.IsPathRooted(text) ? text : Path.Combine(absOutput, text);
            var relative = Path.GetRelativePath(absOutput, Path.GetFullPath(candidate));
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return;
            }

            if (File.Exists(candidate))
            {
                found.Add(relative.Replace('\\', '/'));
            }
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }
    }

    /// <summary>
    /// Hashes (sha256 hex, keyed by slash-separated path relative to <paramref name="output"/>) every capped evidence
    /// file under output and every file the report names; other files (log segments, service databases, logs, saves,
    /// the full rows) are skipped without being read. An unreadable file ends the walk with what was hashed so far.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Artifacts(string output, Report report)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var referenced = ReferencedFiles(report, output);
        try
        {
            foreach (var path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(output, path).Replace('\\', '/');
                if (!IsEvidenceFile(relative) && !referenced.Contains(relative))
                {
                    continue;
                }

                hashes[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return hashes;
    }
}
