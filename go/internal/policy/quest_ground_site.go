package policy

// OdysseyGroundSiteRoot lists ground expeditions with the shared combat and
// native reform/loot return chain. World-layer facts still reject space routes.
func OdysseyGroundSiteRoot(root string) bool {
	switch root {
	case "Gravcore_InsectLair", "Gravcore_AncientReactor", "Gravcore_AncientStockpile", "Gravcore_CrashedMechanoidPlatform", "Gravcore_FrozenTerraformer", "Gravcore_MechanoidRelay",
		"GravshipWreckage", "OpportunitySite_AncientMercenaries", "OpportunitySite_BanditCamp_Giver", "OpportunitySite_ItemStash_Giver", "OpportunitySite_AlphaThrumbo_Giver", "OpportunitySite_DistressCall",
		"Opportunity_AncientStructureLaunchSite", "Opportunity_AncientStructureGarrison", "Opportunity_AncientStructureChemfuelRefinery", "Opportunity_AncientStructureWarehouse", "Opportunity_AncientInfestedSettlement":
		return true
	}
	return false
}
