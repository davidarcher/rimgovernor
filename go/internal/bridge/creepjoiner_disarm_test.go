package bridge

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func bodyRecord(def string, groups []string, children ...*d.BodyPartRecord) *d.BodyPartRecord {
	record := &d.BodyPartRecord{Def: def, Groups: groups}
	for _, c := range children {
		record.Parts = append(record.Parts, &d.Opt_BodyPartRecord{Value: c})
	}
	return record
}

func defsOf(m proto.Message, rows ...proto.Message) (protoreflect.FullName, map[string]proto.Message) {
	out := map[string]proto.Message{}
	for _, r := range rows {
		out[r.ProtoReflect().Get(r.ProtoReflect().Descriptor().Fields().ByName("defName")).String()] = r
	}
	return m.ProtoReflect().Descriptor().FullName(), out
}

// TestDisarmSitesComeFromTheBodyAndToolRows: a race's sites are the
// lowest part holding each melee tool's body part group, except the group of
// the always-usable tool; a race with no always-usable tool has none; the
// install price is the explicit-def ingredient slots only.
func TestDisarmSitesComeFromTheBodyAndToolRows(t *testing.T) {
	// Pre-order: 0 Torso, 1 Head, 2 Jaw, 3 Shoulder, 4 Arm, 5 Hand, 6-7 Fingers, 8 Hand, 9-10 Fingers.
	body := bodyRecord("Torso", nil,
		bodyRecord("Head", []string{"HeadAttackTool"}, bodyRecord("Jaw", []string{"Teeth"})),
		bodyRecord("Shoulder", nil, bodyRecord("Arm", nil, bodyRecord("Hand", []string{"Hands"},
			bodyRecord("Finger", []string{"LeftHand"}), bodyRecord("Finger", []string{"LeftHand"})))),
		bodyRecord("Hand", []string{"Hands"}, bodyRecord("Finger", []string{"RightHand"}), bodyRecord("Finger", []string{"RightHand"})))
	tool := func(group string, always bool) *d.Opt_Tool {
		return &d.Opt_Tool{Value: &d.Tool{LinkedBodyPartsGroup: group, EnsureLinkedBodyPartsGroupAlwaysUsable: always}}
	}
	human := &d.ThingDef{DefName: "Human", Race: &d.RaceProperties{Body: "Human"},
		Tools: []*d.Opt_Tool{tool("LeftHand", false), tool("RightHand", false), tool("Teeth", false), tool("HeadAttackTool", true)}}
	biter := &d.ThingDef{DefName: "Biter", Race: &d.RaceProperties{Body: "Human"}, Tools: []*d.Opt_Tool{tool("Teeth", false)}}
	catalog := &DefinitionCatalog{
		ThingDefs: map[string]*d.ThingDef{"Human": human, "Biter": biter},
		Defs:      map[protoreflect.FullName]map[string]proto.Message{},
	}
	add := func(name protoreflect.FullName, rows map[string]proto.Message) { catalog.Defs[name] = rows }
	add(defsOf(&d.BodyDef{}, &d.BodyDef{DefName: "Human", CorePart: body}))
	add(defsOf(&d.PawnKindDef{}, &d.PawnKindDef{DefName: "Colonist", Race: "Human"}, &d.PawnKindDef{DefName: "Chew", Race: "Biter"}))
	add(defsOf(&d.RecipeDef{},
		&d.RecipeDef{DefName: "InstallWoodenHand", AddsHediff: "WoodenHand", AppliedOnFixedBodyParts: []string{"Hand"},
			Ingredients: []*d.Opt_IngredientCount{slot(&d.ThingFilter{ThingDefs: []string{"WoodLog"}}, 2), slot(&d.ThingFilter{Categories: []string{"Medicine"}}, 1)}},
		&d.RecipeDef{DefName: "InstallDenture", AddsHediff: "Denture", AppliedOnFixedBodyParts: []string{"Jaw"}},
		&d.RecipeDef{DefName: "CookMeal", Ingredients: []*d.Opt_IngredientCount{slot(&d.ThingFilter{ThingDefs: []string{"WoodLog"}}, 1)}},
		&d.RecipeDef{DefName: "InstallUnpriced", AddsHediff: "X", AppliedOnFixedBodyParts: []string{"Hand"},
			Ingredients: []*d.Opt_IngredientCount{slot(&d.ThingFilter{ThingDefs: []string{"Mystery"}}, 1)}}))
	catalog.withRecordedStats(t, "WoodLog").ThingDefs["WoodLog"].ThingCategories = nil

	got, err := catalog.CreepJoinerDisarm()
	if err != nil {
		t.Fatal(err)
	}
	wantSites := map[string][]policy.DisarmSite{
		"Colonist": {{Index: 2, Part: "Jaw", Ancestors: []int{1, 0}}, {Index: 5, Part: "Hand", Ancestors: []int{4, 3, 0}}, {Index: 8, Part: "Hand", Ancestors: []int{0}}},
	}
	if !reflect.DeepEqual(got.Sites, wantSites) {
		t.Fatalf("sites = %+v, want %+v", got.Sites, wantSites)
	}
	wantValue := map[string]float64{"InstallWoodenHand": 2 * float64(float32(1.2)), "InstallDenture": 0}
	if !reflect.DeepEqual(got.InstallValue, wantValue) {
		t.Fatalf("install values = %v, want %v", got.InstallValue, wantValue)
	}
	if again, _ := catalog.CreepJoinerDisarm(); !reflect.DeepEqual(again, got) {
		t.Fatal("the disarm input changed between reads")
	}
	none, err := (*DefinitionCatalog)(nil).CreepJoinerDisarm()
	if err != nil || len(none.Sites) != 0 {
		t.Fatal("a missing catalog has sites", none, err)
	}
}
