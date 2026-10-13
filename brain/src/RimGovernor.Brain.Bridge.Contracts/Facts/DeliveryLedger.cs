using RimGovernor.Protocol.Observations;

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
        if (ledger is null || !ledger.HasEpoch || !IsValidId(ledger.Epoch))
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
            var kind = row.SourceKind;
            if (kind is < DeliverySourceKind.Crop or > DeliverySourceKind.AnimalProduct
                || !ValidDeliveryCounters(row, tick)
                || !IsValidId(row.SourceId)
                || !IsValidId(row.DefName)
                || !seen.Add((kind.ToString(), row.SourceId, row.DefName)))
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
                || !IsValidId(kill.CorpseId)
                || killed.Contains(kill.CorpseId)
                || !IsValidId(kill.Race)
                || !IsValidId(kill.PawnId)
                || !kill.HasBodySize
                || !CombatNumber(kill.BodySize, true)
                || !kill.HasPotentialNutrition
                || !CombatNumber(kill.PotentialNutrition, true)
                || !kill.HasTick
                || kill.Tick < 0
                || kill.Tick > tick)
            {
                return false;
            }
            killed.Add(kill.CorpseId);
        }
        var butchered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var butcher in ledger.Butchers)
        {
            if (butcher is null
                || !IsValidId(butcher.CorpseId)
                || butchered.Contains(butcher.CorpseId)
                || !IsValidId(butcher.Recipe)
                || !butcher.HasMeatUnits
                || butcher.MeatUnits < 0
                || !butcher.HasMeatNutrition
                || !CombatNumber(butcher.MeatNutrition, true)
                || !butcher.HasLeatherUnits
                || butcher.LeatherUnits < 0
                || !butcher.HasTick
                || butcher.Tick < 0
                || butcher.Tick > tick
                || (butcher.MeatUnits > 0 && !IsValidId(butcher.MeatDef)))
            {
                return false;
            }
            butchered.Add(butcher.CorpseId);
        }
        return true;
    }

    /// <summary>Go's <c>validDeliveryCounters</c>.</summary>
    private static bool ValidDeliveryCounters(DeliveryRow row, long tick) =>
        row.HasUnits && row.Units >= 0
        && row.HasNutrition && CombatNumber(row.Nutrition, true)
        && row.HasLastTick && row.LastTick >= 0 && row.LastTick <= tick;
}
