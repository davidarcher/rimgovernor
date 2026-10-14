using System.Globalization;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Telemetry;

public sealed record PawnActivityReportRow(string World, string Pawn, long Start, long End,
    string Activity, string Purpose, string Job, string Outcome, bool Gap);

/// <summary>Clips to the requested window, exposes missing history and rejects overlaps.</summary>
public static class PawnActivityReport
{
    public static IReadOnlyList<PawnActivityReportRow> Read(string path, string world, string pawn, long from, long to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(pawn);
        if (from < 0 || to <= from) throw new ArgumentOutOfRangeException(nameof(to));
        var intervals = new List<PawnActivityReportRow>();
        OtlpReader.ReadLogs(path, log =>
        {
            var row = OtlpTimeline.FromLog(log);
            if (row?.Kind != PawnActivityTelemetry.IntervalKind) return;
            var p = row.Payload;
            string Text(string key) => p.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
            if (Text("world") != world || Text("pawn") != pawn) return;
            if (!long.TryParse(Text("start_tick"), CultureInfo.InvariantCulture, out var start)
                || !long.TryParse(Text("end_tick"), CultureInfo.InvariantCulture, out var end)) return;
            if (end <= from || start >= to) return;
            intervals.Add(new(world, pawn, Math.Max(from, start), Math.Min(to, end),
                Text("activity"), Text("purpose"), Text("job"), Text("end_reason"), Text("phase") == "gap"));
        });
        return Cover(intervals, world, pawn, from, to);
    }

    public static IReadOnlyList<PawnActivityReportRow> Cover(IEnumerable<PawnActivityReportRow> intervals,
        string world, string pawn, long from, long to)
    {
        var result = new List<PawnActivityReportRow>();
        var cursor = from;
        foreach (var row in intervals.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (row.World != world || row.Pawn != pawn || row.Start < from || row.End > to || row.End <= row.Start)
                throw new InvalidDataException("Activity interval is outside the requested pawn/window.");
            if (row.Start < cursor) throw new InvalidDataException("Overlapping pawn activity intervals.");
            if (row.Start > cursor) result.Add(new(world, pawn, cursor, row.Start, "unknown", "unknown", "", "missing_records", true));
            result.Add(row);
            cursor = row.End;
        }
        if (cursor < to) result.Add(new(world, pawn, cursor, to, "unknown", "unknown", "", "missing_records", true));
        return result;
    }
}
