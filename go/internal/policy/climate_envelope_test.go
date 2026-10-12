package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func raisedEnvelope(t *testing.T, owed domain.Fact[bool]) bool {
	t.Helper()
	f := stableRounds()
	f.EnvelopeOwed = owed
	for _, c := range needs(t, f, RoundsLatches{}).Concerns {
		if c.ID == MaintainClimateEnvelope {
			return true
		}
	}
	return false
}

func TestEnvelopeOwedRaisesOnlyWhileSomethingIsMissing(t *testing.T) {
	if !raisedEnvelope(t, domain.Known(true)) {
		t.Fatal("an owed vent or roof raised no MaintainClimateEnvelope")
	}
	if raisedEnvelope(t, domain.Known(false)) {
		t.Fatal("a complete envelope raised MaintainClimateEnvelope")
	}
	if raisedEnvelope(t, domain.Unknown[bool]()) {
		t.Fatal("an unknown envelope raised MaintainClimateEnvelope")
	}
}

func TestEnvelopeOpsKeepsVentsAndRoofsOnly(t *testing.T) {
	ops := []Operation{{Kind: OpWallIn}, {Kind: OpVentIn}, {Kind: OpFloorIn}, {Kind: OpRoofIn}, {Kind: OpBuild}}
	got := EnvelopeOps(ops)
	if len(got) != 2 || got[0].Kind != OpVentIn || got[1].Kind != OpRoofIn {
		t.Fatalf("EnvelopeOps = %+v", got)
	}
}

func TestEnvelopeHallwayRoofIsTheRoofedSetOutsideEveryRoom(t *testing.T) {
	in, _ := climateFixture()
	hall := domain.Cell{X: 20, Z: 20}
	inside := domain.Cell{X: 10, Z: 10}
	plan := in.Plan
	plan.Roofed = RegionOf(append(plan.Roofed.Cells(), hall))
	open := map[domain.Cell]bool{hall: true, inside: true, {X: 30, Z: 30}: true}
	if got := EnvelopeHallwayRoof(plan, open); !slices.Equal(got, []domain.Cell{hall}) {
		t.Fatalf("hallway roof = %v, want only %v", got, hall)
	}
	if got := EnvelopeHallwayRoof(plan, map[domain.Cell]bool{}); len(got) != 0 {
		t.Fatalf("a roofed hallway is owed %v", got)
	}
	plan.Roofed = Region{}
	if got := EnvelopeHallwayRoof(plan, open); len(got) != 0 {
		t.Fatalf("a temperate plan roofs %v", got)
	}
}
