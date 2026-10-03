package observation

import (
	"maps"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// gearGarment is one apparel def of the gear fixtures: its ThingDef row
// facts and the stat values the game computes for it.
type gearGarment struct {
	layers, groups []string
	tags           []string
	sharp, blunt   float32
	speed          float32
}

// gearGarments is the armour catalog of a colony: tribalwear and clothing, a
// flak vest, helmets and plate armour. Only the Soldier-tagged garments are
// armour.
var gearGarments = map[string]gearGarment{
	"Apparel_TribalA":          {layers: []string{"OnSkin"}, groups: []string{"Torso", "Legs"}, sharp: .04, tags: []string{"Worker"}},
	"Apparel_Pants":            {layers: []string{"OnSkin"}, groups: []string{"Legs"}, sharp: .03, tags: []string{"Worker", "Soldier"}},
	"Apparel_Duster":           {layers: []string{"Shell"}, groups: []string{"Torso", "Neck", "Shoulders", "Arms", "Legs"}, sharp: .06, tags: []string{"Worker"}},
	"Apparel_FlakVest":         {layers: []string{"Middle"}, groups: []string{"Torso", "Neck"}, sharp: 1, blunt: .36, speed: -.12, tags: []string{"Soldier"}},
	"Apparel_SimpleHelmet":     {layers: []string{"Overhead"}, groups: []string{"UpperHead"}, sharp: .72, blunt: .26, tags: []string{"Soldier"}},
	"Apparel_AdvancedHelmet":   {layers: []string{"Overhead"}, groups: []string{"UpperHead"}, sharp: .9, blunt: .36, tags: []string{"Soldier"}},
	"Apparel_PlateArmor":       {layers: []string{"Middle", "Shell"}, groups: []string{"Torso", "Neck", "Shoulders", "Arms", "Legs"}, sharp: 1.2, blunt: .5, speed: -.8, tags: []string{"Soldier"}},
	"Apparel_PowerArmorHelmet": {layers: []string{"Overhead"}, groups: []string{"FullHead"}, sharp: 1.2, blunt: .5, tags: []string{"Soldier"}},
	"Apparel_Tuque":            {layers: []string{"Overhead"}, groups: []string{"UpperHead"}},
	"Apparel_ClothMask":        {layers: []string{"Overhead"}, groups: []string{"Mouth"}},
}

// gearModelDefs is the definition catalog of the gear fixtures, decoded the
// way the client does, with the finished research of a colony that has
// Smithing and FlakArmor but not PlateArmor or MarineArmor.
func gearModelDefs(t *testing.T) GearDefinitions {
	t.Helper()
	id := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	v := &o.DefinitionCatalog{Context: &c.ObservationContext{Identity: id, Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)},
		TerrainDefs: []*d.TerrainDef{{DefName: "Soil"}}, Defs: &d.DefSets{StatDefs: []*d.StatDef{{DefName: "MarketValue"}}}, StatValues: &o.DefStatTable{Stats: []string{statArmorSharp, statArmorBlunt, statInsulationCold, statInsulationHeat, statMarketValue}},
		Constants: &o.CatalogConstants{TicksPerHour: 2500, TicksPerDay: 60000, DaysPerYear: 60, BillStackMax: 15, SkillMaxLevel: 20, LitGlowThreshold: 0.3}}
	for _, name := range slices.Sorted(maps.Keys(gearGarments)) {
		g := gearGarments[name]
		row := &d.ThingDef{DefName: name, Apparel: &d.ApparelProperties{Layers: g.layers, BodyPartGroups: g.groups, DefaultOutfitTags: g.tags, DevelopmentalStageFilter: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT}}
		if g.speed != 0 {
			row.EquippedStatOffsets = []*d.StatModifier{{Stat: statMoveSpeed, Value: g.speed}}
		}
		v.ThingDefs = append(v.ThingDefs, row)
		// A stat the game does not show for a def is absent from its row:
		// the unarmoured garments show no blunt armor.
		stats, values := []int32{0, 2, 3, 4}, []float32{g.sharp, 2, 1, 100}
		if g.blunt != 0 {
			stats, values = []int32{0, 1, 2, 3, 4}, []float32{g.sharp, g.blunt, 2, 1, 100}
		}
		v.StatValues.Rows = append(v.StatValues.Rows, &o.DefStatRow{DefName: name, Stat: stats, Value: values})
	}
	catalog, err := bridge.DecodeDefinitionCatalog(v, id)
	if err != nil {
		t.Fatal(err)
	}
	return GearDefinitions{Catalog: catalog, Finished: finishedSet([]string{"ComplexClothing", "FlakArmor", "Smithing"})}
}

// gearModelOption is one native loadout-model option at Normal quality: the
// instance facts only; layers, groups and stats come from the catalog.
func gearModelOption(id, def, source string, research []string, ingredients map[string]int64) *o.GearLoadoutOption {
	x := &o.GearLoadoutOption{Id: proto.String(id), DefName: proto.String(def), Quality: proto.Int32(2), Source: proto.String(source), Condition: proto.Float64(1), Research: research}
	for _, name := range slices.Sorted(maps.Keys(ingredients)) {
		x.Ingredients = append(x.Ingredients, &o.Quantity{DefName: proto.String(name), Units: proto.Int64(ingredients[name])})
	}
	return x
}

// gearModelCensus is the gear census a native producer sends for two drafted
// soldiers and one grower in tribalwear, with the armour options of a colony
// that has finished Smithing and FlakArmor but not PlateArmor or MarineArmor.
func gearModelCensus() *o.ColonyFactsSnapshot {
	catalog := func() []*o.GearLoadoutOption {
		return []*o.GearLoadoutOption{
			gearModelOption("bill:Apparel_Pants/", "Apparel_Pants", "bill", []string{"ComplexClothing"}, map[string]int64{"Cloth": 40}),
			gearModelOption("bill:Apparel_Duster/", "Apparel_Duster", "bill", []string{"ComplexClothing"}, map[string]int64{"Cloth": 80}),
			gearModelOption("bill:Apparel_FlakVest/", "Apparel_FlakVest", "bill", []string{"FlakArmor"}, map[string]int64{"Steel": 60, "Cloth": 30, "ComponentIndustrial": 1}),
			gearModelOption("bill:Apparel_SimpleHelmet/", "Apparel_SimpleHelmet", "bill", []string{"Smithing"}, map[string]int64{"Steel": 40}),
			gearModelOption("bill:Apparel_AdvancedHelmet/", "Apparel_AdvancedHelmet", "bill", []string{"FlakArmor"}, map[string]int64{"Steel": 40, "Plasteel": 10, "ComponentIndustrial": 2}),
			gearModelOption("bill:Apparel_PlateArmor/", "Apparel_PlateArmor", "bill", []string{"PlateArmor"}, map[string]int64{"Steel": 170}),
			gearModelOption("bill:Apparel_PowerArmorHelmet/", "Apparel_PowerArmorHelmet", "bill", []string{"MarineArmor"}, map[string]int64{"Plasteel": 40}),
		}
	}
	pawn := func(id string, drafted bool, gender d.Gender) *o.GearLoadout {
		worn := gearModelOption("tribal-"+id, "Apparel_TribalA", "worn", nil, nil)
		return &o.GearLoadout{
			Pawn:               &c.Ref{Id: proto.String(id)},
			Snapshot:           &o.SnapshotRef{Token: proto.String("loadout-" + id)},
			Deficit:            proto.Bool(false),
			ComfortableMinC:    proto.Float64(16),
			ComfortableMaxC:    proto.Float64(26),
			Gender:             gender.Enum(),
			DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum(),
			BodyPartGroups:     []string{"Torso", "Neck", "Shoulders", "Arms", "Legs", "UpperHead", "FullHead", "Mouth"},
			Equipment:          &o.PawnEquipment{Apparel: []*o.GearItem{{Thing: &c.Ref{Id: proto.String("tribal-" + id)}, ConditionFraction: proto.Float64(1)}}},
			ApparelPolicy:      &o.ApparelPolicyState{Token: proto.String("policy-" + id), Drafted: proto.Bool(drafted)},
			LoadoutModel:       &o.GearLoadoutModel{Worn: []*o.GearLoadoutOption{worn}, Options: catalog()},
		}
	}
	gear := &o.GearSnapshot{Pawns: []*o.GearLoadout{pawn("soldier-a", true, d.Gender_GENDER_MALE), pawn("soldier-b", true, d.Gender_GENDER_FEMALE), pawn("grower", false, d.Gender_GENDER_MALE)}}
	return &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(3), OutdoorTemperatureC: proto.Float64(21), Planning: &o.PlanningSection{Outcome: &o.PlanningSection_Observed{Observed: &o.PlanningFacts{Gear: gear}}}}
}

// A native census carrying the loadout model plans both soldiers a flak vest
// and a helmet (#956, the gear/soldier armour half of #769): PlanColonyGear
// no longer returns unknown demand, plate armour is refused for its speed,
// the marine helmet for its unfinished research, and the grower is planned
// no armour.
func TestColonyGearLoadoutModelPlansSoldierArmour(t *testing.T) {
	gear := colonyGear(gearModelCensus(), gearModelTables(), gearModelDefs(t))
	v, known := gear.Value()
	if !known {
		t.Fatal("census unknown")
	}
	pawns := map[policy.PawnID]policy.GearPawn{}
	for _, p := range v.Pawns {
		if _, known := p.LoadoutModel.Value(); !known {
			t.Fatal("no loadout model", p.Pawn)
		}
		pawns[p.Pawn] = p
	}
	model, _ := pawns["soldier-b"].LoadoutModel.Value()
	if !model.Female || model.Ambient != 21 || len(model.Worn) != 1 || model.Worn[0].Slot != policy.GearSkinTorso || !slices.Equal(model.Research, []string{"ComplexClothing", "FlakArmor", "Smithing"}) {
		t.Fatal("model inputs", model)
	}
	if worn := model.Worn[0]; worn.Sharp != float64(float32(.04)) || worn.Blunt != 0 || worn.Cold != 2 || worn.Heat != 1 || worn.Cost != 100 || !slices.Equal(worn.Layers, []string{"OnSkin"}) || !slices.Equal(worn.Groups, []string{"Torso", "Legs"}) {
		t.Fatal("worn stats are the catalog's", worn)
	}
	flak := func() policy.GearOption {
		for _, o := range model.Options {
			if o.Definition == "Apparel_FlakVest" {
				return o
			}
		}
		t.Fatal("no flak vest option")
		return policy.GearOption{}
	}()
	if flak.Sharp != 1 || flak.MoveSpeed != float64(float32(-.12)) || flak.Slot != policy.GearMiddleTorso || !slices.Equal(flak.Research, []string{"FlakArmor"}) {
		t.Fatal("flak vest", flak)
	}
	grower, _ := pawns["grower"].LoadoutModel.Value()
	for _, option := range grower.Options {
		if option.Definition == "Apparel_FlakVest" || option.Definition == "Apparel_SimpleHelmet" {
			t.Fatal("worker offered armour its policy forbids", option.Definition)
		}
	}
	loadouts, demand, err := policy.PlanColonyGear(v.Pawns)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := demand.Value()
	if !known {
		t.Fatal("demand unknown")
	}
	counts := map[policy.Resource]int{}
	for _, d := range rows {
		counts[d.Definition] += d.Count
	}
	if counts["Apparel_FlakVest"] != 2 || counts["Apparel_SimpleHelmet"]+counts["Apparel_AdvancedHelmet"] != 2 {
		t.Fatal("soldier armour demand", rows)
	}
	if counts["Apparel_PlateArmor"] != 0 || counts["Apparel_PowerArmorHelmet"] != 0 {
		t.Fatal("refused armour planned", rows)
	}
	for _, l := range loadouts {
		wantSoldier := l.Pawn != "grower"
		if (l.Role == policy.GearSoldier) != wantSoldier {
			t.Fatal("role", l.Pawn, l.Role)
		}
		for _, g := range l.Gaps {
			if !wantSoldier && (g.Wanted.Sharp > .5) {
				t.Fatal("grower planned armour", g.Wanted.Definition)
			}
		}
	}
	review, err := policy.ReviewGear(gear)
	if err != nil || review.Recovered != domain.Known(false) {
		t.Fatal("review", review.Recovered, err)
	}
	if needs := policy.GearReplacementNeeds(gear); !slices.Contains(needs, "Apparel_FlakVest") {
		t.Fatal("bench needs", needs)
	}
	if !policy.GearSoldierPresent(gear) {
		t.Fatal("no soldier")
	}
}

// The apparel policy's definitions are the apparel rows the pawn can wear
// (#1732): its gender, its single developmental stage and a present part in
// one of the garment's body part groups; armor is the Soldier outfit tag
// alone, and covers-body a Torso or Legs group.
func TestApparelPolicyDefinitionsAreTheWearableRows(t *testing.T) {
	defs := gearModelDefs(t)
	census := gearModelCensus().GetPlanning().GetObserved().GetGear().Pawns[0]
	state, known := ApparelPolicyFacts(census, defs.Catalog).Value()
	if !known {
		t.Fatal("policy unknown")
	}
	armor, covers := map[string]bool{}, map[string]bool{}
	names := []string{}
	for _, def := range state.Definitions {
		names = append(names, def.Name)
		armor[def.Name], covers[def.Name] = def.Armor, def.CoversBody
		if def.Child || !def.Adult {
			t.Fatal("stage filter", def)
		}
	}
	if len(names) != len(gearGarments) || !slices.IsSorted(names) {
		t.Fatal("definitions", names)
	}
	if !armor["Apparel_FlakVest"] || armor["Apparel_Pants"] || armor["Apparel_Duster"] || armor["Apparel_Tuque"] {
		t.Fatal("armor is the Soldier tag without Worker", armor)
	}
	if !covers["Apparel_FlakVest"] || !covers["Apparel_Pants"] || covers["Apparel_SimpleHelmet"] {
		t.Fatal("covers body", covers)
	}
	for name, change := range map[string]func(*o.GearLoadout){
		"child": func(p *o.GearLoadout) { p.DevelopmentalStage = d.DevelopmentalStage_DEVELOPMENTAL_STAGE_CHILD.Enum() },
		"no mouth": func(p *o.GearLoadout) {
			p.BodyPartGroups = []string{"Torso", "Neck", "Shoulders", "Arms", "Legs", "UpperHead", "FullHead"}
		},
		"no legs":    func(p *o.GearLoadout) { p.BodyPartGroups = []string{"UpperHead", "Mouth"} },
		"no wearer":  func(p *o.GearLoadout) { p.Gender = nil },
		"no catalog": func(p *o.GearLoadout) {},
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(census).(*o.GearLoadout)
			change(p)
			catalog := defs.Catalog
			if name == "no catalog" {
				catalog = nil
			}
			state, known := ApparelPolicyFacts(p, catalog).Value()
			switch name {
			case "child":
				if len(state.Definitions) != 0 {
					t.Fatal("a child wears adult garments", state.Definitions)
				}
			case "no mouth":
				if slices.ContainsFunc(state.Definitions, func(d policy.ApparelDefinition) bool { return d.Name == "Apparel_ClothMask" }) || len(state.Definitions) != len(gearGarments)-1 {
					t.Fatal("a pawn without a mouth wears a mask", state.Definitions)
				}
			case "no legs":
				if len(state.Definitions) != 4 {
					t.Fatal("head garments only", state.Definitions)
				}
			default:
				if known {
					t.Fatal("known without the wear inputs or catalog")
				}
			}
		})
	}
}

// A producer without the model, role or temperatures leaves the model
// unknown, so the census keeps the native deficit path; so does a model the
// policy's bounds refuse (two worn helmets on one slot), a garment the
// catalog lacks and unknown finished research.
func TestColonyGearLoadoutModelUnknownWithoutInputs(t *testing.T) {
	defs := gearModelDefs(t)
	v := gearModelCensus()
	v.OutdoorTemperatureC = nil
	if gear, _ := colonyGear(v, gearModelTables(), defs).Value(); func() bool { _, k := gear.Pawns[0].LoadoutModel.Value(); return k }() {
		t.Fatal("model known without the outdoor temperature")
	}
	if _, known := colonyGear(gearModelCensus(), gearModelTables(), GearDefinitions{}).Value(); known {
		t.Fatal("census known without a catalog")
	}
	for name, change := range map[string]func(*o.GearSnapshot, *GearDefinitions){
		"no model":   func(g *o.GearSnapshot, _ *GearDefinitions) { g.Pawns[0].LoadoutModel = nil },
		"no role":    func(g *o.GearSnapshot, _ *GearDefinitions) { g.Pawns[0].ApparelPolicy = nil },
		"no comfort": func(g *o.GearSnapshot, _ *GearDefinitions) { g.Pawns[0].ComfortableMinC = nil },
		"no gender":  func(g *o.GearSnapshot, _ *GearDefinitions) { g.Pawns[0].Gender = nil },
		"no research": func(_ *o.GearSnapshot, defs *GearDefinitions) {
			defs.Finished = domain.Unknown[map[string]bool]()
		},
		"unknown def": func(g *o.GearSnapshot, _ *GearDefinitions) {
			g.Pawns[0].LoadoutModel.Options[0].DefName = proto.String("Apparel_Missing")
		},
		"slot collision": func(g *o.GearSnapshot, _ *GearDefinitions) {
			hat := gearModelOption("hat", "Apparel_Tuque", "worn", nil, nil)
			mask := gearModelOption("mask", "Apparel_ClothMask", "worn", nil, nil)
			g.Pawns[0].LoadoutModel.Worn = append(g.Pawns[0].LoadoutModel.Worn, hat, mask)
		},
	} {
		t.Run(name, func(t *testing.T) {
			v, defs := gearModelCensus(), gearModelDefs(t)
			change(v.GetPlanning().GetObserved().GetGear(), &defs)
			gear, _ := colonyGear(v, gearModelTables(), defs).Value()
			if _, known := gear.Pawns[0].LoadoutModel.Value(); known {
				t.Fatal("model known")
			}
			if _, demand, err := policy.PlanColonyGear(gear.Pawns); err != nil || isKnown(demand) {
				t.Fatal("colony demand known without every model", err)
			}
		})
	}
}

func isKnown[T any](f domain.Fact[T]) bool {
	_, known := f.Value()
	return known
}

// gearModelTables holds the worn tribal wear gearModelCensus references.
func gearModelTables() bridge.Tables {
	things := bridge.Things{}
	for _, id := range []string{"soldier-a", "soldier-b", "grower"} {
		things = things.With("tribal-"+id, &o.Thing{Thing: &o.EntityRef{Id: proto.String("tribal-" + id), DefName: proto.String("Apparel_TribalA")}})
	}
	return bridge.Tables{Things: things}
}
