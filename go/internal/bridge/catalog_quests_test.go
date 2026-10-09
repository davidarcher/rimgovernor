package bridge

import (
	"os"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/encoding/prototext"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// recordedQuestCatalog is the catalog rows recorded from a headless Odyssey
// game: every Odyssey QuestScriptDef, one Core quest and the planet
// layers.
func recordedQuestCatalog(t *testing.T) *DefinitionCatalog {
	t.Helper()
	raw, err := os.ReadFile("testdata/odyssey_quest_defs.textproto")
	if err != nil {
		t.Fatal(err)
	}
	var sets d.DefSets
	if err := prototext.Unmarshal(raw, &sets); err != nil {
		t.Fatal(err)
	}
	return questCatalog(sets.GetQuestScriptDefs(), sets.GetPlanetLayerDefs())
}

func questCatalog(scripts []*d.QuestScriptDef, layers []*d.PlanetLayerDef) *DefinitionCatalog {
	catalog := &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{}}
	scriptRows, layerRows := map[string]proto.Message{}, map[string]proto.Message{}
	for _, row := range scripts {
		scriptRows[row.GetDefName()] = row
	}
	for _, row := range layers {
		layerRows[row.GetDefName()] = row
	}
	catalog.Defs[(&d.QuestScriptDef{}).ProtoReflect().Descriptor().FullName()] = scriptRows
	catalog.Defs[(&d.PlanetLayerDef{}).ProtoReflect().Descriptor().FullName()] = layerRows
	return catalog
}

// TestQuestClassOfTheRecordedOdysseyScripts: the Odyssey quests that
// need a space layer are the ship-only ones, read from the rows' layer
// references (Root_Gravcore.layer, Root_Asteroid.layerDef, Root_Site
// layerWhitelist); every other Odyssey script is ground, including the
// storyline's Surface gravcore sites; a Core quest is another family's.
func TestQuestClassOfTheRecordedOdysseyScripts(t *testing.T) {
	catalog := recordedQuestCatalog(t)
	shipOnly := map[string]string{
		"Gravcore_Mechhive": "Orbit", "Gravcore_OrbitalAncientPlatform": "Orbit", "Gravcore_OrbitalMechanoidPlatform": "Orbit",
		"OpportunitySite_Asteroid": "Orbit", "OpportunitySite_OrbitalItemStash": "Orbit",
		"OpportunitySite_AbandonedPlatform": "Orbit", "OpportunitySite_OrbitalWreck": "Orbit",
		"OpportunitySite_MechanoidPlatform": "Orbit", "OpportunitySite_Satellite": "Orbit",
		"OrbitalFugitive": "Orbit",
	}
	ground := []string{
		"GravEngine", "MechanoidSignal", "GravshipWreckage", "SurveySite",
		"Gravcore_AncientStockpile", "Gravcore_AncientReactor", "Gravcore_CrashedMechanoidPlatform", "Gravcore_FrozenTerraformer",
		"Gravcore_InsectLair", "Gravcore_MechanoidRelay",
		"OpportunitySite_AncientMercenaries", "OpportunitySite_BanditCamp_Giver", "OpportunitySite_ItemStash_Giver", "OpportunitySite_AlphaThrumbo_Giver",
		"Opportunity_AncientInfestedSettlement", "Opportunity_AncientStructureChemfuelRefinery", "Opportunity_AncientStructureGarrison",
		"Opportunity_AncientStructureLaunchSite", "Opportunity_AncientStructureWarehouse", "Util_SetupAncientStructureCommon",
	}
	for name, layer := range shipOnly {
		got, err := catalog.QuestClass(name)
		if err != nil || got != (policy.QuestClass{Scope: policy.QuestScopeShipOnly, SpaceLayer: layer}) {
			t.Errorf("%s = %+v, %v; want ship-only over %s", name, got, err, layer)
		}
	}
	for _, name := range ground {
		got, err := catalog.QuestClass(name)
		if err != nil || got != (policy.QuestClass{Scope: policy.QuestScopeGround}) {
			t.Errorf("%s = %+v, %v; want ground", name, got, err)
		}
	}
	if got, err := catalog.QuestClass("ThreatReward_Raid_Joiner"); err != nil || got.Scope != policy.QuestScopeOther {
		t.Errorf("a Core quest = %+v, %v", got, err)
	}
	// Every Odyssey row is classified: a script the lists above miss fails here.
	listed := len(shipOnly) + len(ground)
	odyssey := 0
	for _, row := range catalog.Defs[(&d.QuestScriptDef{}).ProtoReflect().Descriptor().FullName()] {
		if strings.EqualFold(row.(*d.QuestScriptDef).GetModPackageId(), odysseyPackageID) {
			odyssey++
		}
	}
	if odyssey != listed {
		t.Errorf("recorded %d Odyssey scripts, the test classifies %d", odyssey, listed)
	}
}

// TestQuestClassRules: the rules on built rows. A required space
// layer or a space-only whitelist needs a ship, a whitelist with a surface
// layer does not, a blacklist is ignored, a sub-script's layer counts, and
// an unknown script, sub-script or layer is an error.
func TestQuestClassRules(t *testing.T) {
	layers := []*d.PlanetLayerDef{{DefName: "Surface"}, {DefName: "Orbit", IsSpace: true}}
	odyssey := func(name string, root *d.QuestNodeAny, mutate ...func(*d.QuestScriptDef)) *d.QuestScriptDef {
		row := &d.QuestScriptDef{DefName: name, ModPackageId: proto.String("Ludeon.RimWorld.Odyssey"), Root: root}
		for _, m := range mutate {
			m(row)
		}
		return row
	}
	site := func(whitelist, blacklist string) *d.QuestNodeAny {
		return &d.QuestNodeAny{Value: &d.QuestNodeAny_QuestNode_Root_Site{QuestNode_Root_Site: &d.QuestNode_Root_Site{LayerWhitelist: whitelist, LayerBlacklist: blacklist}}}
	}
	sub := func(def string) *d.QuestNodeAny {
		return &d.QuestNodeAny{Value: &d.QuestNodeAny_QuestNode_SubScript{QuestNode_SubScript: &d.QuestNode_SubScript{Def: def}}}
	}
	scripts := []*d.QuestScriptDef{
		odyssey("SpaceList", site("<li>Orbit</li>", "")),
		odyssey("MixedList", site("<li>Orbit</li><li>Surface</li>", "")),
		odyssey("BlacklistOnly", site("", "<li>Orbit</li>")),
		odyssey("NoLayer", site("", "")),
		odyssey("NoRoot", nil),
		odyssey("ScriptWhitelist", nil, func(r *d.QuestScriptDef) { r.LayerWhitelist = []string{"Orbit"} }),
		odyssey("ViaSub", sub("SpaceList")),
		odyssey("Loop", sub("Loop")),
		odyssey("Expr", site("$layers", "")),
		odyssey("Ghost", site("<li>Void</li>", "")),
		odyssey("MissingSub", sub("Nowhere")),
		{DefName: "CoreSpace", Root: site("<li>Orbit</li>", "")},
	}
	catalog := questCatalog(scripts, layers)
	for name, want := range map[string]policy.QuestClass{
		"SpaceList":       {Scope: policy.QuestScopeShipOnly, SpaceLayer: "Orbit"},
		"MixedList":       {Scope: policy.QuestScopeGround},
		"BlacklistOnly":   {Scope: policy.QuestScopeGround},
		"NoLayer":         {Scope: policy.QuestScopeGround},
		"NoRoot":          {Scope: policy.QuestScopeGround},
		"ScriptWhitelist": {Scope: policy.QuestScopeShipOnly, SpaceLayer: "Orbit"},
		"ViaSub":          {Scope: policy.QuestScopeShipOnly, SpaceLayer: "Orbit"},
		"Loop":            {Scope: policy.QuestScopeGround},
		"CoreSpace":       {Scope: policy.QuestScopeOther},
	} {
		if got, err := catalog.QuestClass(name); err != nil || got != want {
			t.Errorf("%s = %+v, %v; want %+v", name, got, err, want)
		}
	}
	for _, name := range []string{"Expr", "Ghost", "MissingSub", "NoSuchScript"} {
		if got, err := catalog.QuestClass(name); err == nil {
			t.Errorf("%s classified as %+v without an error", name, got)
		}
	}
	if _, err := (*DefinitionCatalog)(nil).QuestClass("SpaceList"); err == nil {
		t.Error("a nil catalog classified a quest")
	}
}
