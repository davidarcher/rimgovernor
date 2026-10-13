using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/headroom</c> (Go <c>headroom.go</c>): EnsureMood enters and recovers on the headroom margins (#2535, #2549).
/// Native because the threshold and the held level are RimWorld's.
/// </summary>
internal static class Headroom
{
    private const string HeadroomOp = "test/mood_headroom";

    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/headroom",
        Scope = "EnsureMood enters and recovers on the headroom margins (#2535, #2549): with one colonist's mood held natively 0.07 above "
            + "its native minor-break threshold (inside the 0.10 entry margin, above the old entry at the threshold), the journal opens its "
            + "mood incident; lifted natively to 0.22 (past the 0.15 recovery margin) and the service restarted, the same occurrence closes. "
            + "Nightly tier. Needs are frozen so the margin alone decides. The stay-open band between 0.10 and 0.15 needs mood history that "
            + "a restart drops, so policy/mood_margin_test.go pins it. Native because the threshold and the held level are RimWorld's.",
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

        // The EnsureMood occurrence the journal holds: the colony's only mood incident belongs to the pinned colonist
        // (the sample does not expose an incident's subject).
        async Task<JsonObject> IncidentAsync(CancellationToken ct)
        {
            var store = await d.JournalAsync(ct).ConfigureAwait(false);
            return await store.SampleStandardAsync(PolicyFunctions.EnsureMood.Value, ct).ConfigureAwait(false);
        }

        static bool Opened(JsonObject sample) => sample["incident"].AsString().Length > 0;
        static bool Closed(JsonObject sample) => Opened(sample) && (sample["status"].AsString() == "closed" || sample["closed"].Flag());
        static string Sign(JsonObject sample, object tail) =>
            string.Create(CultureInfo.InvariantCulture, $"{sample["review_tick"].ToText()} {sample["review_revision"].ToText()} {tail}");

        try
        {
            // Inside the entry margin: the incident opens.
            await d.StartAsync(cancellationToken).ConfigureAwait(false);
            await Prefixed(
                "entry: ",
                () => d.WaitAsync(
                    TimeSpan.FromMinutes(4),
                    async ct =>
                    {
                        var sample = await IncidentAsync(ct).ConfigureAwait(false);
                        return (Sign(sample, Opened(sample)), Opened(sample) && !Closed(sample));
                    },
                    cancellationToken)).ConfigureAwait(false);
            var opened = await IncidentAsync(cancellationToken).ConfigureAwait(false);
            report["incident_opened"] = new JsonObject { ["id"] = opened["incident"].Copy(), ["pawn"] = pawn, ["status"] = opened["status"].Copy() };

            // Above the recovery margin: it closes.
            var h = await d.NativeAsync("recover", cancellationToken).ConfigureAwait(false);
            _ = await PinAsync(report, h, "pin-above-recovery", pawn, .22, cancellationToken).ConfigureAwait(false);
            await d.StartAsync(cancellationToken).ConfigureAwait(false);
            await Prefixed(
                "recovery: ",
                () => d.WaitAsync(
                    TimeSpan.FromMinutes(4),
                    async ct =>
                    {
                        var sample = await IncidentAsync(ct).ConfigureAwait(false);
                        return (Sign(sample, Closed(sample)), Closed(sample));
                    },
                    cancellationToken)).ConfigureAwait(false);
            var closed = await IncidentAsync(cancellationToken).ConfigureAwait(false);
            report["incident_closed"] = new JsonObject { ["id"] = closed["incident"].Copy(), ["status"] = closed["status"].Copy() };
            if (closed["incident"].AsString() != opened["incident"].AsString())
            {
                throw new AcceptanceException($"recovery closed a different occurrence: opened {opened["incident"].AsString()}, closed {closed["incident"].AsString()}");
            }
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
