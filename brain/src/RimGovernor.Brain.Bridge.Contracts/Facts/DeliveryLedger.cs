using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go delivery_ledger.go.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>ValidDeliveryLedger</c>: whether a delivery ledger is well formed at the read's tick. A ledger is
    /// derived measurement, so the colony read does not fail on a bad one: the decoder treats it as unknown instead.
    /// </summary>
    public static bool ValidDeliveryLedger(DeliveryLedgerFacts? ledger, long tick)
    {
        if (ledger is null || ledger.Epoch is null || !IsValidId((ledger.Epoch ?? "")))
        {
            return false;
        }
        var seen = new HashSet<(string, string, string)>();
        foreach (var row in ledger.Rows)
        {
            if (row is null)
            {
                return false;
            }
            var kind = row.SourceKind.GetValueOrDefault();
            if (kind is < DeliverySourceKind.Crop or > DeliverySourceKind.AnimalProduct
                || !ValidDeliveryCounters(row, tick)
                || !IsValidId((row.SourceId ?? ""))
                || !IsValidId((row.DefName ?? ""))
                || !seen.Add((kind.ToString(), (row.SourceId ?? ""), (row.DefName ?? ""))))
            {
                return false;
            }
        }
        return ValidHuntRecords(ledger, tick);
    }

    /// <summary>
    /// Go's <c>validHuntRecords</c>: checks the kill and butcher windows. Each corpse appears once per window,
    /// counters are non-negative and no record is from the future.
    /// </summary>
    private static bool ValidHuntRecords(DeliveryLedgerFacts ledger, long tick)
    {
        var killed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kill in ledger.Kills)
        {
            if (kill is null
                || !IsValidId((kill.CorpseId ?? ""))
                || killed.Contains((kill.CorpseId ?? ""))
                || !IsValidId((kill.Race ?? ""))
                || !IsValidId((kill.PawnId ?? ""))
                || kill.BodySize is null
                || !CombatNumber(kill.BodySize, true)
                || kill.PotentialNutrition is null
                || !CombatNumber(kill.PotentialNutrition, true)
                || kill.Tick is null
                || kill.Tick.GetValueOrDefault() < 0
                || kill.Tick.GetValueOrDefault() > tick)
            {
                return false;
            }
            killed.Add((kill.CorpseId ?? ""));
        }
        var butchered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var butcher in ledger.Butchers)
        {
            if (butcher is null
                || !IsValidId((butcher.CorpseId ?? ""))
                || butchered.Contains((butcher.CorpseId ?? ""))
                || !IsValidId((butcher.Recipe ?? ""))
                || butcher.MeatUnits is null
                || butcher.MeatUnits.GetValueOrDefault() < 0
                || butcher.MeatNutrition is null
                || !CombatNumber(butcher.MeatNutrition, true)
                || butcher.LeatherUnits is null
                || butcher.LeatherUnits.GetValueOrDefault() < 0
                || butcher.Tick is null
                || butcher.Tick.GetValueOrDefault() < 0
                || butcher.Tick.GetValueOrDefault() > tick
                || (butcher.MeatUnits.GetValueOrDefault() > 0 && !IsValidId((butcher.MeatDef ?? ""))))
            {
                return false;
            }
            butchered.Add((butcher.CorpseId ?? ""));
        }
        return true;
    }

    /// <summary>Go's <c>validDeliveryCounters</c>.</summary>
    private static bool ValidDeliveryCounters(DeliveryRow row, long tick) =>
        row.Units is not null && row.Units.GetValueOrDefault() >= 0
        && row.Nutrition is not null && CombatNumber(row.Nutrition, true)
        && row.LastTick is not null && row.LastTick.GetValueOrDefault() >= 0 && row.LastTick.GetValueOrDefault() <= tick;
}
