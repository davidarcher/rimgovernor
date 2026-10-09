package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func preyRow(id string, x, z int32) *n.PawnState {
	return &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(id), Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}, Animal: proto.Bool(true)}
}

// A hunt origin is a fight with no hostile: the live squad prey are
// the view's targets, each at its cell, and the view is a hunt.
func TestCombatFrameInputsHuntOriginTargetsPrey(t *testing.T) {
	t.Parallel()
	dead := preyRow("dead", 3, 3)
	dead.Dead = proto.Bool(true)
	combat := bridge.Combat{
		Emergency: bridge.EmergencyObservation{Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "rifle"}}}},
		Detail:    bridge.PawnsFromMap(map[string]*n.PawnState{"rifle": {Pawn: &n.EntityRef{Id: proto.String("rifle")}}, "deer": preyRow("deer", 40, 10), "boar": preyRow("boar", 42, 12), "dead": dead}),
	}
	prey := []domain.PawnID{"boar", "dead", "deer", "gone"}
	in, reason, err := combatFrameInputs(combat, prey)
	if err != nil || !reason.IsZero() {
		t.Fatalf("reason %q err %v", reason, err)
	}
	if !reflect.DeepEqual(in.prey, []string{"boar", "deer"}) {
		t.Fatalf("prey = %v", in.prey)
	}
	view := combatView(combat, in, nil, domain.Fact[policy.CombatLayout]{})
	if !view.Hunt || len(view.Threats) != 2 {
		t.Fatalf("view hunt %v threats %+v", view.Hunt, view.Threats)
	}
	cells := map[domain.PawnID]domain.Cell{}
	for _, s := range view.Pawns {
		if cell, ok := s.Cell.Value(); ok {
			cells[s.ID] = cell
		}
	}
	if cells["deer"] != (domain.Cell{X: 40, Z: 10}) || cells["boar"] != (domain.Cell{X: 42, Z: 12}) {
		t.Fatalf("prey cells = %v", cells)
	}
	// With no live prey the origin has no fight to decide.
	if _, reason, _ = combatFrameInputs(combat, []domain.PawnID{"dead", "gone"}); !reason.Is(WaitMethodUsed) {
		t.Fatalf("no live prey: reason %q", reason)
	}
}
