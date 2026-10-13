using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/ledger-hunt</c> (Go <c>ledgerhunt.go</c>).</summary>
internal static class FoodLedgerHunt
{
    private const string PrepareOp = "test/ledger_hunt_prepare";
    private const string ButcherOp = "test/ledger_hunt_butcher";
    private const string ObserveOp = "test/ledger_hunt_observe";

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/ledger-hunt",
        Scope = "Native read contract and end-to-end signal (a Go snapshot test cannot cover native hooks): a ranger kills one wild deer and a bill butchers it; the colony facts delivery ledger counts kill 1 and a butcher record whose meat equals the produced stack's independent tally with leather above zero, linked by corpse id, and a corpse re-placed twice (a haul) is not recounted.",
        Start = new FixtureStart { Op = PrepareOp, On = new LabStart() },
        QuietWorld = true,
        RequiredOps = [ButcherOp, ObserveOp],
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Reason = "a lab with a ranger, one deer and a butcher table; a few native hours of hunting and one butchering",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        Task<JsonObject> Tally(string label) => h.CallAsync(label, ObserveOp, null, cancellationToken);
        _ = await NativeReads.RunUntilAsync(s, "ledger-hunt-kill", 4000, async ct =>
        {
            var v = await h.CallAsync("ledger-hunt-progress", ObserveOp, null, ct).ConfigureAwait(false);
            s.Report["hunt_tally"] = v;
            return (Wait.Signature(v["tick"].ToJson()), v["kills"].AsNumber() > 0);
        }, quiet: true, cancellationToken).ConfigureAwait(false);
        var killed = await FoodLedger.ReadAsync(s, "ledger-hunt-killed", cancellationToken).ConfigureAwait(false);
        var killsTotal = killed.Raw["killsTotal"].AsNumber();
        if (killsTotal != 1 || killed.Raw["kills"].AsSlice().Count != 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"want one counted kill, got total {killsTotal}: {killed.Raw.ToJson()}"));
        }

        if (killed.Raw["butchers"].AsSlice().Count != 0)
        {
            throw new AcceptanceException($"a butcher was counted before any bill: {killed.Raw.ToJson()}");
        }

        var kill = killed.Raw["kills"].AsSlice()[0].AsMap() ?? [];
        var corpse = kill["corpseId"].AsString();
        if (corpse.Length == 0 || kill["race"].AsString() != "Deer" || kill["potentialNutrition"].AsNumber() <= 0 || kill["bodySize"].AsNumber() <= 0)
        {
            throw new AcceptanceException($"kill record is incomplete: {kill.ToJson()}");
        }

        var hauled = await h.CallAsync("ledger-hunt-butcher", ButcherOp, null, cancellationToken).ConfigureAwait(false);
        if (hauled["corpse"].AsString() != corpse)
        {
            throw new AcceptanceException($"fixture corpse {hauled["corpse"].ToText()} is not the counted kill {corpse}");
        }

        _ = await NativeReads.RunUntilAsync(s, "ledger-hunt-butcher", 4000, async ct =>
        {
            var v = await h.CallAsync("ledger-hunt-butcher-progress", ObserveOp, null, ct).ConfigureAwait(false);
            s.Report["hunt_tally"] = v;
            return (Wait.Signature(v["tick"].ToJson()), v["butcherMeat"].AsNumber() > 0);
        }, quiet: true, cancellationToken).ConfigureAwait(false);
        var done = await Tally("ledger-hunt-done").ConfigureAwait(false);
        var ledger = await FoodLedger.ReadAsync(s, "ledger-hunt-facts", cancellationToken).ConfigureAwait(false);
        s.Report["ledger_hunt"] = new JsonObject { ["tally"] = done.DeepClone(), ["ledger"] = ledger.Raw.DeepClone() };
        var kills = ledger.Raw["killsTotal"].AsNumber();
        if (kills != 1 || done["kills"].AsNumber() != 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"a hauled corpse was recounted: ledger kills {kills}, fixture corpses {done["kills"].ToText()}"));
        }

        var butchersTotal = ledger.Raw["butchersTotal"].AsNumber();
        if (butchersTotal != 1 || ledger.Raw["butchers"].AsSlice().Count != 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"want one counted butcher, got total {butchersTotal}: {ledger.Raw.ToJson()}"));
        }

        var butcher = ledger.Raw["butchers"].AsSlice()[0].AsMap() ?? [];
        if (butcher["corpseId"].AsString() != corpse)
        {
            throw new AcceptanceException($"butcher record {butcher["corpseId"].ToText()} is not linked to corpse {corpse}");
        }

        var meat = butcher["meatUnits"].AsNumber();
        if (meat != done["butcherMeat"].AsNumber() || meat <= 0)
        {
            throw new AcceptanceException($"butcher meat {NativeReads.Fmt(meat)} differs from the produced stack {done["butcherMeat"].ToText()}");
        }

        var leather = butcher["leatherUnits"].AsNumber();
        if (leather != done["butcherLeather"].AsNumber() || leather <= 0)
        {
            throw new AcceptanceException($"butcher leather {NativeReads.Fmt(leather)} differs from the produced leather {done["butcherLeather"].ToText()}");
        }

        var nutrition = butcher["meatNutrition"].AsNumber();
        if (nutrition <= 0 || nutrition > kill["potentialNutrition"].AsNumber() * 1.01 || double.IsNaN(nutrition))
        {
            throw new AcceptanceException($"butchered nutrition {NativeReads.Fmt(nutrition)} is not within the kill's potential {kill["potentialNutrition"].ToText()}");
        }
    }
}
