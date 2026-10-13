using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/freeze</c> (Go <c>freeze.go</c>): proves test/freeze_needs. With every need but Rest frozen, a tenth of a day
/// at accelerated Ultrafast leaves each free colonist's frozen needs at maximum while Rest keeps moving; release lets
/// them fall again.
/// </summary>
internal static class NeedsFreeze
{
    // The one need the session leaves live; the freeze itself is what the case proves.
    private const string Kept = "Rest";

    internal static AcceptanceCase Case() => new()
    {
        Name = "needs/freeze",
        Scope = "test/freeze_needs pins every free colonist need but the kept ones at maximum across "
            + "an advance window and releases them on request.",
        Start = new LabStart(),
        Keep = [Kept],
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var names = s.Names;
        report["frozen"] = report["frozen_needs"];
        var supervisor = new ScenarioClock(h.WireFunc(), identity, Draft.Controller, report) { TestAcceleration = true };
        _ = await supervisor.AcquireAsync("acquire", cancellationToken).ConfigureAwait(false);
        var rt = new ScenarioRuntime(h.CallAsync, supervisor, report, names);

        async Task<Dictionary<string, Dictionary<string, double>>> Levels(string label)
        {
            var reply = await h.CallAsync(label, Needs.FreezeNeedsTool, new JsonObject { ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
            var output = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (var raw in reply["needs"].AsSlice())
            {
                var row = raw.AsMap();
                var pawn = row?["pawn"].AsString() ?? "";
                var levels = new Dictionary<string, double>(StringComparer.Ordinal);
                output[pawn] = levels;
                foreach (var n in row?["needs"].AsSlice() ?? [])
                {
                    var need = n.AsMap();
                    levels[need?["def"].AsString() ?? ""] = need?["level"].AsNumber() ?? -1;
                }
            }

            return output.Count == 0
                ? throw new AcceptanceException($"{label}: no free colonists reported: {reply.ToJson()}")
                : output;
        }

        var before = await Levels("needs-before").ConfigureAwait(false);

        // A tenth of a day: Rest falls by a few hundredths awake, more than enough to show it is live, while a frozen
        // need would fall the same.
        try
        {
            _ = await AdvanceGame.RunAsync(rt, 6000, [AdvanceOptions.WithTimeout(TimeSpan.FromSeconds(180))], cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"frozen window: {e.Message}", e);
        }

        var after = await Levels("needs-after").ConfigureAwait(false);
        var summary = new JsonObject { ["kept"] = Kept };
        report["needs"] = summary;
        var moved = 0;
        foreach (var (pawn, needs) in after)
        {
            foreach (var (def, level) in needs)
            {
                if (def == Kept)
                {
                    if (level != (before.GetValueOrDefault(pawn)?.GetValueOrDefault(def) ?? 0))
                    {
                        moved++;
                    }

                    continue;
                }

                if (level < 0.95)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"frozen need {def} on {pawn} fell to {level:0.000}"));
                }
            }
        }

        if (moved == 0)
        {
            throw new AcceptanceException($"kept need {Kept} did not move on any colonist: {LevelsJson(after).ToJson()}");
        }

        summary["kept_moved_on"] = moved;
        summary["after"] = LevelsJson(after);
        var released = await h.CallAsync("release", Needs.FreezeNeedsTool, new JsonObject { ["action"] = "release" }, cancellationToken).ConfigureAwait(false);
        if (released["frozen"].AsBool() == true)
        {
            throw new AcceptanceException($"release left needs frozen: {released.ToJson()}");
        }

        try
        {
            _ = await AdvanceGame.RunAsync(rt, 6000, [AdvanceOptions.WithTimeout(TimeSpan.FromSeconds(180))], cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"released window: {e.Message}", e);
        }

        var final = await Levels("needs-released").ConfigureAwait(false);
        var fell = 0;
        foreach (var (pawn, needs) in final)
        {
            foreach (var (def, level) in needs)
            {
                if (def != Kept && level < (after.GetValueOrDefault(pawn)?.GetValueOrDefault(def) ?? 0))
                {
                    fell++;
                }
            }
        }

        if (fell == 0)
        {
            throw new AcceptanceException($"no need fell after release: {LevelsJson(final).ToJson()}");
        }

        summary["fell_after_release"] = fell;
    }

    private static JsonObject LevelsJson(Dictionary<string, Dictionary<string, double>> levels)
    {
        var json = new JsonObject();
        foreach (var (pawn, needs) in levels)
        {
            var row = new JsonObject();
            foreach (var (def, level) in needs)
            {
                row[def] = level;
            }

            json[pawn] = row;
        }

        return json;
    }
}
