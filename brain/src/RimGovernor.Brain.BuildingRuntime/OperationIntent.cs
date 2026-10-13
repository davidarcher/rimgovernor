using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Port of operation_intent.go: the Operation.intent text a method's writes carry.</summary>
public static class OperationIntent
{
    // The in-game names of Concerns whose split identifier reads poorly; every other kind falls back to its words.
    private static readonly Dictionary<ConcernId, string> ConcernLabels = new()
    {
        [EnsureFoodSupply] = "Food supply",
        [MaintainFoodStorage] = "Food storage",
        [MaintainResource] = "Resource",
        [MaintainTrade] = "Export goods",
        [MaintainFlooring] = "Flooring",
        [MaintainHousing] = "Housing",
        [EnsureTemperatureSafety] = "Temperature",
        [EnsureCooking] = "Cooking",
        [EnsureBasicPower] = "Power",
        [EnsureBasicDefense] = "Defense",
        [EnsureDefensiveLayout] = "Defense layout",
        [CriticalMedicine] = "Medical",
        [MaintainMedicalReserves] = "Medicine",
        [MaintainSurgery] = "Surgery",
        [MaintainRefrigeration] = "Refrigeration",
        [EnsureComfort] = "Comfort",
        [EnsureResearch] = "Research",
        [EnsureWorkAssignments] = "Work",
        [ManageSupplySafety] = "Supply safety",
        [MaintainEquipment] = "Equipment",
        [MaintainEssentialRepairs] = "Repairs",
        [TradeWithCaravan] = "Trade",
        [ActiveCombat] = "Combat",
        [RemoveBlight] = "Blight",
        [ManagePollution] = "Pollution",
        [EnsureMechCharger] = "Mech charger",
        [MaintainGeneBank] = "Gene bank",
        [MaintainWorkLedger] = "Production orders",
        [MaintainIncineration] = "Incineration",
        [MaintainTraining] = "Training",
        [MaintainClimateEnvelope] = "Climate envelope",
        [MaintainHerd] = "Herd",
        [MaintainPopulation] = "Population",
        [Concerns.TradeMission] = "Trade mission",
    };

    /// <summary>The readable name of a Concern.</summary>
    public static string ConcernLabel(ConcernId kind)
    {
        if (ConcernLabels.TryGetValue(kind, out var label))
        {
            return label;
        }
        var name = kind.Value;
        foreach (var prefix in new[] { "Maintain", "Ensure", "Manage" })
        {
            name = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        }
        return SplitWords(name);
    }

    /// <summary>The method id without its identity hash: "hunt-&lt;hex&gt;" is "hunt", "comfort-Armchair" stays whole.</summary>
    private static string MethodReason(MethodId method)
    {
        var parts = method.Value.Split('-').ToList();
        var n = parts.Count;
        if (n > 1 && parts[n - 1].Length >= 16 && parts[n - 1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            parts.RemoveAt(n - 1);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Operation.intent for a method's writes: the planner's admission reason follows the method, "Food supply:
    /// acquire, food runway 1.5d".
    /// </summary>
    public static string Of(PlanMethod method)
    {
        var why = MethodReason(method.Method);
        var reason = method.Reason.Trim();
        if (reason.Length > 0)
        {
            why += ", " + reason;
        }
        return BridgeApi.FormatOperationIntent(ConcernLabel(method.Concern), why);
    }

    private static string SplitWords(string s)
    {
        var b = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var r = s[i];
            if (i > 0 && char.IsUpper(r))
            {
                b.Append(' ');
                r = char.ToLowerInvariant(r);
            }
            b.Append(r);
        }
        return b.ToString();
    }

    /// <summary>
    /// The plan's method intent for the writes the dispatch issues, or null: a plan no goal admitted, or a failed
    /// lookup, dispatches without one. Go carried it on the context; callers wrap the dispatch in
    /// <c>BridgeApi.WithOperationIntent(intent)</c> when this is not null.
    /// </summary>
    public static async Task<string?> PlanIntentAsync(JournalStore intents, PlanId plan, CancellationToken ct)
    {
        try
        {
            var method = await intents.PlanMethodAsync(plan, ct).ConfigureAwait(false);
            return method is null ? null : Of(method);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }
}
