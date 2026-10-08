package bridge

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/encoding/prototext"
)

func TestRoyaltyQuestFamilyCoverage(t *testing.T) {
	raw, err := os.ReadFile("testdata/royalty_quest_defs.textproto")
	if err != nil {
		t.Fatal(err)
	}
	var defs d.DefSets
	if err := prototext.Unmarshal(raw, &defs); err != nil {
		t.Fatal(err)
	}
	catalog := questCatalog(defs.GetQuestScriptDefs(), nil)
	if len(defs.GetQuestScriptDefs()) != 61 {
		t.Fatalf("Royalty mirror has %d rows, want 61", len(defs.GetQuestScriptDefs()))
	}
	for _, row := range defs.GetQuestScriptDefs() {
		profile, err := catalog.QuestProfile(row.GetDefName())
		if err != nil || profile.Family == policy.QuestFamilyUnknown {
			t.Errorf("%s: %+v, %v", row.GetDefName(), profile, err)
		}
		if row.GetAutoAccept() && !profile.NeverAct {
			t.Errorf("autoAccept %s can be acted on", row.GetDefName())
		}
	}
	for _, name := range []string{"RefugeeBetrayal", "EndGame_RoyalAscent"} {
		profile, err := catalog.QuestProfile(name)
		if err != nil || !profile.NeverAct || profile.Cost != policy.QuestCostTrap {
			t.Errorf("trap %s: %+v, %v", name, profile, err)
		}
	}
	for _, name := range []string{"Unrecognized", "Hospitality_Unknown", "ThreatReward_Unknown_Joiner"} {
		if got := policy.QuestFamilyForRoot(name); got.Family != policy.QuestFamilyUnknown {
			t.Errorf("unknown %s has profile %+v", name, got)
		}
		if got, err := questCatalog([]*d.QuestScriptDef{{DefName: name}}, nil).QuestProfile(name); err == nil || got.Family != policy.QuestFamilyUnknown {
			t.Errorf("catalog unknown %s: %+v, %v", name, got, err)
		}
	}
}

func TestOdysseyQuestFamilyUsesLayerFacts(t *testing.T) {
	catalog := recordedQuestCatalog(t)
	for name, family := range map[string]policy.QuestFamily{"SurveySite": policy.QuestFamilyOdysseyGround, "OpportunitySite_Asteroid": policy.QuestFamilyOdysseyShipOnly} {
		profile, err := catalog.QuestProfile(name)
		if err != nil || profile.Family != family {
			t.Errorf("%s: %+v, %v", name, profile, err)
		}
	}
}

func TestEveryDLCQuestRootHasDisposition(t *testing.T) {
	raw, err := os.ReadFile("testdata/dlc_quest_defs.textproto")
	if err != nil {
		t.Fatal(err)
	}
	var defs d.DefSets
	if err := prototext.Unmarshal(raw, &defs); err != nil {
		t.Fatal(err)
	}
	if len(defs.GetQuestScriptDefs()) != 90 {
		t.Fatalf("DLC mirror has %d rows, want 90", len(defs.GetQuestScriptDefs()))
	}
	for _, row := range defs.GetQuestScriptDefs() {
		profile := policy.QuestFamilyForRoot(row.GetDefName())
		if profile.Family == policy.QuestFamilyUnknown || profile.Disposition == "" {
			t.Errorf("unclassified %s", row.GetDefName())
		}
		if profile.Disposition == policy.QuestRefuse && (profile.SkipReason == "" || !profile.NeverAct) {
			t.Errorf("refusal %s: %+v", row.GetDefName(), profile)
		}
		if row.GetAutoAccept() && !profile.NeverAct {
			t.Errorf("automatic root %s can act: %+v", row.GetDefName(), profile)
		}
	}
	for _, name := range []string{"EndGame_ArchonexusVictory_FirstCycle", "EndGame_ShipEscape", "EndGame_VoidAwakening", "EndGame_VoidMonolith", "MonolithMigration", "MysteriousCargoUnnaturalCorpse", "MysteriousCargoCube", "MysteriousCargoRevenantSpine", "PollutionDump", "SanguophageShip", "TradeRequest"} {
		if profile := policy.QuestFamilyForRoot(name); profile.Disposition != policy.QuestRefuse || profile.SkipReason == "" {
			t.Errorf("unsafe root %s: %+v", name, profile)
		}
	}
}
