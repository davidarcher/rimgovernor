package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// TestFullCatalogIdeologyDefs: the view over the whole game's recorded rows
// (every expansion): every precept comp typed, the role precepts apart from
// the plain ones, ritual patterns with their obligation buildings.
func TestFullCatalogIdeologyDefs(t *testing.T) {
	defs, err := fullCatalog(t).IdeologyDefs()
	if err != nil || defs == nil {
		t.Fatalf("%v %v", defs, err)
	}
	if len(defs.Precepts) < 100 || len(defs.Roles) == 0 || len(defs.Rituals) == 0 {
		t.Fatalf("%d precepts, %d roles, %d rituals", len(defs.Precepts), len(defs.Roles), len(defs.Rituals))
	}
	for name := range defs.Roles {
		if _, plain := defs.Precepts[name]; plain {
			t.Fatalf("role %s is also a precept", name)
		}
	}
	slavery := defs.Precepts["Slavery_Abhorrent"]
	var penalises bool
	for _, e := range slavery.Effects {
		penalises = penalises || e.Penalises()
	}
	if len(slavery.Effects) == 0 || !penalises {
		t.Fatalf("%+v", slavery)
	}
	if leader := defs.Roles["IdeoRole_Leader"]; leader.MaxCount != 1 {
		t.Fatalf("%+v", leader)
	}
	buildings := 0
	for _, ritual := range defs.Rituals {
		if len(ritual.RequiredBuildings) > 0 {
			buildings++
		}
	}
	if buildings == 0 {
		t.Fatal("no ritual names a building")
	}
}

// ideologyRows is a small Ideology catalog of generated rows: a slavery-issue
// precept that penalises and forbids, a role, a ritual pattern with a
// behavior and an obligation filter.
func ideologyRows() map[protoreflect.FullName]map[string]proto.Message {
	comp := func(c *d.PreceptCompAny) *d.Opt_PreceptCompAny { return &d.Opt_PreceptCompAny{Value: c} }
	rows := map[protoreflect.FullName]map[string]proto.Message{}
	add := func(name string, m proto.Message) {
		full := m.ProtoReflect().Descriptor().FullName()
		if rows[full] == nil {
			rows[full] = map[string]proto.Message{}
		}
		rows[full][name] = m
	}
	add("Enslaved_Abhorrent", &d.ThoughtDef{DefName: "Enslaved_Abhorrent", Stages: []*d.Opt_ThoughtStage{{Value: &d.ThoughtStage{BaseMoodEffect: -6}}, {}}})
	add("Slavery_Abhorrent", &d.PreceptDef{DefName: "Slavery_Abhorrent", PreceptClass: "RimWorld.Precept", Issue: "Slavery", Comps: []*d.Opt_PreceptCompAny{
		comp(&d.PreceptCompAny{Value: &d.PreceptCompAny_PreceptComp_SelfTookMemoryThought{PreceptComp_SelfTookMemoryThought: &d.PreceptComp_SelfTookMemoryThought{EventDef: "EnslavedPawn", Thought: "Enslaved_Abhorrent", OnlyForNonSlaves: true}}}),
		comp(&d.PreceptCompAny{Value: &d.PreceptCompAny_PreceptComp_UnwillingToDo_Chance{PreceptComp_UnwillingToDo_Chance: &d.PreceptComp_UnwillingToDo_Chance{EventDef: "SoldSlave", Chance: 0.2,
			NullifyingTraits: []*d.Opt_TraitRequirement{{Value: &d.TraitRequirement{Def: "Psychopath"}}}, NullifyingHediffs: []string{"Pain"}}}}),
		comp(&d.PreceptCompAny{Value: &d.PreceptCompAny_PreceptComp_Apparel{PreceptComp_Apparel: &d.PreceptComp_Apparel{}}}),
	}})
	add("Ritual_Sermon", &d.PreceptDef{DefName: "Ritual_Sermon", PreceptClass: "RimWorld.Precept", RitualPatternBase: "Sermon"})
	add("IdeoBuilding_Altar", &d.PreceptDef{DefName: "IdeoBuilding_Altar", PreceptClass: "RimWorld.Precept"})
	add("IdeoRole_Moral", &d.PreceptDef{DefName: "IdeoRole_Moral", PreceptClass: "RimWorld.Precept_Role", MaxCount: 1, RoleRequirements: []*d.Opt_RoleRequirementAny{
		{Value: &d.RoleRequirementAny{Value: &d.RoleRequirementAny_RoleRequirement_MinSkillAny{RoleRequirement_MinSkillAny: &d.RoleRequirement_MinSkillAny{Skills: []*d.Opt_SkillRequirement{{Value: &d.SkillRequirement{Skill: "Social", MinLevel: 6}}}}}}},
		{Value: &d.RoleRequirementAny{Value: &d.RoleRequirementAny_RoleRequirement_NotChild{RoleRequirement_NotChild: &d.RoleRequirement_NotChild{}}}},
	}})
	add("AltarFilter", &d.RitualObligationTargetFilterDef{DefName: "AltarFilter", ThingDefs: []string{"Lectern", "Altar"}})
	add("SermonBehavior", &d.RitualBehaviorDef{DefName: "SermonBehavior", Roles: []*d.Opt_RitualRoleAny{
		{Value: &d.RitualRoleAny{Value: &d.RitualRoleAny_RitualRoleOrganizer{RitualRoleOrganizer: &d.RitualRoleOrganizer{Id: "preacher", Precept: "IdeoRole_Moral", MaxCount: 1, Required: true}}}},
		{Value: &d.RitualRoleAny{Value: &d.RitualRoleAny_RitualRoleColonist{RitualRoleColonist: &d.RitualRoleColonist{Id: "reader", MaxCount: 2}}}},
	}})
	add("Sermon", &d.RitualPatternDef{DefName: "Sermon", RitualFreeStartIntervalDaysRange: &d.FloatRange{Min: 5, Max: 10}, RitualObligationTargetFilter: "AltarFilter", RitualBehavior: "SermonBehavior", CanStartAnytime: true})
	add("Structure_Animist", &d.MemeDef{DefName: "Structure_Animist"})
	return rows
}

func ideologyCatalogRows() *DefinitionCatalog {
	return &DefinitionCatalog{Defs: ideologyRows(), classBases: map[string][]string{
		"RimWorld.Precept":      {"System.Object"},
		"RimWorld.Precept_Role": {"RimWorld.Precept", "System.Object"},
	}}
}

// TestIdeologyDefsAreAViewOverRows: precept comps become typed effects with
// the moods of their thought's stages, a role precept is told apart by its
// class chain, a pattern reads its buildings from its obligation filter and
// its slots from its behavior.
func TestIdeologyDefsAreAViewOverRows(t *testing.T) {
	defs, err := ideologyCatalogRows().IdeologyDefs()
	if err != nil {
		t.Fatal(err)
	}
	if len(defs.Precepts) != 3 || len(defs.Roles) != 1 || len(defs.Rituals) != 1 {
		t.Fatalf("%+v", defs)
	}
	slavery := defs.Precepts["Slavery_Abhorrent"]
	if len(slavery.Effects) != 3 {
		t.Fatalf("%+v", slavery)
	}
	took, unwilling, apparel := slavery.Effects[0], slavery.Effects[1], slavery.Effects[2]
	if took.Kind != policy.EffectSelfTookAction || !took.Penalises() || took.Approves() || took.Forbids() || took.HistoryEvent != "EnslavedPawn" || !took.OnlyForNonSlaves || len(took.StageMoods) != 2 || took.StageMoods[0] != -6 {
		t.Fatalf("%+v", took)
	}
	if chance, known := unwilling.Chance.Value(); !unwilling.Forbids() || !known || chance != 0.2 || unwilling.HistoryEvent != "SoldSlave" || unwilling.NullifyingTraits[0] != "Psychopath" || unwilling.NullifyingHediffs[0] != "Pain" {
		t.Fatalf("%+v", unwilling)
	}
	if apparel.Kind != policy.EffectApparel {
		t.Fatalf("%+v", apparel)
	}
	role := defs.Roles["IdeoRole_Moral"]
	if role.MaxCount != 1 || len(role.Requirements) != 2 || role.Requirements[0].Skills[0] != (policy.SkillRequirement{Skill: "Social", MinLevel: 6}) || len(role.Requirements[1].Skills) != 0 {
		t.Fatalf("%+v", role)
	}
	ritual := defs.Rituals["Sermon"]
	if ritual.IntervalDaysMin != 5 || !ritual.CanStartAnytime || ritual.AlwaysStartAnytime || len(ritual.RequiredBuildings) != 2 || ritual.RequiredBuildings[0] != "Altar" {
		t.Fatalf("%+v", ritual)
	}
	if len(ritual.Roles) != 2 || ritual.Roles[0] != (policy.RitualRoleSlot{ID: "preacher", Precept: "IdeoRole_Moral", MaxCount: 1, Required: true}) || ritual.Roles[1] != (policy.RitualRoleSlot{ID: "reader", MaxCount: 2}) {
		t.Fatalf("%+v", ritual.Roles)
	}
}

// TestIdeologyDefsRefuseWhatTheRowsLack: a row a def names and the catalog
// lacks, and a comp of no class, are errors, never a skipped effect.
func TestIdeologyDefsRefuseWhatTheRowsLack(t *testing.T) {
	for name, change := range map[string]func(map[protoreflect.FullName]map[string]proto.Message){
		"missing-thought": func(rows map[protoreflect.FullName]map[string]proto.Message) {
			delete(rows[(&d.ThoughtDef{}).ProtoReflect().Descriptor().FullName()], "Enslaved_Abhorrent")
		},
		"missing-filter": func(rows map[protoreflect.FullName]map[string]proto.Message) {
			delete(rows[(&d.RitualObligationTargetFilterDef{}).ProtoReflect().Descriptor().FullName()], "AltarFilter")
		},
		"missing-behavior": func(rows map[protoreflect.FullName]map[string]proto.Message) {
			delete(rows[(&d.RitualBehaviorDef{}).ProtoReflect().Descriptor().FullName()], "SermonBehavior")
		},
		"empty-comp": func(rows map[protoreflect.FullName]map[string]proto.Message) {
			row := rows[(&d.PreceptDef{}).ProtoReflect().Descriptor().FullName()]["Slavery_Abhorrent"].(*d.PreceptDef)
			row.Comps = append(row.Comps, &d.Opt_PreceptCompAny{Value: &d.PreceptCompAny{}})
		},
		"null-role": func(rows map[protoreflect.FullName]map[string]proto.Message) {
			row := rows[(&d.RitualBehaviorDef{}).ProtoReflect().Descriptor().FullName()]["SermonBehavior"].(*d.RitualBehaviorDef)
			row.Roles = append(row.Roles, &d.Opt_RitualRoleAny{})
		},
	} {
		t.Run(name, func(t *testing.T) {
			catalog := ideologyCatalogRows()
			change(catalog.Defs)
			if _, err := catalog.IdeologyDefs(); err == nil {
				t.Fatal("incomplete ideology rows accepted")
			}
		})
	}
	if defs, err := (&DefinitionCatalog{}).IdeologyDefs(); err != nil || defs != nil {
		t.Fatalf("a catalog without Ideology rows: %v %v", defs, err)
	}
	if defs, err := (*DefinitionCatalog)(nil).IdeologyDefs(); err != nil || defs != nil {
		t.Fatalf("no catalog: %v %v", defs, err)
	}
}
