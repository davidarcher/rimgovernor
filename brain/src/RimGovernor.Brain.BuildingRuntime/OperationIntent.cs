using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Port of operation_intent.go: the Operation.intent text a method's writes carry.</summary>
public static class OperationIntent
{
    // The in-game names of Concerns whose split identifier reads poorly; every other kind falls back to its words.
    private static readonly Dictionary<ConcernId, string> ConcernLabels = new()
    {
        [FoodSupplyNeed.FilingKey] = "Food supply",
        [FoodStorageNeed.FilingKey] = "Food storage",
        [EconomyFamily.Resource.FilingKey] = "Resource",
        [EconomyFamily.Trade.FilingKey] = "Export goods",
        [RoomsFamily.Flooring.FilingKey] = "Flooring",
        [RoomsFamily.Lighting.FilingKey] = "Lighting",
        [RoomsFamily.Art.FilingKey] = "Art",
        [ShelterFamily.Starter.FilingKey] = "Starter shelter",
        [ShelterFamily.Sleeping.FilingKey] = "Housing",
        [ShelterFamily.Expansion.FilingKey] = "Housing expansion",
        [ShelterFamily.Temperature.FilingKey] = "Temperature",
        [ShelterFamily.SafeArea.FilingKey] = "Safe area",
        [CookingNeed.FilingKey] = "Cooking",
        [PowerFamily.Basic.FilingKey] = "Power",
        [MilitaryFamily.Defense.FilingKey] = "Defense",
        [MilitaryFamily.Pests.FilingKey] = "Clear pests",
        [LayoutFamily.Defense.FilingKey] = "Defense layout",
        [CriticalCensusNeed.FilingKey] = "Medical",
        [MedicalCareNeed.FilingKey] = "Medical care",
        [MedicalReservesNeed.FilingKey] = "Medicine",
        [SurgeryNeed.FilingKey] = "Surgery",
        [RefrigerationNeed.FilingKey] = "Refrigeration",
        [RoomsFamily.Comfort.FilingKey] = "Comfort",
        [RoomsFamily.Hosting.FilingKey] = "Hosting rooms",
        [RoomsFamily.PartySpot.FilingKey] = "Party spot",
        [RoomsFamily.Gatherings.FilingKey] = "Gatherings",
        [EconomyFamily.Research.FilingKey] = "Research",
        [AdminFamily.WorkAssignments.FilingKey] = "Work",
        [AdminFamily.SupplySafety.FilingKey] = "Supply safety",
        [AdminFamily.Burial.FilingKey] = "Burial",
        [HomeUpkeepFamily.Repairs.FilingKey] = "Repairs",
        [HomeUpkeepFamily.Clearance.FilingKey] = "Clear home obstructions",
        [AdminFamily.CreepJoiners.FilingKey] = "Creep joiners",
        [EconomyFamily.Caravan.FilingKey] = "Trade",
        [MilitaryFamily.Combat.FilingKey] = "Combat",
        [BlightNeed.FilingKey] = "Blight",
        [BiotechFamily.Mechs.FilingKey] = "Mechs",
        [BiotechFamily.MechCharger.FilingKey] = "Mech charger",
        [BiotechFamily.GeneBank.FilingKey] = "Gene bank",
        [BiotechFamily.Pollution.FilingKey] = "Pollution",
        [RoyaltyFamily.Permits.FilingKey] = "Permits",
        [RoyaltyFamily.Psylink.FilingKey] = "Psylink",
        [AdminFamily.WorkLedger.FilingKey] = "Production orders",
        [MilitaryFamily.Training.FilingKey] = "Training",
        [LayoutFamily.ClimateEnvelope.FilingKey] = "Climate envelope",
        [LayoutFamily.HomeCoverage.FilingKey] = "Home coverage",
        [LayoutFamily.Firebreak.FilingKey] = "Firebreak",
        [LayoutFamily.StoneShell.FilingKey] = "Stone shell",
        [MilitaryFamily.Equipment.FilingKey] = "Equipment",
        [AnimalsFamily.Herd.FilingKey] = "Herd",
        [AnimalsFamily.Containment.FilingKey] = "Animal containment",
        [AnimalsFamily.Ward.FilingKey] = "Animal ward",
        [AnimalsFamily.Sterilize.FilingKey] = "Sterilization",
        [IdeologyFamily.Roles.FilingKey] = "Ideoligion roles",
        [IdeologyFamily.Rituals.FilingKey] = "Rituals",
        [IdeologyFamily.Reform.FilingKey] = "Ideoligion reform",
        [JoinersFamily.Offers.FilingKey] = "Joiners",
        [JoinersFamily.Letters.FilingKey] = "Joiner letters",
        [JoinersFamily.Ceremony.FilingKey] = "Ceremony",
        [JoinersFamily.Quests.FilingKey] = "Quests",
        [JoinersFamily.Decrees.FilingKey] = "Royal decrees",
        [JoinersFamily.Monuments.FilingKey] = "Quest monuments",
        [JoinersFamily.Departures.FilingKey] = "Quest departures",
        [JoinersFamily.GravInspection.FilingKey] = "Grav inspection",
        [AnomalyFamily.Doors.FilingKey] = "Containment doors",
        [AnomalyFamily.Capture.FilingKey] = "Entity capture",
        [AnomalyFamily.Tend.FilingKey] = "Entity care",
        [AnomalyFamily.Lance.FilingKey] = "Lance",
        [AnomalyFamily.Bioferrite.FilingKey] = "Bioferrite extraction",
        [AnomalyFamily.Monolith.FilingKey] = "Monolith",
        [PrisonersFamily.Recruit.FilingKey] = "Prisoners",
        [PrisonersFamily.Custody.FilingKey] = "Custody",
        [EconomyFamily.TradeMission.FilingKey] = "Trade mission",
    };

    /// <summary>The readable name of a Concern.</summary>
    public static string ConcernLabel(ConcernId kind)
    {
        if (ConcernLabels.TryGetValue(kind, out var label))
        {
            return label;
        }
        // A patient's critical Need (medical/critical/<pawn>) reads as the medical emergency it is (#3258).
        if (kind.Value.StartsWith(CriticalCensusNeed.Value + "/", StringComparison.Ordinal))
        {
            return "Medical";
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
    public static string Of(PlanMethodBinding method)
    {
        var why = MethodReason(method.Method);
        var reason = method.Reason.Trim();
        if (reason.Length > 0)
        {
            why += ", " + reason;
        }
        return FormatOperationIntent(ConcernLabel(method.Concern), why);
    }

    private const int MaxOperationIntent = 80;

    /// <summary>Renders "label: reason" as printable ASCII, whitespace collapsed and capped at 80 characters (bridge.FormatOperationIntent).</summary>
    private static string FormatOperationIntent(string label, string reason)
    {
        label = AsciiLine(label);
        reason = AsciiLine(reason);
        var output = label;
        if (label.Length != 0 && reason.Length != 0)
        {
            output = label + ": " + reason;
        }
        else if (reason.Length != 0)
        {
            output = reason;
        }
        if (output.Length > MaxOperationIntent)
        {
            output = output[..MaxOperationIntent].TrimEnd(' ', ':');
        }
        return output;
    }

    private static string AsciiLine(string s)
    {
        var b = new StringBuilder();
        foreach (var r in s)
        {
            if (r is '\t' or '\n' or '\r')
            {
                b.Append(' ');
            }
            else if (r >= 0x20 && r < 0x7f)
            {
                b.Append(r);
            }
        }
        return string.Join(' ', b.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
    /// <c>bridge.WithOperationIntent(intent)</c> when this is not null.
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
