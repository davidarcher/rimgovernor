package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
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
		slice := sliceRecorded(t, named("Silver"), "room_stat_defs")
		setRow[*d.RoomStatDef](slice, RoomStatImpressiveness).ScoreStages = stages
		if _, err := slice.catalog().ImpressivenessLevels(); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if _, err := sliceRecorded(t, named("Silver"), "weather_defs").catalog().ImpressivenessLevels(); err == nil {
		t.Error("a catalog without the row was accepted")
	}
	got, err := sharedRecordedCatalog(t).ImpressivenessLevels()
	if err != nil || got.Dull != 20 || got.SlightlyImpressive != 50 {
		t.Errorf("recorded levels %+v, %v", got, err)
	}
}
