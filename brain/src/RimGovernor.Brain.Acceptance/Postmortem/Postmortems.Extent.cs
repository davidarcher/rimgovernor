using System.Globalization;
using System.Text.Json;

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
            RecordedRoutines? row;
            try
            {
                row = JsonSerializer.Deserialize(File.ReadAllText(paths[i]), PostmortemJsonContext.Default.RecordedRoutines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Another route's reply, or one that is not a routines body.
                continue;
            }

            if (row is not { Path: "/api/routines", Status: 200, Response.ExtentEligibility: { } view })
            {
                continue;
            }

            var rel = Path.GetRelativePath(dir, paths[i]).Replace('\\', '/');
            s.Note = "";
            s.Lines.Add(new DigestLine($"extent known={(view.Known ? "true" : "false")} reason={view.Reason}", rel));
            var regions = view.Regions;
            for (var j = 0; j < regions.Count; j++)
            {
                if (j >= MaxLines - 1)
                {
                    s.Note = string.Create(CultureInfo.InvariantCulture, $"{regions.Count - j} more regions in recorded response");
                    break;
                }

                var r = regions[j];
                s.Lines.Add(new DigestLine(
                    string.Create(CultureInfo.InvariantCulture, $"region={r.Region} stage={r.Stage} origins={FormatList(r.Origins)} facilities={FormatList(r.Facilities)} active={FormatList(r.ActiveFacilities)} eligible={(r.Eligible ? "true" : "false")} holds={FormatList(r.HoldReasons)}"),
                    rel));
            }

            return s;
        }

        return s;
    }

    /// <summary>A list as Go's <c>%v</c> prints a slice: "[a b]", "[]" for none.</summary>
    private static string FormatList(IReadOnlyList<string> values) => "[" + string.Join(' ', values) + "]";
}
