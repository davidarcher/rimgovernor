package buildingruntime

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func lanceRow(id string, downed, violent bool, apparel ...string) *n.PawnState {
	row := &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(id)}, Dead: proto.Bool(false), Downed: proto.Bool(downed), Equipment: &n.PawnEquipment{},
		Issues: []*n.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
	if !violent {
		row.Biography = &n.PawnBiography{DisabledWorkTags: []string{"Violent"}}
	} else {
		row.Biography = &n.PawnBiography{}
	}
	for i, def := range apparel {
		row.Equipment.Apparel = append(row.Equipment.Apparel, &n.GearItem{Thing: &n.EntityRef{Id: proto.String(def + string(rune('1'+i))), DefName: proto.String(def)}})
	}
	return row
}

func TestLanceUsersNeedAnAbleWearer(t *testing.T) {
	users := lanceUsers([]*n.PawnState{
		lanceRow("plain", false, true, "Apparel_Parka"),
		lanceRow("downed", true, true, "Apparel_PsychicShockLance"),
		lanceRow("pacifist", false, false, "Apparel_PsychicShockLance"),
		lanceRow("insane", false, true, "Apparel_PsychicInsanityLance"),
		lanceRow("wearer", false, true, "Apparel_Parka", "Apparel_PsychicShockLance"),
	})
	if len(users) != 1 || users[0].Pawn != "wearer" || users[0].Item != "Apparel_PsychicShockLance2" {
		t.Fatalf("users = %+v", users)
	}
}
