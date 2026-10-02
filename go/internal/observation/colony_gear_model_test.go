package observation

import (
	"maps"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// gearModelOption is one native loadout-model option at Normal quality.
func gearModelOption(id, def, source string, layers, groups []string, sharp, blunt, speed float64, research []string, ingredients map[string]int64) *o.GearLoadoutOption {
	x := &o.GearLoadoutOption{Id: proto.String(id), DefName: proto.String(def), Quality: proto.Int32(2), Source: proto.String(source), ApparelLayers: layers, BodyPartGroups: groups, Condition: proto.Float64(1), ArmorSharp: proto.Float64(sharp), ArmorBlunt: proto.Float64(blunt), InsulationCold: proto.Float64(2), InsulationHeat: proto.Float64(1), MoveSpeed: proto.Float64(speed), MarketValue: proto.Float64(100), Research: research}
	for _, name := range slices.Sorted(maps.Keys(ingredients)) {
		x.Ingredients = append(x.Ingredients, &o.Quantity{DefName: proto.String(name), Units: proto.Int64(ingredients[name])})
	}
	return x
}

// gearModelCensus is the gear census a native producer sends for two drafted
// soldiers and one grower in tribalwear, with the armour catalog of a colony
// that has finished Smithing and FlakArmor but not PlateArmor or
// MarineArmor.
func gearModelCensus() *o.ColonyFactsSnapshot {
	definitions := []*o.ApparelPolicyDefinition{}
	for def, armor := range map[string]bool{"Apparel_TribalA": false, "Apparel_Pants": false, "Apparel_Duster": false, "Apparel_FlakVest": true, "Apparel_SimpleHelmet": true, "Apparel_AdvancedHelmet": true, "Apparel_PlateArmor": true, "Apparel_PowerArmorHelmet": true} {
		definitions = append(definitions, &o.ApparelPolicyDefinition{DefName: proto.String(def), Armor: proto.Bool(armor), Adult: proto.Bool(true)})
	}
	slices.SortFunc(definitions, func(a, b *o.ApparelPolicyDefinition) int { return strings.Compare(a.GetDefName(), b.GetDefName()) })
	catalog := func() []*o.GearLoadoutOption {
		return []*o.GearLoadoutOption{
			gearModelOption("bill:Apparel_Pants/Cloth", "Apparel_Pants", "bill", []string{"OnSkin"}, []string{"Legs"}, .03, 0, 0, []string{"ComplexClothing"}, map[string]int64{"Cloth": 40}),
			gearModelOption("bill:Apparel_Duster/Cloth", "Apparel_Duster", "bill", []string{"Shell"}, []string{"Torso", "Neck", "Shoulders", "Arms", "Legs"}, .06, 0, 0, []string{"ComplexClothing"}, map[string]int64{"Cloth": 80}),
			gearModelOption("bill:Apparel_FlakVest/", "Apparel_FlakVest", "bill", []string{"Middle"}, []string{"Torso", "Neck"}, 1, .36, -.12, []string{"FlakArmor"}, map[string]int64{"Steel": 60, "Cloth": 30, "ComponentIndustrial": 1}),
			gearModelOption("bill:Apparel_SimpleHelmet/Steel", "Apparel_SimpleHelmet", "bill", []string{"Overhead"}, []string{"UpperHead"}, .72, .26, 0, []string{"Smithing"}, map[string]int64{"Steel": 40}),
			gearModelOption("bill:Apparel_AdvancedHelmet/Steel", "Apparel_AdvancedHelmet", "bill", []string{"Overhead"}, []string{"UpperHead"}, .9, .36, 0, []string{"FlakArmor"}, map[string]int64{"Steel": 40, "Plasteel": 10, "ComponentIndustrial": 2}),
			gearModelOption("bill:Apparel_PlateArmor/Steel", "Apparel_PlateArmor", "bill", []string{"Middle", "Shell"}, []string{"Torso", "Neck", "Shoulders", "Arms", "Legs"}, 1.2, .5, -.8, []string{"PlateArmor"}, map[string]int64{"Steel": 170}),
			gearModelOption("bill:Apparel_PowerArmorHelmet/", "Apparel_PowerArmorHelmet", "bill", []string{"Overhead"}, []string{"FullHead"}, 1.2, .5, 0, []string{"MarineArmor"}, map[string]int64{"Plasteel": 40}),
		}
	}
	pawn := func(id string, drafted, female bool) *o.GearLoadout {
		worn := gearModelOption("tribal-"+id, "Apparel_TribalA", "worn", []string{"OnSkin"}, []string{"Torso", "Legs"}, .04, 0, 0, nil, nil)
		return &o.GearLoadout{
			Pawn:            &o.EntityRef{Id: proto.String(id)},
			Snapshot:        &o.SnapshotRef{Token: proto.String("loadout-" + id)},
			Deficit:         proto.Bool(false),
			ComfortableMinC: proto.Float64(16),
			ComfortableMaxC: proto.Float64(26),
			Equipment:       &o.PawnEquipment{Apparel: []*o.GearItem{{Thing: &c.Ref{Id: proto.String("tribal-" + id)}, BodyPartGroups: []string{"Torso", "Legs"}, ConditionFraction: proto.Float64(1)}}},
			ApparelPolicy:   &o.ApparelPolicyState{Token: proto.String("policy-" + id), Drafted: proto.Bool(drafted), Definitions: definitions},
			LoadoutModel:    &o.GearLoadoutModel{Female: proto.Bool(female), Worn: []*o.GearLoadoutOption{worn}, Options: catalog()},
		}
	}
	gear := &o.GearSnapshot{Pawns: []*o.GearLoadout{pawn("soldier-a", true, false), pawn("soldier-b", true, true), pawn("grower", false, false)}, FinishedResearch: []string{"ComplexClothing", "FlakArmor", "Smithing"}}
	return &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(3), OutdoorTemperatureC: proto.Float64(21), Planning: &o.PlanningSection{Outcome: &o.PlanningSection_Observed{Observed: &o.PlanningFacts{Gear: gear}}}}
}

// A native census carrying the loadout model plans both soldiers a flak vest
// and a helmet (#956, the gear/soldier armour half of #769): PlanColonyGear
// no longer returns unknown demand, plate armour is refused for its speed,
// the marine helmet for its unfinished research, and the grower is planned
// no armour.
func TestColonyGearLoadoutModelPlansSoldierArmour(t *testing.T) {
	gear := colonyGear(gearModelCensus(), gearModelTables())
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
	if model, _ := pawns["soldier-b"].LoadoutModel.Value(); !model.Female || model.Ambient != 21 || len(model.Worn) != 1 || model.Worn[0].Slot != policy.GearSkinTorso {
		t.Fatal("model inputs", model)
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

// A producer without the model, role or temperatures leaves the model
// unknown, so the census keeps the native deficit path; so does a model the
// policy's bounds refuse (two worn helmets on one slot).
func TestColonyGearLoadoutModelUnknownWithoutInputs(t *testing.T) {
	v := gearModelCensus()
	v.OutdoorTemperatureC = nil
	if gear, _ := colonyGear(v, gearModelTables()).Value(); func() bool { _, k := gear.Pawns[0].LoadoutModel.Value(); return k }() {
		t.Fatal("model known without the outdoor temperature")
	}
	for name, change := range map[string]func(*o.GearSnapshot){
		"no model":   func(g *o.GearSnapshot) { g.Pawns[0].LoadoutModel = nil },
		"no role":    func(g *o.GearSnapshot) { g.Pawns[0].ApparelPolicy = nil },
		"no comfort": func(g *o.GearSnapshot) { g.Pawns[0].ComfortableMinC = nil },
		"slot collision": func(g *o.GearSnapshot) {
			hat := gearModelOption("hat", "Apparel_Tuque", "worn", []string{"Overhead"}, []string{"UpperHead"}, 0, 0, 0, nil, nil)
			mask := gearModelOption("mask", "Apparel_ClothMask", "worn", []string{"Overhead"}, []string{"Mouth"}, 0, 0, 0, nil, nil)
			g.Pawns[0].LoadoutModel.Worn = append(g.Pawns[0].LoadoutModel.Worn, hat, mask)
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := gearModelCensus()
			change(v.GetPlanning().GetObserved().GetGear())
			gear, _ := colonyGear(v, gearModelTables()).Value()
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
		things["tribal-"+id] = &o.Thing{Thing: &o.EntityRef{Id: proto.String("tribal-" + id), DefName: proto.String("Apparel_TribalA")}}
	}
	return bridge.Tables{Things: things}
}
