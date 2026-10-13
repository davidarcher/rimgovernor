using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/ledger</c> (Go <c>ledger.go</c>): the mood ledger routes thoughts by owner (#2549, #2538). The native side
/// proves only that both memories are present on the pawn; the claim is the controller's own output.
/// </summary>
internal static class Ledger
{
    internal const string ThoughtsOp = "test/mood_thoughts";
    internal const string FreeThought = "MyOrganHarvested";
    private const string OwnedThought = "SleptOutside";
    private const double OwnedLoss = 4;
    private const string Owner = "MaintainHousing";
    private const double FreeLoss = 6;

    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/ledger",
        Scope = "The mood ledger routes thoughts by owner (#2549, #2538): a colonist carrying SleptOutside (owner MaintainHousing, -4) "
            + "and MyOrganHarvested (no owner, -6) shows both in the moodLedger of GET /api/player/colony, the first with its owner and "
            + "the second alone in the unowned bucket, each with its pawn count and mood lost. Nightly tier. The native side proves only "
            + "that both memories are present on the pawn; the claim is the controller's own output.",
        Start = new LabStart { Colonists = 3 },
        Serve = new ServeSpec { Families = [.. MoodDrive.EpicFamilies("mood")], Prefix = "mood-ledger" },
        RequiredOps = [ThoughtsOp],
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        JsonObject? put = null;
        foreach (var (def, loss) in new[] { (OwnedThought, OwnedLoss), (FreeThought, FreeLoss) })
        {
            put = await MoodDrive.FixtureCallAsync(
                s.Harness,
                "inject-" + def,
                ThoughtsOp,
                new JsonObject { ["pawn"] = "first", ["thought"] = def, ["loss"] = loss },
                cancellationToken).ConfigureAwait(false);
            if (put["pawns"].AsSlice().Count != 1)
            {
                throw new AcceptanceException($"inject {def}: expected one pawn: {put.ToJson()}");
            }
        }

        // Both memories are on the pawn natively, at their requested size (the last reply lists every memory the pawn holds).
        report["injected"] = put!.DeepClone();
        var have = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in put["pawns"].Rows())
        {
            foreach (var memory in row["memories"].Rows())
            {
                have[memory["def"].AsString()] = memory["offset"].AsNumber();
            }
        }

        if (!MoodDrive.Near(have.GetValueOrDefault(OwnedThought), -OwnedLoss, .05) || !MoodDrive.Near(have.GetValueOrDefault(FreeThought), -FreeLoss, .05))
        {
            throw new AcceptanceException("native memories are not the injected ones: " + string.Join(", ", have.Select(p => $"{p.Key}:{NativeReads.Fmt(p.Value)}")));
        }

        var d = new MoodDrive(s);
        await d.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject? colony = null;
            await d.WaitAsync(
                TimeSpan.FromMinutes(4),
                async ct =>
                {
                    var got = await d.Service.GetAsync("GET", "/api/player/colony", ct).ConfigureAwait(false);
                    colony = got;
                    if (got["moodLedger"].AsMap() is not { } ledgerNow)
                    {
                        return ("no ledger yet", false);
                    }

                    var rows = ledgerNow["sources"].AsSlice();
                    return (rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Find(rows, FreeThought) is not null && Find(rows, OwnedThought) is not null);
                },
                cancellationToken).ConfigureAwait(false);
            var ledger = colony?["moodLedger"].AsMap() ?? [];
            report["mood_ledger"] = ledger.DeepClone();
            var sources = ledger["sources"].AsSlice();
            var unowned = ledger["unowned"].AsSlice();

            var owned = Find(sources, OwnedThought);
            var free = Find(sources, FreeThought);
            var ownedOwners = owned?["owners"].AsSlice() ?? [];
            if (ownedOwners.Count != 1 || ownedOwners[0].AsString() != Owner)
            {
                throw new AcceptanceException($"{OwnedThought} owners = {owned?["owners"].ToJson()}, want [{Owner}]");
            }

            if ((free?["owners"].AsSlice() ?? []).Count != 0)
            {
                throw new AcceptanceException($"{FreeThought} owners = {free?["owners"].ToJson()}, want none");
            }

            foreach (var (name, row, loss) in new[] { (OwnedThought, owned, OwnedLoss), (FreeThought, free, FreeLoss) })
            {
                if (row?["pawns"].AsNumber() != 1 || !MoodDrive.Near(Math.Abs(row["lost"].Strict() ?? 0), loss, .05))
                {
                    throw new AcceptanceException($"{name} = {row.ToJson()}, want one pawn losing {NativeReads.Fmt(loss)}");
                }
            }

            if (Find(unowned, FreeThought) is null)
            {
                throw new AcceptanceException($"{FreeThought} missing from the unowned bucket: {ledger["unowned"].ToJson()}");
            }

            if (Find(unowned, OwnedThought) is not null)
            {
                throw new AcceptanceException($"{OwnedThought} (owner {Owner}) landed in the unowned bucket: {ledger["unowned"].ToJson()}");
            }

            if (IndexOf(sources, FreeThought) > IndexOf(sources, OwnedThought))
            {
                throw new AcceptanceException($"sources are not ranked by mood lost: {ledger["sources"].ToJson()}");
            }
        }
        finally
        {
            await d.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The ledger row for <paramref name="def"/>, null when absent.</summary>
    private static JsonObject? Find(IReadOnlyList<JsonNode?> rows, string def)
    {
        var i = IndexOf(rows, def);
        return i >= 0 ? rows[i].AsMap() : null;
    }

    private static int IndexOf(IReadOnlyList<JsonNode?> rows, string def)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].AsMap()?["def"].AsString() == def)
            {
                return i;
            }
        }

        return -1;
    }
}
