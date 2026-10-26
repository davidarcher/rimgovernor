using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>The sleeping and royal room prerequisites over canonical reviewed facts. Reads remain ordered by the selected path.</summary>
public static class SleepingPlanningRules
{
    private static IReadOnlyList<SiteCell> RequiredCells(IReadOnlyList<SiteCell>? cells) => cells ?? throw new RequiredFactException("planning window");

    public static Dictionary<PawnId, TraitEffects>? SleepingTraits(ReviewFacts facts)
    {
        var pawns = facts.ForecastObserved.WorkPawns.Pawns;
        var output = new Dictionary<PawnId, TraitEffects>(pawns.Count);
        foreach (var p in pawns)
        {
            output[p.Id] = PawnRules.BuildProfile(p).Effects;
        }
        return output;
    }

    public static (BedroomSwap Swap, bool Ok) BedroomSwapOf(ReviewFacts facts)
    {
        var obs = facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (traits is null)
        {
            return (new BedroomSwap(), false);
        }
        var rooms = facts.Upkeep.Rooms;
        if (facts.LayoutPlan is not { } plan)
        {
            return (new BedroomSwap(), false);
        }
        return RoomQualityRules.NextBedroomSwap(obs, traits, RoomQualityRules.SuiteRoomIds(plan, rooms));
    }

    public static Dictionary<string, RoomTarget>? BedroomTargets(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished)
    {
        var obs = facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (traits is null)
        {
            return null;
        }
        var tier = TechTierRules.SelectTechTier(finished, facts.Census.PlayerTechLevel);
        return RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness);
    }

    public static BedroomStep MigrateStep(ReviewFacts facts)
    {
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Upkeep.Rooms;
        var sleeping = facts.Sleeping;
        if (!pk)
        {
            return new BedroomStep();
        }
        return SleepingRules.NextMigrateStep(plan!, rooms!, sleeping!);
    }

    public static IReadOnlyDictionary<string, RoomTarget> UpgradeTargetsOf(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished, IReadOnlyDictionary<string, RoomTarget>? targets)
    {
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Upkeep.Rooms;
        var sleeping = facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (!pk || traits is null)
        {
            return targets ?? new Dictionary<string, RoomTarget>();
        }
        var claims = RoomQualityRules.SuiteClaims(plan!, rooms!, sleeping!, BedroomTargets(facts, finished) ?? new Dictionary<string, RoomTarget>(), traits, SuitePressure(facts) ?? new Dictionary<PawnId, double>());
        return RoomQualityRules.UpgradeTargets(targets ?? new Dictionary<string, RoomTarget>(), sleeping!, claims);
    }

    public static bool BedroomsOwedOf(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished, IReadOnlyList<SiteCell>? cells)
    {
        if (SleepingRules.BedroomsOwed(facts.LayoutPlan, facts.Upkeep.Rooms, facts.Sleeping, BedroomTargets(facts, finished), SleepingTraits(facts), SuitePressure(facts)))
        {
            return true;
        }
        if (facts.LayoutPlan is null)
        {
            return false;
        }
        return BedroomSwapOf(facts).Ok
            || MigrateStep(facts).Kind != BedroomStepKind.None
            || RoomUpgradeOf(facts, finished, cells).Due
            || CompanionBed(facts, cells).Due
            || ThroneStepOf(facts, cells).Owed
            || ChildRoomStep(facts, finished, cells).Owed();
    }

    public static Dictionary<PawnId, double>? SuitePressure(ReviewFacts facts) =>
        SuitePriorityRules.SuitePressure(facts.MoodPawns);

    public static (RoomUpgrade Upgrade, bool Due) RoomUpgradeOf(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished, IReadOnlyList<SiteCell>? cells)
    {
        var obs = facts.Sleeping;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (census is null || traits is null)
        {
            return (new RoomUpgrade(), false);
        }
        var tier = TechTierRules.SelectTechTier(finished, facts.Census.PlayerTechLevel);
        var targets = RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness);
        foreach (var (id, t) in RoomQualityRules.CommonRoomTargets(obs!, tier, facts.Impressiveness))
        {
            targets.TryAdd(id, t);
        }
        targets = WithRequirementTargets(facts, targets);
        return RoomQualityRules.NextRoomUpgrade(obs!, UpgradeTargetsOf(facts, finished, targets), FurnitureRoomsRules.FurnitureRooms(rooms!, census, RequiredCells(cells)), def => ColonyPlanningRules.DefinitionAvailable(facts, def));
    }

    public static (BedReplacement Replacement, bool Due) BedReplacementOf(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished, IReadOnlyList<SiteCell>? cells)
    {
        var obs = facts.Sleeping;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (census is null || traits is null)
        {
            return (new BedReplacement(), false);
        }
        var tier = TechTierRules.SelectTechTier(finished, facts.Census.PlayerTechLevel);
        var cost = new Dictionary<Resource, long>();
        var stock = ResourceStockRules.Count(facts.Census.Resources);
        var materials = new BedMaterials { Cost = cost, Items = facts.Items, Stock = stock };
        foreach (var d in facts.Definitions)
        {
            // A bed's stuff is the allowed stocked one with the best rest effectiveness; a def with no rest effectiveness is no bed.
            StuffPrice price;
            try
            {
                price = d.StuffChoice(StuffCriterion.MaxRestEffectiveness, stock);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            if (price.Stuff == "")
            {
                continue;
            }
            foreach (var c in price.Costs)
            {
                if (c.Resource == new Resource(price.Stuff))
                {
                    cost[new Resource(d.Name)] = c.Count;
                }
            }
        }
        return RoomQualityRules.NextBedReplacement(obs!, UpgradeTargetsOf(facts, finished, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness)), FurnitureRoomsRules.FurnitureRooms(rooms!, census, RequiredCells(cells)), def => ColonyPlanningRules.DefinitionAvailable(facts, def), materials, facts.Preferences);
    }

    public static (RoomUpgrade Upgrade, bool Due) TitleFurniture(ReviewFacts facts, IReadOnlyList<SiteCell>? cells)
    {
        var obs = facts.Sleeping;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        if (census is null)
        {
            return (new RoomUpgrade(), false);
        }
        return RoomQualityRules.NextTitleFurniture(obs!, FurnitureRoomsRules.FurnitureRooms(rooms!, census, RequiredCells(cells)), def => ColonyPlanningRules.DefinitionAvailable(facts, def));
    }

    public static (RoomUpgrade Upgrade, bool Due) CompanionBed(ReviewFacts facts, IReadOnlyList<SiteCell>? cells)
    {
        var obs = facts.Sleeping;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        var animals = facts.AnimalUpkeep.Animals;
        if (census is null)
        {
            return (new RoomUpgrade(), false);
        }
        if (facts.Definitions.FirstOrDefault(d => d.Name == facts.Shapes.Furniture.AnimalSpot) is not { } d)
        {
            return (new RoomUpgrade(), false);
        }
        var size = d.Size;
        var available = d.Available;
        if (size.Width < 1 || size.Height < 1)
        {
            return (new RoomUpgrade(), false);
        }
        var spot = new InteriorPieceDef { Def = d.Name, Size = new Cell(size.Width, size.Height) };
        return HerdCompanionRules.NextCompanionBed(obs!, animals,FurnitureRoomsRules.FurnitureRooms(rooms!, census, RequiredCells(cells)), facts.Shapes.Furniture, spot, available);
    }

    public static (RoomUpgrade Upgrade, bool Due) BeautyUpgrade(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished, IReadOnlyList<SiteCell>? cells)
    {
        var obs = facts.Sleeping;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (census is null || traits is null)
        {
            return (new RoomUpgrade(), false);
        }
        var tier = TechTierRules.SelectTechTier(finished, facts.Census.PlayerTechLevel);
        var definitions = new Dictionary<string, FloorDefinition>(StringComparer.Ordinal);
        var floors = new FlooringFacts { Definitions = definitions, Stock = ResourceStockRules.Count(facts.Census.Resources) };
        foreach (var d in facts.Definitions.Where(d => d.Floor is not null))
        {
            definitions[d.Name] = new FloorDefinition { Available = d.Available, Terrain = d.Terrain, Cleanliness = (d.Floor ?? throw new RequiredFactException("planning Floor")).Cleanliness, Beauty = (d.Floor ?? throw new RequiredFactException("planning Floor")).Beauty, Flammability = (d.Floor ?? throw new RequiredFactException("planning Floor")).Flammability, PathCost = d.PathCost, Costs = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs, WorkToBuild = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).WorkToBuild };
        }
        return RoomQualityRules.NextBeautyUpgrade(obs!, UpgradeTargetsOf(facts, finished, WithRequirementTargets(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness))), FurnitureRoomsRules.FurnitureRooms(rooms!, census, RequiredCells(cells)), def => ColonyPlanningRules.DefinitionAvailable(facts, def), facts.Upkeep.Flooring, floors);
    }

    public static (ThroneNeed Need, bool Owed) ThroneNeed(ReviewFacts facts)
    {
        if (facts.Royalty is not RoyaltyState.Active { Facts: var royalty })
        {
            return (new ThroneNeed(), false);
        }
        if (CeremonyRules.CeremonyThroneNeed(royalty) is { } ceremony)
        {
            return (ceremony, true);
        }
        return ThroneRules.NextThroneNeed(royalty);
    }

    public static IReadOnlyList<QuestId> TitleClaimQuests(ReviewFacts facts, IReadOnlyList<SiteCell>? cells)
    {
        if (facts.Royalty is not RoyaltyState.Active { Facts: var royalty })
        {
            return [];
        }
        var sleeping = facts.Sleeping;
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        if (!pk || census is null)
        {
            return [];
        }

        var claim = TitleClaimRules.NextTitleClaim(new TitleClaimFacts
        {
            Royalty = royalty,
            Sleeping = sleeping,
            BedroomPieces = FurnitureRoomsRules.FurnitureRooms(rooms!, census, RequiredCells(cells)),
            Plan = plan!,
            Rooms = rooms!,
            Built = census.Buildings,
        });
        return CeremonyRules.ClaimQuests(royalty!, claim);
    }

    public static ThroneStep ThroneStepOf(ReviewFacts facts, IReadOnlyList<SiteCell>? cells)
    {
        var (need, owed) = ThroneNeed(facts);
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        if (!owed || !pk || census is null)
        {
            return new ThroneStep();
        }
        var names = need.DefNames();
        var defs = new List<FurnitureDefinition>(names.Count);
        foreach (var d in facts.Definitions)
        {
            if (names.Contains(d.Name))
            {
                defs.Add(new FurnitureDefinition { Name = d.Name, Available = d.Available, Size = d.Size });
            }
        }
        var ground = LayoutGroundRules.GroundOf(facts.CurrentConstruction.Buildings);
        // ThroneNeed owes a room only with Royalty active.
        var thrones = facts.Royalty is RoyaltyState.Active { Facts: var royalty } ? royalty.Thrones : [];
        var step = ThroneRules.NextThroneStep(plan!, rooms!, plan!.GroundWithRock(ground, RequiredCells(cells).Where(c => c.NaturalRock()).Select(c => c.Cell).ToList()), census.Buildings, need, defs, thrones);
        if (step.Kind != ThroneStepKind.None)
        {
            return step;
        }
        // Everything stands and is assigned: keep its braziers lit.
        var lighting = facts.Upkeep.Lighting;
        var workers = facts.ForecastObserved.WorkPawns.Pawns;

        return ThroneRules.NextThroneRefuel(plan, rooms!, need, lighting.Lamps, workers!);
    }

    private static Dictionary<string, RoomTarget>? WithRequirementTargets(ReviewFacts facts, Dictionary<string, RoomTarget>? targets)
    {
        var (need, owed) = ThroneNeed(facts);
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Upkeep.Rooms;
        if (!pk)
        {
            return targets;
        }
        var throne = owed ? ThroneRules.ThroneRoomTargets(plan!, rooms!, need) : new Dictionary<string, RoomTarget>();
        var worship = WorshipRules.WorshipRoomTargets(plan!, rooms!, facts.Worship);
        if (throne.Count == 0 && worship.Count == 0)
        {
            return targets;
        }
        targets ??= new Dictionary<string, RoomTarget>(StringComparer.Ordinal);
        foreach (var (id, t) in throne.Concat(worship))
        {
            targets[id] = t;
        }
        return targets;
    }

    public static FlooringObservation WithThroneFloor(ReviewFacts facts, FlooringObservation v)
    {
        var (need, owed) = ThroneNeed(facts);
        if (!owed || facts.LayoutPlan is not { } plan || need.FloorTags.Count == 0)
        {
            return v;
        }
        var rooms = facts.Upkeep.Rooms;
        var (room, ok) = plan.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return v;
        }
        // Census: the floor requirement is keyed by the room's id.
        var (standing, found) = PlannedRoom.CensusRoomIn(room, rooms);
        if (!found || standing is null)
        {
            return v;
        }
        var copy = v.Rooms.ToList();
        for (var i = 0; i < copy.Count; i++)
        {
            if (copy[i].Id == standing.Id)
            {
                copy[i] = copy[i] with { RequiredTags = need.FloorTags };
            }
        }
        return v with { Rooms = [.. copy] };
    }

    public static IReadOnlyList<string> WorshipDefinitions(ReviewFacts facts) => facts.Worship.Definitions;

    public static IReadOnlyList<string> ContainmentDefinitions(ReviewFacts facts)
    {
        if (facts.Containment.Defs is ContainmentDefsRead.Read { Defs: var defs })
        {
            var output = new List<string> { defs.Holder, ContainmentCellRules.ContainmentLampDefinition };
            if (defs.Floor.Def != "")
            {
                output.Add(defs.Floor.Def);
            }
            return output;
        }
        return [];
    }

    private static ChildRoomNeed? ContainmentCellNeed(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished) =>
        ContainmentCellRules.ContainmentCellNeed(facts.Containment, FurnitureDefinitions(facts), ColonyPlanningRules.FlooringFacts(facts, finished));

    public static (ChildRoomNeed Need, bool Owed) IsolationRoomNeed(ReviewFacts facts) =>
        IsolationRoomRules.IsolationRoomNeed(facts.Isolation, FurnitureDefinitions(facts));

    public static List<ChildRoomNeed> ChildRoomNeeds(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished)
    {
        var needs = new List<ChildRoomNeed>();
        var pawns = facts.ForecastObserved.WorkPawns.Pawns;
        {
            needs.AddRange(ChildRoomsRules.ChildRoomNeeds(pawns, facts.ForecastObserved.Needs?.Deathrest));
        }
        if (facts.Worship.Need is { } worship)
        {
            needs.Add(worship);
        }
        // The rooms the player's goals name (#3496) join the rooms the pawns owe: layout sizes and places them, the sleeping step builds them.
        needs.AddRange(ChildRoomsRules.GoalRoomNeeds(facts.GoalBuild.Rooms, needs));
        if (ContainmentCellNeed(facts, finished) is { } cell)
        {
            needs.Add(cell);
        }
        var (isolation, isolationOwed) = IsolationRoomNeed(facts);
        if (isolationOwed)
        {
            needs.Add(isolation);
        }
        return needs;
    }

    public static List<FurnitureDefinition> FurnitureDefinitions(ReviewFacts facts)
    {
        var names = new HashSet<string>(WorshipDefinitions(facts).Concat(ContainmentDefinitions(facts)).Concat(facts.Isolation.Beds), StringComparer.Ordinal);
        var defs = new List<FurnitureDefinition>();
        foreach (var d in facts.Definitions)
        {
            if (d.RoomRoles.Count > 0 || names.Contains(d.Name))
            {
                defs.Add(new FurnitureDefinition { Name = d.Name, Available = d.Available, Size = d.Size, Roles = d.RoomRoles });
            }
        }
        return defs;
    }

    public static ChildRoomStep ChildRoomStep(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished, IReadOnlyList<SiteCell>? cells)
    {
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        var ground = LayoutGroundRules.GroundOf(facts.CurrentConstruction.Buildings);
        if (!pk)
        {
            return new ChildRoomStep();
        }
        return ChildRoomsRules.NextChildRoomStep(plan!, rooms!, plan!.GroundWithRock(ground, RequiredCells(cells).Where(c => c.NaturalRock()).Select(c => c.Cell).ToList()), census.Buildings, ChildRoomNeeds(facts, finished), FurnitureDefinitions(facts));
    }
}
