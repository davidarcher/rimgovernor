package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func TestImpressivenessLevelsRefuseAMalformedRoomStat(t *testing.T) {
	stage := func(min float32) *d.Opt_RoomStatScoreStage {
		return &d.Opt_RoomStatScoreStage{Value: &d.RoomStatScoreStage{MinScore: min}}
	}
	for name, stages := range map[string][]*d.Opt_RoomStatScoreStage{
		"too few stages":  {stage(0), stage(20), stage(30), stage(40)},
		"not rising":      {stage(0), stage(20), stage(30), stage(30), stage(50)},
		"dull not above0": {stage(0), stage(0), stage(30), stage(40), stage(50)},
	} {
		catalog := &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{
			(&d.RoomStatDef{}).ProtoReflect().Descriptor().FullName(): {RoomStatImpressiveness: &d.RoomStatDef{DefName: RoomStatImpressiveness, ScoreStages: stages}},
		}}
		if _, err := catalog.ImpressivenessLevels(); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if _, err := (&DefinitionCatalog{}).ImpressivenessLevels(); err == nil {
		t.Error("a catalog without the row was accepted")
	}
	got, err := FixtureCatalog("load").ImpressivenessLevels()
	if err != nil || got.Dull != 20 || got.SlightlyImpressive != 50 {
		t.Errorf("fixture levels %+v, %v", got, err)
	}
}
