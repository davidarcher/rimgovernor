using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Explicit native Party mechanics after normal review declines unsupported gathering labor.</summary>
internal static class Gathering
{
    private const string PartySpotOp = "test/party_spot";
    private const string LordsOp = "test/gathering_lords";
    private const double DipLoss = 9.0;

    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/gathering",
        Scope = "Normal review explicitly leaves gathering labor unsupported and admits no gathering action. An explicit native gathering intent starts exactly one Party at the prepared spot with a legal organizer; this proves native mechanics, not automatic participant choice.",
        Start = new LabStart { Colonists = 5 },
        Serve = new ServeSpec { Families = [.. MoodDrive.EpicFamilies("gathering")], Prefix = "mood-gathering" },
        RequiredOps = [Ledger.ThoughtsOp, PartySpotOp, LordsOp],
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 5 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var spot = await MoodDrive.FixtureCallAsync(h, "party-spot", PartySpotOp, null, cancellationToken).ConfigureAwait(false);
        report["party_spot"] = spot.DeepClone();
        var dip = await MoodDrive.FixtureCallAsync(h, "dip", Ledger.ThoughtsOp, new JsonObject { ["pawn"] = string.Empty, ["thought"] = Ledger.FreeThought, ["loss"] = DipLoss }, cancellationToken).ConfigureAwait(false);
        report["dip"] = dip.DeepClone();
        foreach (var row in dip["pawns"].Rows())
        {
            var found = false;
            foreach (var memory in row["memories"].Rows())
            {
                found = found || (memory["def"].AsString() == Ledger.FreeThought && MoodDrive.Near(memory["offset"].AsNumber(), -DipLoss, .05));
            }

            if (!found)
            {
                throw new AcceptanceException($"a colonist lacks the {DipLoss.ToString("F0", CultureInfo.InvariantCulture)}-point dip: {row.ToJson()}");
            }
        }

        // The preconditions the case needs, read natively before the controller starts: a refused gathering then names
        // its gate, never the planner.
        var before = await MoodDrive.FixtureCallAsync(h, "lords-before", LordsOp, new JsonObject { ["def"] = PolicyFunctions.PartyGatheringDef }, cancellationToken).ConfigureAwait(false);
        report["lords_before"] = before.DeepClone();
        if (before["colonists"].AsNumber() < PolicyFunctions.GatheringMinColonists || before["spots"].AsNumber() != 1
            || before["lords"].AsNumber() != 0 || before["attendedMemories"].AsNumber() != 0)
        {
            throw new AcceptanceException($"gathering fixture is not set: {before.ToJson()}");
        }

        if (!before["canExecute"].Flag())
        {
            throw new AcceptanceException($"the game cannot hold a Party now (hour {before["hour"].ToText()}, game conditions ok: {before["acceptable"].ToText()}): {before.ToJson()}");
        }

        h = await UnsupportedLabor.ReviewAsync(s, "unsupported_forecast_gathering",
            RimGovernor.Brain.Domain.ActionKind.Gathering, cancellationToken).ConfigureAwait(false);
        var nativeBefore = await MoodDrive.FixtureCallAsync(h, "explicit-organizer", LordsOp,
            new JsonObject { ["def"] = PolicyFunctions.PartyGatheringDef }, cancellationToken).ConfigureAwait(false);
        var organizer = nativeBefore["eligibleOrganizer"].AsString();
        if (organizer.Length == 0) throw new AcceptanceException("No native legal explicit gathering organizer.");
        var receipt = await ApplyIntents.ApplyOneAsync(h, "explicit-party", s.Identity, "explicit-party",
            new JsonObject { ["gathering"] = new JsonObject { ["gatheringDef"] = PolicyFunctions.PartyGatheringDef,
                ["organizer"] = new JsonObject { ["id"] = organizer } } }, cancellationToken).ConfigureAwait(false);
        if (receipt["refused"] is not null) throw new AcceptanceException("Explicit gathering refused: " + receipt.ToJson());
        report["explicit_receipt"] = receipt.DeepClone();
        _ = await Authority.ReleaseAuthorityAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        var after = await MoodDrive.FixtureCallAsync(h, "lords-after", LordsOp, new JsonObject { ["def"] = PolicyFunctions.PartyGatheringDef }, cancellationToken).ConfigureAwait(false);
        report["lords_after"] = after.DeepClone();
        if (after["lords"].AsNumber() != 1)
        {
            throw new AcceptanceException($"expected exactly one running Party lord job, got {after["lords"].ToText()}: {after.ToJson()}");
        }

        if (after["spotX"].AsNumber() != spot["x"].AsNumber() || after["spotZ"].AsNumber() != spot["z"].AsNumber())
        {
            throw new AcceptanceException($"the party is not on the PartySpot {spot["x"].ToText()},{spot["z"].ToText()}: {after.ToJson()}");
        }

        if (after["organizer"].AsString().Length == 0)
        {
            throw new AcceptanceException($"the party has no organizer: {after.ToJson()}");
        }
    }
}
