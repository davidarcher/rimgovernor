using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/headroom</c> (Go <c>headroom.go</c>): a pawn's people/mood Need is unmet and met on the one headroom margin
/// (#2535, #2549, #3304, #3265).
/// Native because the threshold and the held level are RimWorld's.
/// </summary>
internal static class Headroom
{
    private const string HeadroomOp = "test/mood_headroom";

    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/headroom",
        Tier = CaseTier.Nightly,
        Scope = "A pawn's people/mood Need is unmet and met on the one headroom margin (#2535, #2549, #3304, #3265): with one colonist's mood held natively 0.07 above "
            + "its native minor-break threshold (inside the 0.15 margin, above the old entry at the threshold), the review files its "
            + "people/mood/<pawn> progress row (unmet); lifted natively to 0.22 (past the 0.15 margin) and the service restarted, the same row goes (met). "
            + "Nightly tier. Needs are frozen so the margin alone decides; there is no stay-open band (decision 26). "
            + "Native because the threshold and the held level are RimWorld's.",
        Start = new LabStart { Colonists = 3 },
        Serve = new ServeSpec { Families = [.. MoodDrive.EpicFamilies("mood")], Prefix = "mood-headroom" },
        RequiredOps = [HeadroomOp],
        Keep = ["Mood"],
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task<string> PinAsync(Report report, Harness h, string label, string pawn, double headroom, CancellationToken cancellationToken)
    {
        var pinned = await MoodDrive.FixtureCallAsync(h, label, HeadroomOp, new JsonObject { ["pawn"] = pawn, ["headroom"] = headroom }, cancellationToken).ConfigureAwait(false);
        var got = pinned["headroom"].AsNumber();
        if (!MoodDrive.Near(got, headroom, 0.01))
        {
            throw new AcceptanceException($"{label}: mood held {got.ToString("F3", CultureInfo.InvariantCulture)} above the threshold, want {headroom.ToString("F3", CultureInfo.InvariantCulture)}: {pinned.ToJson()}");
        }

        report[label] = pinned.DeepClone();
        return pinned["pawn"].AsString();
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var pawn = await PinAsync(report, s.Harness, "pin-inside-entry", string.Empty, .07, cancellationToken).ConfigureAwait(false);
        var d = new MoodDrive(s);

        // The people/mood rows the review holds: the colony's only unmet mood Need is the pinned colonist's.
        async Task<JsonObject> RowsAsync(CancellationToken ct)
        {
            var store = await d.JournalAsync(ct).ConfigureAwait(false);
            return await store.NeedRowsAsync(PeopleFamily.MoodCensus.Value + "/", ct).ConfigureAwait(false);
        }

        static List<string> Needs(JsonObject sample) => [.. sample["needs"]!.AsArray().Select(n => n.AsString())];
        static string Sign(JsonObject sample, object tail) =>
            string.Create(CultureInfo.InvariantCulture, $"{sample["review_tick"].ToText()} {sample["review_revision"].ToText()} {tail}");

        try
        {
            // Inside the margin: the pawn's mood Need is unmet.
            await d.StartAsync(cancellationToken).ConfigureAwait(false);
            await Prefixed(
                "entry: ",
                () => d.WaitAsync(
                    TimeSpan.FromMinutes(4),
                    async ct =>
                    {
                        var sample = await RowsAsync(ct).ConfigureAwait(false);
                        return (Sign(sample, Needs(sample).Count), Needs(sample).Count > 0);
                    },
                    cancellationToken)).ConfigureAwait(false);
            var need = Needs(await RowsAsync(cancellationToken).ConfigureAwait(false))[0];
            report["need_unmet"] = new JsonObject { ["need"] = need, ["pawn"] = pawn };

            // Above the margin: the same Need is met and its row goes.
            var h = await d.NativeAsync("recover", cancellationToken).ConfigureAwait(false);
            _ = await PinAsync(report, h, "pin-above-recovery", pawn, .22, cancellationToken).ConfigureAwait(false);
            await d.StartAsync(cancellationToken).ConfigureAwait(false);
            await Prefixed(
                "recovery: ",
                () => d.WaitAsync(
                    TimeSpan.FromMinutes(4),
                    async ct =>
                    {
                        var sample = await RowsAsync(ct).ConfigureAwait(false);
                        var met = !Needs(sample).Contains(need);
                        return (Sign(sample, met), met);
                    },
                    cancellationToken)).ConfigureAwait(false);
            report["need_met"] = new JsonObject { ["need"] = need, ["rows"] = (await RowsAsync(cancellationToken).ConfigureAwait(false))["needs"]!.DeepClone() };
        }
        finally
        {
            await d.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task Prefixed(string prefix, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException(prefix + e.Message);
        }
    }
}
