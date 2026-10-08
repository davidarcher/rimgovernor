package policy

// QuestFamily identifies the work a quest root requests. An unlisted root is
// unknown; neither a familiar prefix nor a favor reward establishes its work.
type QuestFamily string

const (
	QuestFamilyUnknown              QuestFamily = ""
	QuestFamilyHospitalityAnimals   QuestFamily = "hospitality_animals"
	QuestFamilyHospitalityJoiners   QuestFamily = "hospitality_joiners"
	QuestFamilyHospitalityPrisoners QuestFamily = "hospitality_prisoners"
	QuestFamilyHospitalityRefugee   QuestFamily = "hospitality_refugee"
	QuestFamilyJoiner               QuestFamily = "joiner"
	QuestFamilyBanditCamp           QuestFamily = "bandit_camp"
	QuestFamilyPawnLend             QuestFamily = "pawn_lend"
	QuestFamilyShuttleRescue        QuestFamily = "shuttle_rescue"
	QuestFamilyBuildMonument        QuestFamily = "build_monument"
	QuestFamilyDecreeProduce        QuestFamily = "decree_produce"
	QuestFamilyDecreeHarvest        QuestFamily = "decree_harvest"
	QuestFamilyDecreeHunt           QuestFamily = "decree_hunt"
	QuestFamilyDecreeMonument       QuestFamily = "decree_monument"
	QuestFamilyRoyalHeir            QuestFamily = "royal_heir"
	QuestFamilyProblemCauser        QuestFamily = "problem_causer"
	QuestFamilyBestowing            QuestFamily = "bestowing"
	QuestFamilyLaborers             QuestFamily = "laborers"
	QuestFamilyOdysseyGround        QuestFamily = "odyssey_ground"
	QuestFamilyOdysseyShipOnly      QuestFamily = "odyssey_ship_only"
	QuestFamilyThreatReward         QuestFamily = "threat_reward"
	QuestFamilyRoyalAscent          QuestFamily = "royal_ascent"
	QuestFamilyUtility              QuestFamily = "utility"
	QuestFamilySite                 QuestFamily = "site"
	QuestFamilyRelic                QuestFamily = "relic"
	QuestFamilyHack                 QuestFamily = "hack"
	QuestFamilyIncident             QuestFamily = "incident"
	QuestFamilyEndgame              QuestFamily = "endgame"
	QuestFamilyTrade                QuestFamily = "trade"
	QuestFamilyAnomaly              QuestFamily = "anomaly"
)

type QuestDisposition string

const (
	QuestDecide  QuestDisposition = "decide"
	QuestRefuse  QuestDisposition = "refuse"
	QuestObserve QuestDisposition = "observe"
	QuestFollow  QuestDisposition = "follow"
)

type QuestCost string

const (
	QuestCostFree  QuestCost = "free"
	QuestCostPawns QuestCost = "costs_pawns"
	QuestCostTime  QuestCost = "costs_time"
	QuestCostTrap  QuestCost = "trap"
)

// QuestDemand is a set of resource and work categories. Amounts and deadlines
// come from the live quest objectives, never from this root classification.
type QuestDemand uint16

const (
	QuestDemandFood QuestDemand = 1 << iota
	QuestDemandBeds
	QuestDemandMedical
	QuestDemandSecurity
	QuestDemandPawns
	QuestDemandTravel
	QuestDemandShuttle
	QuestDemandMaterials
	QuestDemandConstruction
	QuestDemandProduction
	QuestDemandHarvest
	QuestDemandHunt
	QuestDemandCeremony
)

type QuestProfile struct {
	Family  QuestFamily
	Cost    QuestCost
	Demands QuestDemand
	// NeverAct excludes helper scripts, automatic consequences and Royal Ascent.
	NeverAct    bool
	Disposition QuestDisposition
	SkipReason  string
	// ProtectGuests forbids arrest and operations during an open hosted meeting (#2415).
	ProtectGuests bool
}

// QuestFamilyForRoot is the explicit game-data table. Utility roots are listed
// as excluded, so coverage includes every named Royalty QuestScriptDef without
// turning a sub-script into an actionable offer.
func QuestFamilyForRoot(root string) QuestProfile { return questFamilies[root] }

var questFamilies = func() map[string]QuestProfile {
	table := make(map[string]QuestProfile)
	add := func(family QuestFamily, cost QuestCost, demands QuestDemand, never bool, roots ...string) {
		for _, root := range roots {
			mode := QuestDecide
			if never {
				mode = QuestObserve
			}
			table[root] = QuestProfile{Family: family, Cost: cost, Demands: demands, NeverAct: never, Disposition: mode}
		}
	}
	add(QuestFamilyHospitalityAnimals, QuestCostTime, QuestDemandFood|QuestDemandMedical, false, "Hospitality_Animals")
	add(QuestFamilyHospitalityJoiners, QuestCostTime, QuestDemandFood|QuestDemandBeds|QuestDemandMedical|QuestDemandSecurity|QuestDemandShuttle, false, "Hospitality_Joiners")
	add(QuestFamilyHospitalityPrisoners, QuestCostTime, QuestDemandFood|QuestDemandBeds|QuestDemandMedical|QuestDemandSecurity|QuestDemandShuttle, false, "Hospitality_Prisoners")
	add(QuestFamilyHospitalityRefugee, QuestCostTime, QuestDemandFood|QuestDemandBeds|QuestDemandMedical|QuestDemandSecurity, false, "Hospitality_Refugee")
	add(QuestFamilyHospitalityRefugee, QuestCostTrap, 0, true, "RefugeeBetrayal")
	add(QuestFamilyBanditCamp, QuestCostPawns, QuestDemandPawns|QuestDemandTravel|QuestDemandSecurity, false, "Mission_BanditCamp")
	add(QuestFamilyPawnLend, QuestCostPawns, QuestDemandPawns|QuestDemandShuttle, false, "PawnLend")
	add(QuestFamilyShuttleRescue, QuestCostTime, QuestDemandMedical|QuestDemandSecurity|QuestDemandShuttle, false, "ShuttleCrash_Rescue")
	add(QuestFamilyBuildMonument, QuestCostTime, QuestDemandMaterials|QuestDemandConstruction, false, "BuildMonument_Basic", "BuildMonument_TimeProtect")
	add(QuestFamilyDecreeProduce, QuestCostTime, QuestDemandMaterials|QuestDemandProduction, false, "Decree_ProduceItem")
	add(QuestFamilyDecreeHarvest, QuestCostTime, QuestDemandHarvest, false, "Decree_HarvestCrop")
	add(QuestFamilyDecreeHunt, QuestCostTime, QuestDemandHunt, false, "Decree_HuntAnimal")
	add(QuestFamilyDecreeMonument, QuestCostTime, QuestDemandMaterials|QuestDemandConstruction, false, "Decree_BuildMonument")
	add(QuestFamilyRoyalHeir, QuestCostFree, 0, true, "ChangeRoyalHeir")
	add(QuestFamilyProblemCauser, QuestCostTime, QuestDemandTravel|QuestDemandSecurity, true, "ProblemCauser")
	add(QuestFamilyBestowing, QuestCostTime, QuestDemandCeremony, false, "BestowingCeremony")
	add(QuestFamilyLaborers, QuestCostFree, QuestDemandFood|QuestDemandBeds, true, "Permit_CallLaborers")
	add(QuestFamilyRoyalAscent, QuestCostTrap, 0, true, "EndGame_RoyalAscent")
	add(QuestFamilyJoiner, QuestCostTime, QuestDemandFood|QuestDemandBeds|QuestDemandSecurity, false,
		"ThreatReward_Raid_Joiner", "ThreatReward_Infestation_Joiner", "ThreatReward_Manhunters_Joiner",
		"ThreatReward_GameCondition_Joiner", "ThreatReward_SiteThreat_Joiner", "ThreatReward_RaidMultiFaction_Joiner", "ThreatReward_MysteryThreat_Joiner")
	add(QuestFamilyJoiner, QuestCostTrap, QuestDemandMedical, true, "WandererJoinAbasia")
	add(QuestFamilyThreatReward, QuestCostTime, QuestDemandSecurity, false,
		"Intro_Deserter", "Intro_Wimp", "ThreatReward_Manhunters_ItemPod", "ThreatReward_Infestation_ItemPod",
		"ThreatReward_GameCondition_ItemPod", "ThreatReward_SiteThreat_ItemPod", "ThreatReward_MechPods_MiscReward", "ThreatReward_Raid_MiscReward")
	add(QuestFamilyUtility, QuestCostFree, 0, true,
		"RefugeeDelayedReward", "Util_TransportShip_DropOff", "Util_TransportShip_Pickup", "BuildMonumentWorker",
		"DecreeSetup", "Decree_Util_Reward", "Util_RandomSelectDiseaseHuman", "Util_RandomSelectDiseaseAnimal",
		"Hospitality_Util_DecideRandomLodgerCount", "Hospitality_Util_DecideLodgerCountFromPoints", "Hospitality_Util_AddHealthConditions",
		"Util_ChooseRandomQuestLodgerKind", "Util_ApplyMoodThreshold", "Util_DecideRandomLodgerConditions",
		"Util_ApplyLodgerConditions", "Util_MaybeApplyGoodwillForMood", "Hospitality_Util_Setup", "Util_ArriveByDropPodsOrShuttle",
		"Hospitality_Util_Worker", "Util_MaybeGenerateHelpers", "Util_ChooseRandomQuestHelperKind", "Util_ManhunterPack",
		"Util_Infestation", "Util_SpawnSiteThreat", "Util_GameConditionNegativeRandom", "Util_DecideRandomAsker", "Util_Constants_Monuments")
	extendQuestFamilies(table)
	return table
}()
