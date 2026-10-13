using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using DomainPawnId = RimGovernor.Brain.Domain.PawnId;
using PolicyMissingPart = RimGovernor.Brain.Policy.MissingPart;
using PolicyInstalledPart = RimGovernor.Brain.Policy.InstalledPart;
using PolicySurgeryKind = RimGovernor.Brain.Policy.SurgeryKind;
using PolicySurgeryOperation = RimGovernor.Brain.Policy.SurgeryOperation;
using WireSurgeryKind = RimGovernor.Contracts.Wire.Observations.SurgeryKind;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// The catalog answers surgery facts read per recipe and hediff: the part item a recipe installs, its tier and the
/// item a hediff spawns on removal. The bridge definition catalog implements it; the port keeps the dependency a
/// seam because the catalog derivations live in their own project. Every method throws when the catalog has no row
/// for the name.
/// </summary>
public interface IPartCatalog
{
    /// <summary>The part item the recipe installs; default when it installs none.</summary>
    Resource InstallItem(string recipe);

    /// <summary>The recipe's installed part tier.</summary>
    double PartTier(string recipe);

    /// <summary>The hediff def's partEfficiency.</summary>
    double HediffPartTier(string hediff);

    /// <summary>The hediff's spawnThingOnRemoved item; unknown when the part spawns nothing.</summary>
    Fact<Resource> HediffSpawnOnRemoved(string hediff);
}

// Go surgery_facts.go.
public static partial class BridgeFunctions
{
    private static PolicySurgeryKind SurgeryKindOf(WireSurgeryKind kind) => kind switch
    {
        WireSurgeryKind.Restore => PolicySurgeryKind.Restore,
        WireSurgeryKind.Cure => PolicySurgeryKind.Cure,
        WireSurgeryKind.Amputate => PolicySurgeryKind.Amputate,
        WireSurgeryKind.Install => PolicySurgeryKind.Install,
        WireSurgeryKind.Harvest => PolicySurgeryKind.Harvest,
        WireSurgeryKind.Other => PolicySurgeryKind.Other,
        _ => PolicySurgeryKind.Unknown,
    };

    /// <summary>
    /// Go's <c>SurgeryFacts</c>: maps the native surgery facts as observed, for a colonist's care read and a
    /// prisoner's population row. A list carrying a read issue is unknown; values are never recomputed here. Each
    /// operation carries the part item its recipe installs, read from the catalog's recipe rows; a recipe the
    /// catalog has no row for throws. Without a catalog (<paramref name="catalog"/> null) the weapon drop and
    /// inspections read recipe names only, no part items.
    /// </summary>
    public static (Fact<EquatableArray<PolicyMissingPart>> Parts, Fact<EquatableArray<PolicySurgeryOperation>> Operations) SurgeryFacts(PawnHealth health, IPartCatalog? catalog)
    {
        var parts = Unknown<EquatableArray<PolicyMissingPart>>();
        var operations = Unknown<EquatableArray<PolicySurgeryOperation>>();
        if (!IssueNames(health.Issues, "missing_parts"))
        {
            var rows = new List<PolicyMissingPart>(health.MissingParts.Count);
            foreach (var part in health.MissingParts)
            {
                rows.Add(
                    new PolicyMissingPart
                    {
                        PartIndex = OptionalInt(part.PartIndex is not null, part.PartIndex.GetValueOrDefault()),
                        ParentIndex = OptionalInt(part.ParentIndex is not null, part.ParentIndex.GetValueOrDefault()),
                        PartDefName = OptionalFact(part.PartDefName is not null, (part.PartDefName ?? "")),
                        ParentDefName = OptionalFact(part.ParentDefName is not null, (part.ParentDefName ?? "")),
                        Vital = OptionalFact(part.Vital is not null, part.Vital.GetValueOrDefault()),
                    });
            }
            parts = Known(EquatableArray.From(rows));
        }
        if (!IssueNames(health.Issues, "operations"))
        {
            var rows = new List<PolicySurgeryOperation>(health.Operations.Count);
            foreach (var op in health.Operations)
            {
                rows.Add(SurgeryOperationOf(op, catalog));
            }
            operations = Known(EquatableArray.From(rows));
        }
        return (parts, operations);
    }

    private static PolicySurgeryOperation SurgeryOperationOf(RimGovernor.Contracts.Wire.Observations.SurgeryOperation op, IPartCatalog? catalog)
    {
        var row = new PolicySurgeryOperation
        {
            PartIndex = OptionalInt(op.PartIndex is not null, op.PartIndex.GetValueOrDefault()),
            PartDefName = OptionalFact(op.PartDefName is not null, (op.PartDefName ?? "")),
            Kind = SurgeryKindOf(op.Kind),
            SuccessChance = OptionalFact(op.SuccessChance is not null, op.SuccessChance.GetValueOrDefault()),
            DoctorSuccessChance = OptionalFact(op.DoctorSuccessChance is not null, op.DoctorSuccessChance.GetValueOrDefault()),
            IngredientsOnMap = OptionalFact(op.IngredientsOnMap is not null, op.IngredientsOnMap.GetValueOrDefault()),
            Violation = OptionalFact(op.Violation is not null, op.Violation.GetValueOrDefault()),
            Lethal = OptionalFact(op.Lethal is not null, op.Lethal.GetValueOrDefault()),
            YieldValue = OptionalFact(op.YieldMarketValue is not null, op.YieldMarketValue.GetValueOrDefault()),
            AddedPart = OptionalFact(op.AddedPartHediff is not null, (op.AddedPartHediff ?? "")),
            YieldThing = OptionalFact(op.YieldThingDef is not null, (op.YieldThingDef ?? "")),
            MedicineValue = OptionalFact(op.MedicineMarketValue is not null, op.MedicineMarketValue.GetValueOrDefault()),
            CareLimited = OptionalFact(op.MedicineCareLimited is not null, op.MedicineCareLimited.GetValueOrDefault()),
            EligibleDoctors = op.EligibleDoctors is not null ? Known((int)op.EligibleDoctors.GetValueOrDefault()) : Unknown<int>(),
        };
        if (op.Recipe is { } recipe)
        {
            row = row with { Recipe = OptionalFact(recipe.DefName is not null, (recipe.DefName ?? "")) };
            if (recipe.DefName is not null && catalog is not null)
            {
                row = row with { Item = catalog.InstallItem((recipe.DefName ?? "")), Tier = catalog.PartTier((recipe.DefName ?? "")) };
            }
        }
        if (op.DoctorChances.Count > 0)
        {
            var chances = new Dictionary<DomainPawnId, double>(op.DoctorChances.Count);
            foreach (var (doctor, chance) in op.DoctorChances)
            {
                chances[new DomainPawnId(doctor)] = chance;
            }
            row = row with { DoctorChances = chances };
        }
        return row;
    }

    /// <summary>
    /// Go's <c>QueuedSurgeryItems</c>: lists the part item each queued medical bill installs, by the catalog's
    /// recipe rows; a bill that installs no item adds none.
    /// </summary>
    public static IReadOnlyList<Resource> QueuedSurgeryItems(IEnumerable<string> recipes, IPartCatalog catalog)
    {
        var items = new List<Resource>();
        foreach (var recipe in recipes)
        {
            var item = catalog.InstallItem(recipe);
            if (item != default)
            {
                items.Add(item);
            }
        }
        return items;
    }

    /// <summary>Go's <c>QueuedSurgeries</c>: counts the medical bills on the patient; unknown when the bill stack carried a read issue.</summary>
    public static Fact<int> QueuedSurgeries(PawnHealth health) =>
        IssueNames(health.Issues, "surgery_bills") ? Unknown<int>() : Known(health.SurgeryBills.Count);

    /// <summary>Go's <c>QueuedSurgeryRecipes</c>: each medical bill's recipe on the patient; null when the bill stack carried a read issue.</summary>
    public static IReadOnlyList<string>? QueuedSurgeryRecipes(PawnHealth health) =>
        IssueNames(health.Issues, "surgery_bills")
            ? null
            : [.. health.SurgeryBills.Where(bill => bill.Recipe is not null && (bill.Recipe ?? "").Length > 0).Select(bill => (bill.Recipe ?? ""))];

    /// <summary>
    /// Go's <c>InstalledParts</c>: maps the native installed added parts. A read issue on installed_parts is
    /// unknown, never an empty list. The tier is the hediff def's partEfficiency from the catalog; the priced item
    /// is the hediff's spawnThingOnRemoved, unknown when the part spawns nothing.
    /// </summary>
    public static Fact<EquatableArray<PolicyInstalledPart>> InstalledParts(PawnHealth health, IPartCatalog catalog)
    {
        if (IssueNames(health.Issues, "installed_parts"))
        {
            return Unknown<EquatableArray<PolicyInstalledPart>>();
        }
        var rows = new List<PolicyInstalledPart>(health.InstalledParts.Count);
        foreach (var part in health.InstalledParts)
        {
            var hediff = part.Definition?.DefName ?? "";
            if (hediff.Length == 0)
            {
                continue;
            }
            rows.Add(
                new PolicyInstalledPart
                {
                    Hediff = hediff,
                    Part = OptionalFact(part.PartDefName is not null, (part.PartDefName ?? "")),
                    PartIndex = OptionalInt(part.PartIndex is not null, part.PartIndex.GetValueOrDefault()),
                    Tier = catalog.HediffPartTier(hediff),
                    Item = catalog.HediffSpawnOnRemoved(hediff),
                });
        }
        return Known(EquatableArray.From(rows));
    }
}
