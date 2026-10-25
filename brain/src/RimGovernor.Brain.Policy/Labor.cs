namespace RimGovernor.Brain.Policy;

// Native work types routine goals dispatch pawn work through. Names are RimWorld WorkTypeDef defNames as
// ListPawns reports them. The WorkType members (Construction, Research, Mining,
// PlantCutting, Crafting, Hauling, Cleaning, Basic = BasicWorker, Handling, Cooking, Firefighter, Tailoring,
// Smithing) belong to the WorkType enum.
public static class LaborRules
{
    // Keyed by filing key (#3231 decision 22): a Need's NeedId.FilingKey at its family/kind level, so a Need keeps
    // the profile its Concern had.
    private static readonly Lazy<IReadOnlyDictionary<ConcernId, IReadOnlyList<WorkType>>> LaborTable = new(() =>
    {
        var table = new Dictionary<ConcernId, IReadOnlyList<WorkType>>();
        void SetNeeds(IReadOnlyList<WorkType> profile, params NeedId[] needs)
        {
            foreach (var need in needs)
            {
                table[need.FilingKey] = profile;
            }
        }
        SetNeeds(
            [WorkType.Construction],
            HomeUpkeepFamily.Clearance, MilitaryFamily.Defense, RoomsFamily.Comfort, HomeUpkeepFamily.Repairs,
            LayoutFamily.StoneShell, FoodStoresRules.FoodStorageNeed, LayoutFamily.HomeCoverage, AnimalsFamily.Containment,
            RoomsFamily.Lighting, RoomsFamily.Flooring, BiotechFamily.MechCharger, BiotechFamily.GeneBank,
            ShelterFamily.Sleeping, LayoutFamily.Routes);
        SetNeeds([WorkType.PlantCutting], FoodRules.BlightNeed);
        SetNeeds([WorkType.Art], RoomsFamily.Art);

        // The trade Need's sale batch at any crafting bench: sculpture, apparel, weapon.
        SetNeeds([WorkType.Art, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting], EconomyFamily.Trade);
        SetNeeds([WorkType.Research], EconomyFamily.Research);

        // The resource Need's mines, chops and workshop bench.
        SetNeeds([WorkType.Mining, WorkType.PlantCutting, WorkType.Crafting], EconomyFamily.Resource);

        // A wear order is a forced job any pawn carries; the replacement bill needs the bench's work type.
        // Construction keeps the workshop rung available before a bench exists to request its crafting work.
        SetNeeds([WorkType.Construction, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting], MilitaryFamily.Equipment);
        SetNeeds([WorkType.Hauling], BiotechFamily.Pollution);

        // The shell, sarcophagus and graves are built; vanilla haulers carry the corpses.
        SetNeeds([WorkType.Construction], AdminFamily.Burial);

        // The range shell and its stands are built; the native training job (#2610) spends the trainees' own hours.
        SetNeeds([WorkType.Construction], MilitaryFamily.Training);
        SetNeeds([WorkType.Handling], AnimalsFamily.Herd);
        SetNeeds([WorkType.Cooking], FoodRules.BabyFeedingNeed);

        // Vents are built over standing walls and roofs are designated for builders.
        SetNeeds([WorkType.Construction], LayoutFamily.ClimateEnvelope);

        // Plants are cut by plant cutters, wooden ruins taken down by builders.
        SetNeeds([WorkType.PlantCutting, WorkType.Construction], LayoutFamily.Firebreak);

        // A resized or retargeted stockpile is refilled by haulers.
        SetNeeds([WorkType.Hauling], AdminFamily.Stockpiles);

        // A medicine bill at a crafting bench, or a wild healroot harvest.
        SetNeeds([WorkType.Crafting, WorkType.PlantCutting], MedicalRules.MedicalReservesNeed);
        return table;
    });

    /// <summary>
    /// Lists the native work types a goal's methods can put pawns to, by filing key: a goal is admissible when any
    /// listed type still has free labor. An empty profile means the goal issues no pawn work and is bounded only by
    /// the coarse worker count. Goals not listed (emergencies, monitoring-only needs, configuration pushes) have
    /// no profile.
    /// </summary>
    public static IReadOnlyList<WorkType> ConcernLabor(ConcernId filingKey) => LaborTable.Value.GetValueOrDefault(filingKey) ?? [];

    /// <summary>A Need's labor profile: its family/kind filing key's, so every subject of one kind shares it.</summary>
    public static IReadOnlyList<WorkType> NeedLabor(NeedId need) => ConcernLabor(NeedId.Of(need.Family, need.Kind).FilingKey);

    /// <summary>
    /// Counts the available pawns whose work settings enable <paramref name="work"/> (priority above zero and not
    /// disabled). An available pawn with an empty work-settings list violates the census contract: native lists every
    /// WorkTypeDef for a pawn whose work applies, and partial labor
    /// evidence must not admit or refuse work.
    /// </summary>
    public static int WorkersFor(IReadOnlyList<WorkPawn> pawns, WorkType work)
    {
        var count = 0;
        foreach (var p in pawns)
        {
            if (!p.Available || p.Work is not PawnWorkTable.Works { Rows: var rows })
            {
                continue;
            }
            if (rows.Count == 0)
            {
                throw new DomainException("empty available pawn work census");
            }
            foreach (var w in rows)
            {
                if (w.Work == work && w.Priority > 0 && !w.Disabled)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
