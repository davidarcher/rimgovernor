package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The census's skills reach DeriveGearRole, so a work-count tie splits on the
// pawn's best skill.
func TestApparelPolicySkillsBreakRoleTie(t *testing.T) {
	skill := func(name string, level int32) *o.Skill {
		return &o.Skill{DefName: proto.String(name), Level: proto.Int32(level), Passion: o.Passion_PASSION_NONE.Enum(), Disabled: proto.Bool(false)}
	}
	work := func(name string) *o.WorkSetting {
		return &o.WorkSetting{DefName: proto.String(name), Priority: proto.Int32(2), Disabled: proto.Bool(false)}
	}
	v := &o.ApparelPolicyState{Work: []*o.WorkSetting{work("Construction"), work("Crafting")}, Skills: []*o.Skill{skill("Construction", 3), skill("Crafting", 14)}}
	state, known := ApparelPolicyFacts(&o.GearLoadout{ApparelPolicy: v, Gender: d.Gender_GENDER_MALE.Enum(), DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum()}, &bridge.DefinitionCatalog{}).Value()
	if !known {
		t.Fatal("unknown")
	}
	// The loadout model resolves each work row's skill from the catalog.
	rows, _ := state.Role.Work.Work.Value()
	for i := range rows {
		rows[i].Skill = string(rows[i].Work)
	}
	if got := policy.DeriveGearRole(state.Role); got != policy.GearIndoor {
		t.Fatal(got)
	}
	v.Skills = nil
	state, _ = ApparelPolicyFacts(&o.GearLoadout{ApparelPolicy: v, Gender: d.Gender_GENDER_MALE.Enum(), DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum()}, &bridge.DefinitionCatalog{}).Value()
	if _, known := state.Role.Work.Skills.Value(); known {
		t.Fatal("absent skills read as known")
	}
	if got := policy.DeriveGearRole(state.Role); got != policy.GearWorker {
		t.Fatal(got)
	}
}
