namespace RimGovernor.Brain.Observation;

/// <summary>
/// The Biotech colony section: pollution makers and removers, wastepack stacks, mech gestators and chargers,
/// and baby care. An absent scalar is unknown, never zero; a Fact for the whole section is unknown without
/// Biotech or when the read failed.
/// </summary>
public sealed record BiotechColony
{
    public Fact<int> TotalPollution { get; init; }

    public Fact<uint> PollutableCells { get; init; }

    public Fact<string> ClearAreaID { get; init; }

    public Fact<int> ClearAreaCells { get; init; }

    /// <summary>Counts the polluted pollutable cells.</summary>
    public Fact<uint> PollutedCells { get; init; }

    /// <summary>Counts the polluted pollutable cells outside the clear area.</summary>
    public Fact<uint> PollutedUncoveredCells { get; init; }

    public IReadOnlyList<Polluter> Polluters { get; init; } = [];

    public IReadOnlyList<Wastepack> Wastepacks { get; init; } = [];

    public IReadOnlyList<WastepackAtomizer> Atomizers { get; init; } = [];

    public IReadOnlyList<PollutionPump> Pumps { get; init; } = [];

    public IReadOnlyList<MechGestator> Gestators { get; init; } = [];

    public IReadOnlyList<MechChargerRow> Chargers { get; init; } = [];

    public IReadOnlyList<BabyCare> Babies { get; init; } = [];

    public IReadOnlyList<string> Breastfeeders { get; init; } = [];

    // Gene-building rows. Genes are GeneDef defNames; the totals are the game's own GeneSet values.
    public IReadOnlyList<GeneBank> GeneBanks { get; init; } = [];

    public IReadOnlyList<GeneAssembler> GeneAssemblers { get; init; } = [];

    public IReadOnlyList<GeneExtractor> GeneExtractors { get; init; } = [];

    public IReadOnlyList<Genepack> Genepacks { get; init; } = [];

    public IReadOnlyList<Xenogerm> Xenogerms { get; init; } = [];

    /// <summary>The section's chargers as the recharge policy reads them: charging is a mech attached.</summary>
    public IReadOnlyList<MechCharger> MechChargerRows()
    {
        var output = new List<MechCharger>(Chargers.Count);
        foreach (var c in Chargers)
        {
            // Native sets charging_mech_id exactly while a mech is attached.
            var charging = c.ChargingMechID.TryGetValue(out _);
            output.Add(new MechCharger { Powered = c.Powered, FullOfWaste = c.FullOfWaste, Charging = Known(charging) });
        }
        return output;
    }

    /// <summary>The section's banks and packs as MaintainGeneBank reads them: a pack is loose when it has a map position, held when a bank names it, unknown when it reports neither.</summary>
    public GeneBankFacts GeneBankFacts()
    {
        var banks = new List<GeneBankSlot>();
        foreach (var bank in GeneBanks)
        {
            banks.Add(new GeneBankSlot { Capacity = bank.Capacity, Held = bank.PackIDs.Count });
        }
        var packs = new List<Fact<bool>>();
        foreach (var pack in Genepacks)
        {
            var loose = pack.Position.TryGetValue(out _);
            var held = pack.BankID.TryGetValue(out _);
            packs.Add(loose ? Known(true) : held ? Known(false) : Unknown<bool>());
        }
        return new GeneBankFacts { Banks = EquatableArray.From(banks), Packs = EquatableArray.From(packs) };
    }
}

/// <summary>Heads every building row: the thing id, definition and cell.</summary>
public record BuildingRow
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Position { get; init; }
}

/// <summary>A genepack container; PackIDs are the packs it holds.</summary>
public sealed record GeneBank : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<bool> AutoLoad { get; init; }

    public Fact<int> Capacity { get; init; }

    public IReadOnlyList<string> PackIDs { get; init; } = [];
}

/// <summary>A gene assembler. The run facts (progress, total work, archites owed, can-work verdict) are unknown while it is idle.</summary>
public sealed record GeneAssembler : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<bool> Working { get; init; }

    public Fact<bool> CanWorkNow { get; init; }

    public Fact<double> Progress { get; init; }

    public Fact<double> TotalWork { get; init; }

    public Fact<int> ArchitesOwed { get; init; }

    public Fact<int> MaxComplexity { get; init; }

    public IReadOnlyList<string> PackIDs { get; init; } = [];

    public IReadOnlyList<string> LinkedBankIDs { get; init; } = [];
}

/// <summary>A gene extractor; TicksRemaining and PowerCutTicks are unknown while it is idle, the pawn ids unknown while none is set.</summary>
public sealed record GeneExtractor : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<bool> Working { get; init; }

    public Fact<string> SelectedPawnID { get; init; }

    public Fact<string> OccupantID { get; init; }

    public Fact<int> TicksRemaining { get; init; }

    public Fact<int> PowerCutTicks { get; init; }
}

/// <summary>A gene set's own complexity, metabolism and archite totals.</summary>
public readonly record struct GeneTotals(Fact<int> Complexity, Fact<int> Metabolism, Fact<int> Archites);

/// <summary>A pack in a bank (BankID known, no position) or loose on the map (Position known).</summary>
public sealed record Genepack
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public IReadOnlyList<string> Genes { get; init; } = [];

    public GeneTotals Totals { get; init; }

    public Fact<string> BankID { get; init; }

    public Fact<Cell> Position { get; init; }

    public Fact<bool> Deteriorating { get; init; }

    public Fact<bool> AutoLoad { get; init; }

    public Fact<int> HitPoints { get; init; }
}

/// <summary>A spawned xenogerm item; TargetPawnID is unknown with no pending implant.</summary>
public sealed record Xenogerm : BuildingRow
{
    public IReadOnlyList<string> Genes { get; init; } = [];

    public GeneTotals Totals { get; init; }

    public Fact<string> TargetPawnID { get; init; }

    public Fact<bool> Forbidden { get; init; }

    /// <summary>The game's metabolism after implanting this xenogerm, per pawn it may target, sorted by pawn id.</summary>
    public IReadOnlyList<ImplantMetabolism> ImplantMetabolism { get; init; } = [];
}

/// <summary>GeneUtility.MetabolismAfterImplanting for one pawn; the implant is refused below the biostat range minimum.</summary>
public sealed record ImplantMetabolism(string PawnID, Fact<int> Metabolism);

public sealed record Polluter : BuildingRow
{
    public Fact<bool> Polluting { get; init; }

    public Fact<int> CellsPerDay { get; init; }
}

public sealed record Wastepack : BuildingRow
{
    public int Count { get; init; }

    public Fact<bool> Frozen { get; init; }

    public Fact<bool> Outdoors { get; init; }

    public Fact<bool> InAtomizer { get; init; }

    public Fact<bool> CanDissolveNow { get; init; }

    public Fact<bool> Forbidden { get; init; }

    public Fact<int> DeteriorationRate { get; init; }
}

public sealed record WastepackAtomizer : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<bool> AutoLoad { get; init; }

    public Fact<int> SpaceLeft { get; init; }

    public Fact<int> TicksLeft { get; init; }

    public Fact<double> FillPercent { get; init; }
}

public sealed record PollutionPump : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<bool> DisabledByArtificialBuildings { get; init; }
}

/// <summary>A gestator and its active bill; the bill facts are unknown (not zero) without one.</summary>
public sealed record MechGestator : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<string> BillID { get; init; }

    public Fact<string> State { get; init; }

    public Fact<string> MechKind { get; init; }

    public Fact<string> BoundPawnID { get; init; }

    public Fact<int> CyclesCompleted { get; init; }

    public Fact<int> WasteCount { get; init; }

    public Fact<double> BandwidthCost { get; init; }

    public Fact<double> FormingPercent { get; init; }
}

/// <summary>A mech charger row of the Biotech section (Go <c>observation.MechCharger</c>); <see cref="MechCharger"/> in the policy is the recharge policy's view of it.</summary>
public sealed record MechChargerRow : BuildingRow
{
    public Fact<bool> Powered { get; init; }

    public Fact<bool> FullOfWaste { get; init; }

    public Fact<string> ChargingMechID { get; init; }

    public Fact<int> WasteCount { get; init; }
}

public sealed record BabyCare
{
    public string PawnID { get; init; } = "";

    public Fact<bool> WantsSuckle { get; init; }

    public Fact<bool> CanSuckleNow { get; init; }

    public Fact<bool> BeingPlayedWith { get; init; }

    public Fact<bool> InBed { get; init; }

    public IReadOnlyList<BabyAutofeeder> Autofeeders { get; init; } = [];
}

public sealed record BabyAutofeeder(string PawnID, string Mode);

public static partial class ObservationPackage
{
    private static GeneTotals GeneTotalsOf(bool hasComplexity, int complexity, bool hasMetabolism, int metabolism, bool hasArchites, int archites) =>
        new(Optional(hasComplexity, complexity), Optional(hasMetabolism, metabolism), Optional(hasArchites, archites));

    private static T WithRow<T>(T row, string id, string def, C.Cell? at)
        where T : BuildingRow => row with { ID = id, Definition = def, Position = CellOf(at) };

    /// <summary>Projects a validated section; an absent or unavailable one is an unknown fact.</summary>
    internal static Fact<BiotechColony> ColonyBiotech(O.BiotechSection? section)
    {
        var f = section?.Outcome?.Observed;
        if (f is null)
        {
            return default;
        }
        var p = f.Pollution ?? new O.PollutionTotals();
        var polluters = new List<Polluter>();
        foreach (var x in f.Polluters)
        {
            polluters.Add(WithRow(new Polluter { Polluting = Optional(x.Polluting is not null, x.Polluting.GetValueOrDefault()), CellsPerDay = Optional(x.CellsPerDay is not null, x.CellsPerDay.GetValueOrDefault()) }, x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var wastepacks = new List<Wastepack>();
        foreach (var x in f.Wastepacks)
        {
            wastepacks.Add(WithRow(
                new Wastepack
                {
                    Count = x.Count.GetValueOrDefault(),
                    Frozen = Optional(x.Frozen is not null, x.Frozen.GetValueOrDefault()),
                    Outdoors = Optional(x.Outdoors is not null, x.Outdoors.GetValueOrDefault()),
                    InAtomizer = Optional(x.InAtomizer is not null, x.InAtomizer.GetValueOrDefault()),
                    CanDissolveNow = Optional(x.CanDissolveNow is not null, x.CanDissolveNow.GetValueOrDefault()),
                    Forbidden = Optional(x.Forbidden is not null, x.Forbidden.GetValueOrDefault()),
                    DeteriorationRate = Optional(x.DeteriorationRate is not null, x.DeteriorationRate.GetValueOrDefault()),
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var atomizers = new List<WastepackAtomizer>();
        foreach (var x in f.Atomizers)
        {
            atomizers.Add(WithRow(
                new WastepackAtomizer
                {
                    Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()),
                    AutoLoad = Optional(x.AutoLoad is not null, x.AutoLoad.GetValueOrDefault()),
                    SpaceLeft = Optional(x.SpaceLeft is not null, x.SpaceLeft.GetValueOrDefault()),
                    TicksLeft = Optional(x.TicksLeft is not null, x.TicksLeft.GetValueOrDefault()),
                    FillPercent = Optional(x.FillPercent is not null, x.FillPercent.GetValueOrDefault()),
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var pumps = new List<PollutionPump>();
        foreach (var x in f.Pumps)
        {
            pumps.Add(WithRow(
                new PollutionPump { Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()), DisabledByArtificialBuildings = Optional(x.DisabledByArtificialBuildings is not null, x.DisabledByArtificialBuildings.GetValueOrDefault()) },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var gestators = new List<MechGestator>();
        foreach (var x in f.Gestators)
        {
            gestators.Add(WithRow(
                new MechGestator
                {
                    Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()),
                    BillID = Optional(x.BillId is not null, x.BillId ?? ""),
                    State = Optional(x.State is not null, x.State ?? ""),
                    MechKind = Optional(x.MechKind is not null, x.MechKind ?? ""),
                    BoundPawnID = Optional(x.BoundPawnId is not null, x.BoundPawnId ?? ""),
                    CyclesCompleted = Optional(x.CyclesCompleted is not null, x.CyclesCompleted.GetValueOrDefault()),
                    WasteCount = Optional(x.WasteCount is not null, x.WasteCount.GetValueOrDefault()),
                    BandwidthCost = Optional(x.BandwidthCost is not null, x.BandwidthCost.GetValueOrDefault()),
                    FormingPercent = Optional(x.FormingPercent is not null, x.FormingPercent.GetValueOrDefault()),
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var chargers = new List<MechChargerRow>();
        foreach (var x in f.Chargers)
        {
            chargers.Add(WithRow(
                new MechChargerRow
                {
                    Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()),
                    FullOfWaste = Optional(x.FullOfWaste is not null, x.FullOfWaste.GetValueOrDefault()),
                    ChargingMechID = Optional(x.ChargingMechId is not null, x.ChargingMechId ?? ""),
                    WasteCount = Optional(x.WasteCount is not null, x.WasteCount.GetValueOrDefault()),
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var babies = new List<BabyCare>();
        foreach (var x in f.Babies)
        {
            var feeders = new List<BabyAutofeeder>();
            foreach (var a in x.Autofeeders)
            {
                feeders.Add(new BabyAutofeeder(a.PawnId ?? "", a.Mode ?? ""));
            }
            babies.Add(new BabyCare
            {
                PawnID = x.PawnId ?? "",
                WantsSuckle = Optional(x.WantsSuckle is not null, x.WantsSuckle.GetValueOrDefault()),
                CanSuckleNow = Optional(x.CanSuckleNow is not null, x.CanSuckleNow.GetValueOrDefault()),
                BeingPlayedWith = Optional(x.BeingPlayedWith is not null, x.BeingPlayedWith.GetValueOrDefault()),
                InBed = Optional(x.InBed is not null, x.InBed.GetValueOrDefault()),
                Autofeeders = feeders,
            });
        }
        var banks = new List<GeneBank>();
        foreach (var x in f.GeneBanks)
        {
            banks.Add(WithRow(
                new GeneBank { Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()), AutoLoad = Optional(x.AutoLoad is not null, x.AutoLoad.GetValueOrDefault()), Capacity = Optional(x.Capacity is not null, x.Capacity.GetValueOrDefault()), PackIDs = [.. x.PackIds] },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var assemblers = new List<GeneAssembler>();
        foreach (var x in f.GeneAssemblers)
        {
            assemblers.Add(WithRow(
                new GeneAssembler
                {
                    Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()),
                    Working = Optional(x.Working is not null, x.Working.GetValueOrDefault()),
                    CanWorkNow = Optional(x.CanWorkNow is not null, x.CanWorkNow.GetValueOrDefault()),
                    Progress = Optional(x.Progress is not null, x.Progress.GetValueOrDefault()),
                    TotalWork = Optional(x.TotalWork is not null, x.TotalWork.GetValueOrDefault()),
                    ArchitesOwed = Optional(x.ArchitesOwed is not null, x.ArchitesOwed.GetValueOrDefault()),
                    MaxComplexity = Optional(x.MaxComplexity is not null, x.MaxComplexity.GetValueOrDefault()),
                    PackIDs = [.. x.PackIds],
                    LinkedBankIDs = [.. x.LinkedBankIds],
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var extractors = new List<GeneExtractor>();
        foreach (var x in f.GeneExtractors)
        {
            extractors.Add(WithRow(
                new GeneExtractor
                {
                    Powered = Optional(x.Powered is not null, x.Powered.GetValueOrDefault()),
                    Working = Optional(x.Working is not null, x.Working.GetValueOrDefault()),
                    SelectedPawnID = Optional(x.SelectedPawnId is not null, x.SelectedPawnId ?? ""),
                    OccupantID = Optional(x.OccupantId is not null, x.OccupantId ?? ""),
                    TicksRemaining = Optional(x.TicksRemaining is not null, x.TicksRemaining.GetValueOrDefault()),
                    PowerCutTicks = Optional(x.PowerCutTicks is not null, x.PowerCutTicks.GetValueOrDefault()),
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        var packs = new List<Genepack>();
        foreach (var x in f.Genepacks)
        {
            packs.Add(new Genepack
            {
                ID = x.ThingId ?? "",
                Definition = x.DefName ?? "",
                Genes = [.. x.Genes],
                Totals = GeneTotalsOf(x.Complexity is not null, x.Complexity.GetValueOrDefault(), x.Metabolism is not null, x.Metabolism.GetValueOrDefault(), x.Archites is not null, x.Archites.GetValueOrDefault()),
                BankID = Optional(x.BankId is not null, x.BankId ?? ""),
                Deteriorating = Optional(x.Deteriorating is not null, x.Deteriorating.GetValueOrDefault()),
                AutoLoad = Optional(x.AutoLoad is not null, x.AutoLoad.GetValueOrDefault()),
                HitPoints = Optional(x.HitPoints is not null, x.HitPoints.GetValueOrDefault()),
                Position = x.Position is null ? default : Known(CellOf(x.Position)),
            });
        }
        var xenogerms = new List<Xenogerm>();
        foreach (var x in f.Xenogerms)
        {
            var implants = new List<ImplantMetabolism>();
            foreach (var m in x.ImplantMetabolism)
            {
                implants.Add(new ImplantMetabolism(m.PawnId ?? "", Optional(m.MetabolismAfter is not null, m.MetabolismAfter.GetValueOrDefault())));
            }
            xenogerms.Add(WithRow(
                new Xenogerm
                {
                    Genes = [.. x.Genes],
                    Totals = GeneTotalsOf(x.Complexity is not null, x.Complexity.GetValueOrDefault(), x.Metabolism is not null, x.Metabolism.GetValueOrDefault(), x.Archites is not null, x.Archites.GetValueOrDefault()),
                    TargetPawnID = Optional(x.TargetPawnId is not null, x.TargetPawnId ?? ""),
                    Forbidden = Optional(x.Forbidden is not null, x.Forbidden.GetValueOrDefault()),
                    ImplantMetabolism = implants,
                },
                x.ThingId ?? "", x.DefName ?? "", x.Position));
        }
        return Known(new BiotechColony
        {
            TotalPollution = Optional(p.TotalPollution is not null, p.TotalPollution.GetValueOrDefault()),
            PollutableCells = Optional(p.PollutableCells is not null, p.PollutableCells.GetValueOrDefault()),
            ClearAreaID = Optional(p.ClearAreaId is not null, p.ClearAreaId ?? ""),
            ClearAreaCells = Optional(p.ClearAreaCells is not null, p.ClearAreaCells.GetValueOrDefault()),
            PollutedCells = Optional(p.PollutedCells is not null, p.PollutedCells.GetValueOrDefault()),
            PollutedUncoveredCells = Optional(p.PollutedUncoveredCells is not null, p.PollutedUncoveredCells.GetValueOrDefault()),
            Breastfeeders = [.. f.Breastfeeders],
            Polluters = polluters,
            Wastepacks = wastepacks,
            Atomizers = atomizers,
            Pumps = pumps,
            Gestators = gestators,
            Chargers = chargers,
            Babies = babies,
            GeneBanks = banks,
            GeneAssemblers = assemblers,
            GeneExtractors = extractors,
            Genepacks = packs,
            Xenogerms = xenogerms,
        });
    }

    /// <summary>The section's chargers as a fact, unknown with the section.</summary>
    internal static Fact<EquatableArray<MechCharger>> MechChargerFact(Fact<BiotechColony> section) =>
        section.TryGetValue(out var b) ? Known(EquatableArray.From(b.MechChargerRows())) : Unknown<EquatableArray<MechCharger>>();

    /// <summary>The catalog definitions that are mech chargers (PlanningDefinition.MechCharger, from the game's Building_MechCharger class), in catalog order; the build planner picks among them.</summary>
    public static IReadOnlyList<PlanningDefinition> MechChargerDefs(IEnumerable<PlanningDefinition> defs)
    {
        var output = new List<PlanningDefinition>();
        foreach (var d in defs)
        {
            if (d.MechCharger.TryGetValue(out var charger) && charger)
            {
                output.Add(d);
            }
        }
        return output;
    }

    /// <summary>The section's gene-building rows as a fact, unknown with the section.</summary>
    internal static Fact<GeneBankFacts> GeneBankFact(Fact<BiotechColony> section) =>
        section.TryGetValue(out var b) ? Known(b.GeneBankFacts()) : Unknown<GeneBankFacts>();
}
