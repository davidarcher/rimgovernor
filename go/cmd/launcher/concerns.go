package main

// concernLabels say what each concern is about, in the governor's words
// rather than the policy package's identifiers. The Now report prints these;
// an id with no entry prints as given.
var concernLabels = map[string]string{
	"AnswerDialog":              "Answer a pending dialog",
	"ActiveCombat":              "Fight off attackers",
	"CriticalMedical":           "Save a dying colonist",
	"RestoreWorkers":            "Get downed or idle colonists working again",
	"ManageSupplySafety":        "Keep supply runs safe",
	"EnsureWorkAssignments":     "Assign colonists their work",
	"EnsureFoodSupply":          "Keep the colony fed",
	"MaintainHousing":           "Keep everyone housed",
	"EnsureTemperatureSafety":   "Protect against heat and cold",
	"EnsureCooking":             "Keep meals being cooked",
	"MaintainButcherSpot":       "Keep a place to butcher",
	"EnsureBasicPower":          "Keep the power running",
	"EnsureBasicDefense":        "Keep a basic defense ready",
	"MaintainMedicalReserves":   "Stock medicine",
	"MaintainFoodStorage":       "Store food and keep a reserve",
	"MaintainRefrigeration":     "Keep food cold",
	"EnsureComfort":             "Keep colonists comfortable",
	"ClearPests":                "Clear pests and infestations",
	"MaintainEquipment":         "Keep colonists equipped",
	"EnsureResearch":            "Keep research going",
	"MaintainResource":          "Keep key materials stocked",
	"EnsureDefensiveLayout":     "Build the defensive layout",
	"TradeWithCaravan":          "Trade with visiting caravans",
	"MaintainBabyFeeding":       "Feed the babies",
	"ClearHomeObstructions":     "Clear obstructions from the home area",
	"MaintainArt":               "Make art",
	"MaintainTrade":             "Make goods to sell",
	"MaintainAnimalContainment": "Keep animals contained",
	"RemoveBlight":              "Remove crop blight",
	"ManageCreepJoiners":        "Handle creepjoiners",
	"RecoverDisasterServices":   "Restore services after a disaster",
	"MaintainHomeCoverage":      "Keep the home area covered",
	"MaintainStoneShell":        "Keep the stone shell sound",
	"MaintainFirebreak":         "Keep a firebreak",
	"MaintainFlooring":          "Keep floors laid",
	"MaintainGeneBank":          "Keep the gene bank running",
	"MaintainWorkLedger":        "Keep production orders in order",
	"MaintainIdeoRoles":         "Fill ideology roles",
	"MaintainHerd":              "Tend the herd",
	"MaintainLighting":          "Keep workplaces lit",
	"EnsureMechCharger":         "Keep a mech charger",
	"MaintainMechs":             "Keep mechs gestating and fed",
	"EnsureMood":                "Keep moods up",
	"ManagePollution":           "Manage pollution",
	"MaintainPermits":           "Choose royal permits",
	"MaintainPopulation":        "Grow the population",
	"MaintainPsylink":           "Keep psylinks developing",
	"MaintainRituals":           "Hold rituals",
	"HoldGatherings":            "Hold a party",
	"MaintainRoutes":            "Keep facilities reachable",
	"MaintainShelter":           "Keep a safe shelter",
	"ClearAncientShrine":        "Clear the ancient shrine",
	"MaintainStockpiles":        "Keep stockpiles in order",
	"MaintainSurgery":           "Keep surgery available",
	"MaintainFireSafety":        "Guard against fire",
	"MaintainEssentialRepairs":  "Repair essential buildings",
	"MaintainCleanFacilities":   "Keep facilities clean",
	"MaintainBurial":            "Bury the dead",
	"MaintainTraining":          "Train combat skills",
	"MaintainIncineration":      "Burn the waste",
}

// concernLabel is the governor-speak name for a concern id.
func concernLabel(id string) string {
	if l, ok := concernLabels[id]; ok {
		return l
	}
	return id
}
