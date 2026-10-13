using System.Text.Json;

namespace RimGovernor.Brain.HttpChannel;

/// <summary>
/// The discovery file of the channel (issue #3083). The server binds an ephemeral loopback port, so a client finds
/// it by polling this file, which lives beside the OTLP rings. It names the run, so a client can tell a stale file
/// of a previous run from the live one.
/// </summary>
public static class EndpointFile
{
    public const string FileName = "http-endpoint.json";

    public const int SchemaVersion = 1;

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    /// <summary>Writes the file atomically (temp file then replace), so a poller never reads half of it.</summary>
    public static void Write(string directory, string address, int pid, string runId)
    {
        Directory.CreateDirectory(directory);
        var target = PathIn(directory);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("address", address);
            writer.WriteNumber("pid", pid);
            writer.WriteString("runId", runId);
            writer.WriteEndObject();
        }

        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Reads the file; null when it is absent or unreadable.</summary>
    public static (string Address, int Pid, string RunId)? Read(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(PathIn(directory)));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                return null;
            }

            return (root.GetProperty("address").GetString() ?? "", root.GetProperty("pid").GetInt32(), root.GetProperty("runId").GetString() ?? "");
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the file, but only when it still names this run, so a stopping run never deletes its successor's file.</summary>
    public static void Remove(string directory, string runId)
    {
        if (Read(directory) is { } current && current.RunId == runId)
        {
            try
            {
                File.Delete(PathIn(directory));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A reader holds it; the next run overwrites it.
            }
        }
    }
}
