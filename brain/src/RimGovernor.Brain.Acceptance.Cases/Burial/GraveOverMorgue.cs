using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>burial/grave_over_morgue</c> (Go <c>burial/grave_over_morgue.go</c>).</summary>
internal static class GraveOverMorgue
{
    private const string BurialStage = "test/burial_stage";
    private const string BurialRead = "test/burial_read";

    // The window bounds the vanilla haul: the grave is a few cells from the morgue and the hauler is idle.
    private const int BurialWindow = 2 * Wait.TicksPerDay;

    internal static AcceptanceCase Case() => new()
    {
        Name = "burial/grave_over_morgue",
        Scope = "Issue #2196 grave-versus-zone ranking: a morgue zone at policy.MorguePriority holding a colonist and a stranger corpse "
            + "beside a free grave. Vanilla hauling carries the colonist corpse into the grave, whose storage priority outranks the morgue, "
            + "and leaves the stranger, whom the grave refuses, in the morgue. A native read of vanilla stockpile ranking and hauling; "
            + "no snapshot test covers it.",
        Start = new FixtureStart
        {
            Op = BurialStage,
            Args = new JsonObject { ["priority"] = DomainWire.Name(StockpilePriority.Normal) },
            On = new LabStart(),
        },
        RequiredOps = [BurialStage, BurialRead],
        QuietWorld = true,
        VanillaColonists = true,
        Reason = "VanillaColonists: an idle colonist's own vanilla haul ranks the grave over the morgue zone.",
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        var colonist = prepared["colonistCorpse"].AsString();
        var stranger = prepared["strangerCorpse"].AsString();
        if (colonist.Length == 0 || stranger.Length == 0 || prepared["grave"].AsString().Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture ids: {prepared.ToJson()}");
        }

        var staged = StockpilePriority.Normal;
        if (PolicyFunctions.MorguePriority != staged)
        {
            throw new AcceptanceException($"the case stages the morgue at {DomainWire.Name(staged)} but policy.MorguePriority is {DomainWire.Name(PolicyFunctions.MorguePriority)}");
        }

        async Task<(bool ColonistInGrave, bool StrangerInZone)> Read(string label)
        {
            var reply = await h.CallAsync(label, BurialRead, new JsonObject { ["ids"] = colonist + "," + stranger }, cancellationToken).ConfigureAwait(false);
            var colonistInGrave = false;
            var strangerInZone = false;
            foreach (var raw in reply["things"].AsSlice())
            {
                var row = raw.AsMap();
                var inGrave = row?["inGrave"].AsBool() == true;
                var inZone = row?["zone"].AsBool() == true;
                var id = row?["id"].AsString();
                if (id == colonist)
                {
                    colonistInGrave = inGrave;
                }
                else if (id == stranger)
                {
                    if (inGrave)
                    {
                        throw new AcceptanceException($"{label}: the stranger corpse was interred in a colonist grave: {row.ToJson()}");
                    }

                    strangerInZone = inZone;
                }
            }

            return (colonistInGrave, strangerInZone);
        }

        for (var advanced = 0; ; advanced += 300)
        {
            var (inGrave, _) = await Read("burial-read-" + CaseSupport.Tag(advanced)).ConfigureAwait(false);
            if (inGrave)
            {
                s.Report["burial_ticks"] = advanced;
                break;
            }

            if (advanced >= BurialWindow)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist corpse was not hauled into the grave within {advanced} ticks: the morgue zone outranks the grave"));
            }

            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
        }

        // The hauler had every chance at the stranger too: it stays in the morgue.
        _ = await s.AdvanceAsync(600, null, cancellationToken).ConfigureAwait(false);
        var (_, strangerInMorgue) = await Read("burial-read-final").ConfigureAwait(false);
        if (!strangerInMorgue)
        {
            throw new AcceptanceException("the stranger corpse left the morgue zone");
        }

        s.Report["stranger_stays_in_morgue"] = true;
    }
}
