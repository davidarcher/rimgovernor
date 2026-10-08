package policy

import "testing"

func TestQuestFamiliesCarryWorkDemands(t *testing.T) {
	for name, want := range map[string]QuestProfile{
		"OpportunitySite_PeaceTalks": {Family: QuestFamilySite, Cost: QuestCostTime, Disposition: QuestFollow, Demands: QuestDemandPawns | QuestDemandTravel},
		"PawnLend":                   {Family: QuestFamilyPawnLend, Cost: QuestCostPawns, Disposition: QuestDecide, Demands: QuestDemandPawns | QuestDemandShuttle},
		"Mission_BanditCamp":         {Family: QuestFamilyBanditCamp, Cost: QuestCostPawns, Disposition: QuestDecide, Demands: QuestDemandPawns | QuestDemandTravel | QuestDemandSecurity},
		"Decree_ProduceItem":         {Family: QuestFamilyDecreeProduce, Cost: QuestCostTime, Disposition: QuestDecide, Demands: QuestDemandMaterials | QuestDemandProduction},
		"Decree_HarvestCrop":         {Family: QuestFamilyDecreeHarvest, Cost: QuestCostTime, Disposition: QuestDecide, Demands: QuestDemandHarvest},
		"Decree_HuntAnimal":          {Family: QuestFamilyDecreeHunt, Cost: QuestCostTime, Disposition: QuestDecide, Demands: QuestDemandHunt},
		"Decree_BuildMonument":       {Family: QuestFamilyDecreeMonument, Cost: QuestCostTime, Disposition: QuestDecide, Demands: QuestDemandMaterials | QuestDemandConstruction},
	} {
		if got := QuestFamilyForRoot(name); got != want {
			t.Errorf("%s: %+v, want %+v", name, got, want)
		}
	}
}
