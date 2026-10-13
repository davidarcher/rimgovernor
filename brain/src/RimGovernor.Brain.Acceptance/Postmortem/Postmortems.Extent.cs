using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// Go extent.go.
public static partial class Postmortems
{
    /// <summary>
    /// Uses the latest successful recorded routines response, never reconstructs current safety from history alone or
    /// combines different observations: the extentEligibility view of the newest <c>service*/http-*.json</c> row whose
    /// path is /api/routines with status 200.
    /// </summary>
    private static DigestSection ExtentEligibility(string dir)
    {
        var s = new DigestSection("colony extent") { Note = "no recorded /api/routines eligibility view" };
        var paths = new List<string>();
        if (Directory.Exists(dir))
        {
            foreach (var service in new DirectoryInfo(dir).EnumerateDirectories("service*"))
            {
                paths.AddRange(Directory.EnumerateFiles(service.FullName, "http-*.json"));
            }
        }

        paths = [.. paths.OrderBy(Path.GetFileName, StringComparer.Ordinal).ThenBy(p => p, StringComparer.Ordinal)];
        for (var i = paths.Count - 1; i >= 0; i--)
        {
            JsonObject? row;
            try
            {
                row = JsonNode.Parse(File.ReadAllText(paths[i])) as JsonObject;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (row is null
                || row["path"].AsString() != "/api/routines"
                || row["status"].AsNumber() != 200
                || row["response"].Member("extentEligibility") is not JsonObject view)
            {
                continue;
            }

            var rel = Path.GetRelativePath(dir, paths[i]).Replace('\\', '/');
            s.Note = "";
            var known = view["known"].AsBool() ?? false;
            s.Lines.Add(new DigestLine($"extent known={(known ? "true" : "false")} reason={view["reason"].AsString()}", rel));
            var regions = view["regions"].AsSlice();
            for (var j = 0; j < regions.Count; j++)
            {
                if (j >= MaxLines - 1)
                {
                    s.Note = string.Create(CultureInfo.InvariantCulture, $"{regions.Count - j} more regions in recorded response");
                    break;
                }

                var r = regions[j];
                var eligible = r.Member("eligible").AsBool() ?? false;
                s.Lines.Add(new DigestLine(
                    $"region={Plain(r.Member("region") ?? JsonValue.Create(0))} stage={r.Member("stage").AsString()} origins={FormatList(r.Member("origins"))} facilities={FormatList(r.Member("facilities"))} active={FormatList(r.Member("activeFacilities"))} eligible={(eligible ? "true" : "false")} holds={FormatList(r.Member("holdReasons"))}",
                    rel));
            }

            return s;
        }

        return s;
    }

    /// <summary>A list as Go's <c>%v</c> prints a slice: "[a b]", "[]" for none.</summary>
    private static string FormatList(JsonNode? value) => "[" + string.Join(' ', value.AsSlice().Select(Plain)) + "]";
}
