package policy

// Named Core and DLC roots distinguish player offers from incident-driven work.
func extendQuestFamilies(table map[string]QuestProfile) {
	add := func(family QuestFamily, mode QuestDisposition, reason string, demand QuestDemand, names ...string) {
		for _, name := range names {
			table[name] = QuestProfile{Family: family, Cost: QuestCostTime, Demands: demand, NeverAct: mode != QuestDecide && mode != QuestFollow, Disposition: mode, SkipReason: reason}
		}
	}
	add(QuestFamilyUtility, QuestObserve, "", 0,
		"DelayedRewardDropPods", "Util_JoinerThreat_Joiner", "Util_GetDefaultRewardValueFromPoints", "Util_JoinerWalkIn", "Util_JoinerDropIn", "Util_SendItemPods", "Util_RandomizePointsChallengeRating", "Util_AdjustPointsForDistantFight", "Util_Raid", "Util_MechCluster", "Util_RaidDelayRepeatable", "Util_GenerateSite")
	add(QuestFamilyBanditCamp, QuestDecide, "", QuestDemandPawns|QuestDemandTravel|QuestDemandSecurity, "OpportunitySite_BanditCamp")
	add(QuestFamilySite, QuestObserve, "", QuestDemandTravel,
		"OpportunitySite_DownedRefugee", "OpportunitySite_ItemStash", "LongRangeMineralScannerLump", "OpportunitySite_PeaceTalks", "OpportunitySite_PrisonerWillingToJoin", "OpportunitySite_AncientComplex", "OpportunitySite_WorkSite", "OpportunitySite_AncientComplex_Mechanitor", "OpportunitySite_DistressCall")
	add(QuestFamilyJoiner, QuestObserve, "", QuestDemandFood|QuestDemandBeds|QuestDemandMedical,
		"RefugeePodCrash", "WandererJoins", "RefugeePodCrash_Baby", "RefugeePodCrash_Ghoul", "CreepJoinerArrival", "CreepJoinerArrival_Metalhorror")
	add(QuestFamilyEndgame, QuestRefuse, "endgame", 0,
		"EndGame_ShipEscape", "EndGame_ArchonexusVictory", "EndGame_ArchonexusVictory_FirstCycle", "EndGame_ArchonexusVictory_SecondCycle", "EndGame_ArchonexusVictory_ThirdCycle", "EndGame_VoidMonolith", "EndGame_VoidAwakening")
	add(QuestFamilyTrade, QuestRefuse, "delivery_unavailable", QuestDemandProduction|QuestDemandTravel, "TradeRequest")
	add(QuestFamilyIncident, QuestObserve, "", QuestDemandSecurity,
		"AncientSignalActivation", "ReliquaryPilgrims", "Bossgroup", "MechanitorShip", "PollutionRaid", "PollutionRetaliation", "SightstealerArrival", "UnnaturalDarkness", "MechanitorStartingMech", "Beggars")
	add(QuestFamilyHack, QuestDecide, "", QuestDemandTravel|QuestDemandSecurity, "AncientComplex_Standard", "Hack_Spacedrone", "AncientComplex_Mission", "Hack_WorshippedTerminal")
	add(QuestFamilyRelic, QuestDecide, "", QuestDemandTravel, "RelicHunt")
	add(QuestFamilyHospitalityJoiners, QuestDecide, "", QuestDemandFood|QuestDemandBeds|QuestDemandSecurity, "SanguophageMeetingHost")
	meeting := table["SanguophageMeetingHost"]
	meeting.ProtectGuests = true
	table["SanguophageMeetingHost"] = meeting
	add(QuestFamilyIncident, QuestRefuse, "pollution", 0, "PollutionDump")
	add(QuestFamilyIncident, QuestRefuse, "sanguophage_ship", 0, "SanguophageShip")
	add(QuestFamilyAnomaly, QuestRefuse, "entity_risk", 0, "MonolithMigration", "MysteriousCargoUnnaturalCorpse", "MysteriousCargoCube", "MysteriousCargoRevenantSpine")
	add(QuestFamilyOdysseyGround, QuestObserve, "", QuestDemandTravel,
		"OpportunitySite_AlphaThrumbo_Giver", "OpportunitySite_AncientComplex_Giver", "OpportunitySite_AncientMercenaries", "OpportunitySite_BanditCamp_Giver", "GravEngine", "Gravcore_MechanoidRelay", "Gravcore_InsectLair", "Gravcore_AncientReactor", "Gravcore_AncientStockpile", "Gravcore_CrashedMechanoidPlatform", "Gravcore_FrozenTerraformer", "GravshipWreckage", "OpportunitySite_ItemStash_Giver")
	add(QuestFamilyOdysseyGround, QuestDecide, "", QuestDemandTravel|QuestDemandSecurity,
		"Opportunity_AncientStructureLaunchSite", "Opportunity_AncientStructureGarrison", "Opportunity_AncientStructureChemfuelRefinery", "Opportunity_AncientStructureWarehouse", "Opportunity_AncientInfestedSettlement", "MechanoidSignal")
	add(QuestFamilyOdysseyGround, QuestRefuse, "needs_remote_site_hold", QuestDemandPawns|QuestDemandShuttle, "SurveySite")
	add(QuestFamilyUtility, QuestObserve, "", 0, "Util_SetupAncientStructureCommon")
	add(QuestFamilyOdysseyShipOnly, QuestRefuse, "ship_only", QuestDemandTravel,
		"Gravcore_OrbitalMechanoidPlatform", "Gravcore_OrbitalAncientPlatform", "Gravcore_Mechhive", "OrbitalFugitive", "OpportunitySite_Asteroid", "OpportunitySite_OrbitalItemStash", "OpportunitySite_AbandonedPlatform", "OpportunitySite_OrbitalWreck", "OpportunitySite_MechanoidPlatform", "OpportunitySite_Satellite")
	for _, name := range []string{"RefugeeBetrayal", "EndGame_RoyalAscent"} {
		profile := table[name]
		profile.Disposition = QuestRefuse
		profile.SkipReason = "automatic_trap"
		if name == "EndGame_RoyalAscent" {
			profile.SkipReason = "endgame"
		}
		table[name] = profile
	}
}
