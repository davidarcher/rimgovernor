using System.Globalization;
using System.Text.Json;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

internal static class PawnActivityCommand
{
    public static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string>(), new HashSet<string> { "logs", "world", "pawn", "from", "to" });
        if (flags.HelpRequested)
        {
            Console.WriteLine("pawnactivity -logs <logs.jsonl> -world <colony/load/map> -pawn <id> -from <tick> -to <tick>");
            return Task.FromResult(0);
        }
        ct.ThrowIfCancellationRequested();
        if (!long.TryParse(flags.String("from"), CultureInfo.InvariantCulture, out var from)
            || !long.TryParse(flags.String("to"), CultureInfo.InvariantCulture, out var to)
            || to <= from || from < 0 || flags.String("world").Length == 0 || flags.String("pawn").Length == 0)
            throw new UsageException("Supply logs, world, pawn and a nonempty game-tick window.");
        var rows = PawnActivityReport.Read(flags.String("logs"), flags.String("world"), flags.String("pawn"), from, to);
        var summary = rows.GroupBy(r => new { r.Activity, r.Purpose, r.Job, r.Outcome, r.Gap })
            .Select(g => new { g.Key, Ticks = g.Sum(r => r.End - r.Start) }).ToArray();
        var world = flags.String("world").Split('/');
        var outputs = OtlpTimeline.Read(flags.String("logs")).Where(r => r.Kind == PawnActivityTelemetry.OutputKind
            && r.Payload.GetValueOrDefault("pawn") as string == flags.String("pawn")
            && world.Length == 3 && r.Payload.GetValueOrDefault("colony") as string == world[0]
            && r.Payload.GetValueOrDefault("load") as string == world[1]
            && Convert.ToString(r.Payload.GetValueOrDefault("map"), CultureInfo.InvariantCulture) == world[2]
            && Convert.ToDouble(r.Payload.GetValueOrDefault("tick"), CultureInfo.InvariantCulture) >= from
            && Convert.ToDouble(r.Payload.GetValueOrDefault("tick"), CultureInfo.InvariantCulture) < to).Select(r => r.Payload).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { From = from, To = to, Summary = summary, Intervals = rows, Outputs = outputs }));
        return Task.FromResult(0);
    }
}
