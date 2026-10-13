using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Identifies the work a quest root requests. An unlisted root is unknown (<see cref="None"/>, Go's empty
/// string); neither a familiar prefix nor a favor reward establishes its work.
/// </summary>
public enum QuestFamily
{
    [WireName("")]
    None = 0,
    [WireName("hospitality_animals")]
    HospitalityAnimals,
    [WireName("hospitality_joiners")]
    HospitalityJoiners,
    [WireName("hospitality_prisoners")]
    HospitalityPrisoners,
    [WireName("hospitality_refugee")]
    HospitalityRefugee,
    [WireName("joiner")]
    Joiner,
    [WireName("bandit_camp")]
    BanditCamp,
    [WireName("pawn_lend")]
    PawnLend,
    [WireName("shuttle_rescue")]
    ShuttleRescue,
    [WireName("build_monument")]
    BuildMonument,
    [WireName("decree_produce")]
    DecreeProduce,
    [WireName("decree_harvest")]
    DecreeHarvest,
    [WireName("decree_hunt")]
    DecreeHunt,
    [WireName("decree_monument")]
    DecreeMonument,
    [WireName("royal_heir")]
    RoyalHeir,
    [WireName("problem_causer")]
    ProblemCauser,
    [WireName("bestowing")]
    Bestowing,
    [WireName("laborers")]
    Laborers,
    [WireName("odyssey_ground")]
    OdysseyGround,
    [WireName("odyssey_ship_only")]
    OdysseyShipOnly,
    [WireName("threat_reward")]
    ThreatReward,
    [WireName("royal_ascent")]
    RoyalAscent,
    [WireName("utility")]
    Utility,
    [WireName("site")]
    Site,
    [WireName("beggars")]
    Beggars,
    [WireName("relic")]
    Relic,
    [WireName("hack")]
    Hack,
    [WireName("incident")]
    Incident,
    [WireName("endgame")]
    Endgame,
    [WireName("trade")]
    Trade,
    [WireName("anomaly")]
    Anomaly,
    [WireName("refugee_pod")]
    RefugeePod,
}

public enum QuestDisposition
{
    [WireName("")]
    None = 0,
    [WireName("decide")]
    Decide,
    [WireName("refuse")]
    Refuse,
    [WireName("observe")]
    Observe,
    [WireName("follow")]
    Follow,
}

public enum QuestCost
{
    [WireName("")]
    None = 0,
    [WireName("free")]
    Free,
    [WireName("costs_pawns")]
    Pawns,
    [WireName("costs_time")]
    Time,
    [WireName("trap")]
    Trap,
}

/// <summary>
/// A set of resource and work categories. Amounts and deadlines come from the live quest objectives, never
/// from this root classification.
/// </summary>
[Flags]
public enum QuestDemand : ushort
{
    None = 0,
    Food = 1 << 0,
    Beds = 1 << 1,
    Medical = 1 << 2,
    Security = 1 << 3,
    Pawns = 1 << 4,
    Travel = 1 << 5,
    Shuttle = 1 << 6,
    Materials = 1 << 7,
    Construction = 1 << 8,
    Production = 1 << 9,
    Harvest = 1 << 10,
    Hunt = 1 << 11,
    Ceremony = 1 << 12,
}

public sealed record QuestProfile
{
    public QuestFamily Family { get; init; }

    public QuestCost Cost { get; init; }

    public QuestDemand Demands { get; init; }

    /// <summary>Excludes helper scripts, automatic consequences and Royal Ascent.</summary>
    public bool NeverAct { get; init; }

    public QuestDisposition Disposition { get; init; }

    public string SkipReason { get; init; } = "";

    /// <summary>Forbids arrest and operations during an open hosted meeting.</summary>
    public bool ProtectGuests { get; init; }
}

public static partial class PolicyFunctions
{
    private static readonly Dictionary<string, QuestProfile> QuestFamilies = BuildQuestFamilies();

    /// <summary>
    /// The explicit game-data table. Utility roots are listed as excluded, so coverage includes every named
    /// Royalty QuestScriptDef without turning a sub-script into an actionable offer. An unlisted root returns
    /// the zero profile (unknown family).
    /// </summary>
    public static QuestProfile QuestFamilyForRoot(string root) =>
        QuestFamilies.TryGetValue(root, out var profile) ? profile : new QuestProfile();

    private static Dictionary<string, QuestProfile> BuildQuestFamilies()
    {
        var table = new Dictionary<string, QuestProfile>(StringComparer.Ordinal);

        void Add(QuestFamily family, QuestCost cost, QuestDemand demands, bool never, params string[] roots)
        {
            foreach (var root in roots)
            {
                table[root] = new QuestProfile
                {
                    Family = family,
                    Cost = cost,
                    Demands = demands,
                    NeverAct = never,
                    Disposition = never ? QuestDisposition.Observe : QuestDisposition.Decide,
                };
            }
        }

        Add(QuestFamily.HospitalityAnimals, QuestCost.Time, QuestDemand.Food | QuestDemand.Medical, false, "Hospitality_Animals");
        Add(QuestFamily.HospitalityJoiners, QuestCost.Time, QuestDemand.Food | QuestDemand.Beds | QuestDemand.Medical | QuestDemand.Security | QuestDemand.Shuttle, false, "Hospitality_Joiners");
        Add(QuestFamily.HospitalityPrisoners, QuestCost.Time, QuestDemand.Food | QuestDemand.Beds | QuestDemand.Medical | QuestDemand.Security | QuestDemand.Shuttle, false, "Hospitality_Prisoners");
        Add(QuestFamily.HospitalityRefugee, QuestCost.Time, QuestDemand.Food | QuestDemand.Beds | QuestDemand.Medical | QuestDemand.Security, false, "Hospitality_Refugee");
        Add(QuestFamily.HospitalityRefugee, QuestCost.Trap, 0, true, "RefugeeBetrayal");
        Add(QuestFamily.BanditCamp, QuestCost.Pawns, QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security, false, "Mission_BanditCamp");
        Add(QuestFamily.PawnLend, QuestCost.Pawns, QuestDemand.Pawns | QuestDemand.Shuttle, false, "PawnLend");
        Add(QuestFamily.ShuttleRescue, QuestCost.Time, QuestDemand.Medical | QuestDemand.Security | QuestDemand.Shuttle, false, "ShuttleCrash_Rescue");
        Add(QuestFamily.BuildMonument, QuestCost.Time, QuestDemand.Materials | QuestDemand.Construction, false, "BuildMonument_Basic");
        Add(QuestFamily.BuildMonument, QuestCost.Time, QuestDemand.Materials | QuestDemand.Construction | QuestDemand.Security, false, "BuildMonument_TimeProtect");
        Add(QuestFamily.DecreeProduce, QuestCost.Time, QuestDemand.Materials | QuestDemand.Production, false, "Decree_ProduceItem");
        Add(QuestFamily.DecreeHarvest, QuestCost.Time, QuestDemand.Harvest, false, "Decree_HarvestCrop");
        Add(QuestFamily.DecreeHunt, QuestCost.Time, QuestDemand.Hunt, false, "Decree_HuntAnimal");
        Add(QuestFamily.DecreeMonument, QuestCost.Time, QuestDemand.Materials | QuestDemand.Construction, false, "Decree_BuildMonument");
        Add(QuestFamily.RoyalHeir, QuestCost.Free, 0, true, "ChangeRoyalHeir");
        Add(QuestFamily.ProblemCauser, QuestCost.Time, QuestDemand.Travel | QuestDemand.Security, true, "ProblemCauser");
        Add(QuestFamily.Bestowing, QuestCost.Time, QuestDemand.Ceremony, false, "BestowingCeremony");
        Add(QuestFamily.Laborers, QuestCost.Free, QuestDemand.Food | QuestDemand.Beds, true, "Permit_CallLaborers");
        Add(QuestFamily.RoyalAscent, QuestCost.Trap, 0, true, "EndGame_RoyalAscent");
        Add(QuestFamily.Joiner, QuestCost.Time, QuestDemand.Food | QuestDemand.Beds | QuestDemand.Security, false,
            "ThreatReward_Raid_Joiner", "ThreatReward_Infestation_Joiner", "ThreatReward_Manhunters_Joiner",
            "ThreatReward_GameCondition_Joiner", "ThreatReward_SiteThreat_Joiner", "ThreatReward_RaidMultiFaction_Joiner", "ThreatReward_MysteryThreat_Joiner");
        Add(QuestFamily.Joiner, QuestCost.Trap, QuestDemand.Medical, true, "WandererJoinAbasia");
        Add(QuestFamily.ThreatReward, QuestCost.Time, QuestDemand.Security, false,
            "Intro_Deserter", "Intro_Wimp", "ThreatReward_Manhunters_ItemPod", "ThreatReward_Infestation_ItemPod",
            "ThreatReward_GameCondition_ItemPod", "ThreatReward_SiteThreat_ItemPod", "ThreatReward_MechPods_MiscReward", "ThreatReward_Raid_MiscReward");
        Add(QuestFamily.Utility, QuestCost.Free, 0, true,
            "RefugeeDelayedReward", "Util_TransportShip_DropOff", "Util_TransportShip_Pickup", "BuildMonumentWorker",
            "DecreeSetup", "Decree_Util_Reward", "Util_RandomSelectDiseaseHuman", "Util_RandomSelectDiseaseAnimal",
            "Hospitality_Util_DecideRandomLodgerCount", "Hospitality_Util_DecideLodgerCountFromPoints", "Hospitality_Util_AddHealthConditions",
            "Util_ChooseRandomQuestLodgerKind", "Util_ApplyMoodThreshold", "Util_DecideRandomLodgerConditions",
            "Util_ApplyLodgerConditions", "Util_MaybeApplyGoodwillForMood", "Hospitality_Util_Setup", "Util_ArriveByDropPodsOrShuttle",
            "Hospitality_Util_Worker", "Util_MaybeGenerateHelpers", "Util_ChooseRandomQuestHelperKind", "Util_ManhunterPack",
            "Util_Infestation", "Util_SpawnSiteThreat", "Util_GameConditionNegativeRandom", "Util_DecideRandomAsker", "Util_Constants_Monuments");
        ExtendQuestFamilies(table);
        return table;
    }

    /// <summary>Named Core and DLC roots distinguish player offers from incident-driven work.</summary>
    private static void ExtendQuestFamilies(Dictionary<string, QuestProfile> table)
    {
        void Add(QuestFamily family, QuestDisposition mode, string reason, QuestDemand demand, params string[] names)
        {
            foreach (var name in names)
            {
                table[name] = new QuestProfile
                {
                    Family = family,
                    Cost = QuestCost.Time,
                    Demands = demand,
                    NeverAct = mode != QuestDisposition.Decide && mode != QuestDisposition.Follow,
                    Disposition = mode,
                    SkipReason = reason,
                };
            }
        }

        void SetCost(QuestCost cost, params string[] names)
        {
            foreach (var name in names)
            {
                table[name] = table[name] with { Cost = cost };
            }
        }

        Add(QuestFamily.Utility, QuestDisposition.Observe, "", 0,
            "DelayedRewardDropPods", "Util_JoinerThreat_Joiner", "Util_GetDefaultRewardValueFromPoints", "Util_JoinerWalkIn", "Util_JoinerDropIn", "Util_SendItemPods", "Util_RandomizePointsChallengeRating", "Util_AdjustPointsForDistantFight", "Util_Raid", "Util_MechCluster", "Util_RaidDelayRepeatable", "Util_GenerateSite");
        Add(QuestFamily.BanditCamp, QuestDisposition.Decide, "", QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security, "OpportunitySite_BanditCamp");
        Add(QuestFamily.Site, QuestDisposition.Observe, "", QuestDemand.Travel,
            "OpportunitySite_DownedRefugee", "OpportunitySite_ItemStash", "LongRangeMineralScannerLump", "OpportunitySite_PeaceTalks", "OpportunitySite_PrisonerWillingToJoin", "OpportunitySite_AncientComplex", "OpportunitySite_WorkSite", "OpportunitySite_AncientComplex_Mechanitor", "OpportunitySite_DistressCall");
        Add(QuestFamily.Site, QuestDisposition.Follow, "", QuestDemand.Pawns | QuestDemand.Travel, "OpportunitySite_PeaceTalks");
        Add(QuestFamily.Site, QuestDisposition.Follow, "", QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security,
            "OpportunitySite_ItemStash", "LongRangeMineralScannerLump");
        Add(QuestFamily.Site, QuestDisposition.Follow, "", QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security | QuestDemand.Medical,
            "OpportunitySite_DownedRefugee", "OpportunitySite_PrisonerWillingToJoin");
        SetCost(QuestCost.Pawns, "OpportunitySite_DownedRefugee", "OpportunitySite_ItemStash", "LongRangeMineralScannerLump", "OpportunitySite_PrisonerWillingToJoin");
        Add(QuestFamily.Joiner, QuestDisposition.Observe, "", QuestDemand.Food | QuestDemand.Beds | QuestDemand.Medical,
            "WandererJoins", "RefugeePodCrash_Ghoul", "CreepJoinerArrival", "CreepJoinerArrival_Metalhorror");
        Add(QuestFamily.RefugeePod, QuestDisposition.Follow, "", QuestDemand.Medical, "RefugeePodCrash", "RefugeePodCrash_Baby");
        foreach (var name in table.Keys.ToList())
        {
            if (table[name].Family == QuestFamily.RefugeePod)
            {
                table[name] = table[name] with { Cost = QuestCost.Free };
            }
        }
        Add(QuestFamily.Endgame, QuestDisposition.Refuse, "endgame", 0,
            "EndGame_ShipEscape", "EndGame_ArchonexusVictory", "EndGame_ArchonexusVictory_FirstCycle", "EndGame_ArchonexusVictory_SecondCycle", "EndGame_ArchonexusVictory_ThirdCycle", "EndGame_VoidMonolith", "EndGame_VoidAwakening");
        Add(QuestFamily.Trade, QuestDisposition.Refuse, "delivery_unavailable", QuestDemand.Production | QuestDemand.Travel, "TradeRequest");
        Add(QuestFamily.Incident, QuestDisposition.Observe, "", QuestDemand.Security,
            "AncientSignalActivation", "ReliquaryPilgrims", "Bossgroup", "MechanitorShip", "PollutionRaid", "PollutionRetaliation", "SightstealerArrival", "UnnaturalDarkness", "MechanitorStartingMech", "Beggars");
        Add(QuestFamily.Hack, QuestDisposition.Decide, "", QuestDemand.Travel | QuestDemand.Security, "AncientComplex_Standard", "Hack_Spacedrone", "AncientComplex_Mission", "Hack_WorshippedTerminal");
        Add(QuestFamily.Relic, QuestDisposition.Decide, "", QuestDemand.Travel, "RelicHunt");
        Add(QuestFamily.Site, QuestDisposition.Follow, "", QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security,
            "OpportunitySite_AncientComplex", "OpportunitySite_AncientComplex_Mechanitor", "OpportunitySite_WorkSite", "OpportunitySite_AncientComplex_Giver");
        SetCost(QuestCost.Pawns, "OpportunitySite_AncientComplex", "OpportunitySite_AncientComplex_Mechanitor", "OpportunitySite_WorkSite", "OpportunitySite_AncientComplex_Giver", "AncientComplex_Standard", "AncientComplex_Mission", "Hack_WorshippedTerminal");
        Add(QuestFamily.Hack, QuestDisposition.Decide, "", QuestDemand.Security, "Hack_Spacedrone");
        Add(QuestFamily.Beggars, QuestDisposition.Follow, "", QuestDemand.Materials, "Beggars");
        table["Beggars"] = table["Beggars"] with { ProtectGuests = true };
        Add(QuestFamily.HospitalityJoiners, QuestDisposition.Decide, "", QuestDemand.Food | QuestDemand.Beds | QuestDemand.Security, "SanguophageMeetingHost");
        table["SanguophageMeetingHost"] = table["SanguophageMeetingHost"] with { ProtectGuests = true };
        Add(QuestFamily.Incident, QuestDisposition.Refuse, "pollution", 0, "PollutionDump");
        Add(QuestFamily.Incident, QuestDisposition.Refuse, "sanguophage_ship", 0, "SanguophageShip");
        Add(QuestFamily.Anomaly, QuestDisposition.Refuse, "entity_risk", 0, "MonolithMigration", "MysteriousCargoUnnaturalCorpse", "MysteriousCargoCube", "MysteriousCargoRevenantSpine");
        Add(QuestFamily.OdysseyGround, QuestDisposition.Observe, "", QuestDemand.Travel,
            "OpportunitySite_AlphaThrumbo_Giver", "OpportunitySite_AncientMercenaries", "OpportunitySite_BanditCamp_Giver", "GravEngine", "Gravcore_MechanoidRelay", "Gravcore_InsectLair", "Gravcore_AncientReactor", "Gravcore_AncientStockpile", "Gravcore_CrashedMechanoidPlatform", "Gravcore_FrozenTerraformer", "GravshipWreckage", "OpportunitySite_ItemStash_Giver");
        Add(QuestFamily.OdysseyGround, QuestDisposition.Decide, "", QuestDemand.Travel | QuestDemand.Security,
            "Opportunity_AncientStructureLaunchSite", "Opportunity_AncientStructureGarrison", "Opportunity_AncientStructureChemfuelRefinery", "Opportunity_AncientStructureWarehouse", "Opportunity_AncientInfestedSettlement", "MechanoidSignal");
        foreach (var name in table.Keys.ToList())
        {
            if (OdysseyGroundSiteRoot(name))
            {
                table[name] = table[name] with
                {
                    Family = QuestFamily.OdysseyGround,
                    Disposition = QuestDisposition.Follow,
                    NeverAct = false,
                    Cost = QuestCost.Pawns,
                    Demands = QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security,
                };
            }
        }
        table["MechanoidSignal"] = table["MechanoidSignal"] with { Cost = QuestCost.Free, Demands = 0 };
        Add(QuestFamily.OdysseyGround, QuestDisposition.Decide, "", QuestDemand.Pawns | QuestDemand.Travel | QuestDemand.Security, "SurveySite");
        Add(QuestFamily.Utility, QuestDisposition.Observe, "", 0, "Util_SetupAncientStructureCommon");
        Add(QuestFamily.OdysseyShipOnly, QuestDisposition.Refuse, "ship_only", QuestDemand.Travel,
            "Gravcore_OrbitalMechanoidPlatform", "Gravcore_OrbitalAncientPlatform", "Gravcore_Mechhive", "OrbitalFugitive", "OpportunitySite_Asteroid", "OpportunitySite_OrbitalItemStash", "OpportunitySite_AbandonedPlatform", "OpportunitySite_OrbitalWreck", "OpportunitySite_MechanoidPlatform", "OpportunitySite_Satellite");
        foreach (var name in new[] { "RefugeeBetrayal", "EndGame_RoyalAscent" })
        {
            table[name] = table[name] with
            {
                Disposition = QuestDisposition.Refuse,
                SkipReason = name == "EndGame_RoyalAscent" ? "endgame" : "automatic_trap",
            };
        }
    }

    /// <summary>
    /// Lists ground expeditions with the shared combat and native reform/loot return chain. World-layer facts
    /// still reject space routes.
    /// </summary>
    public static bool OdysseyGroundSiteRoot(string root) => root is
        "Gravcore_InsectLair" or "Gravcore_AncientReactor" or "Gravcore_AncientStockpile" or "Gravcore_CrashedMechanoidPlatform" or "Gravcore_FrozenTerraformer" or "Gravcore_MechanoidRelay"
        or "GravshipWreckage" or "OpportunitySite_AncientMercenaries" or "OpportunitySite_BanditCamp_Giver" or "OpportunitySite_ItemStash_Giver" or "OpportunitySite_AlphaThrumbo_Giver" or "OpportunitySite_DistressCall"
        or "Opportunity_AncientStructureLaunchSite" or "Opportunity_AncientStructureGarrison" or "Opportunity_AncientStructureChemfuelRefinery" or "Opportunity_AncientStructureWarehouse" or "Opportunity_AncientInfestedSettlement";
}
