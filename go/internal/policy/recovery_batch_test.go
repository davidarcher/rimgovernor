package policy

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var batchRoofRules = RoofRules{"RoofConstructed": {}, "RoofRockThin": {Natural: true}, "RoofRockThick": {Thick: true, Natural: true}}

func (w *roofWorld) roofDef(x, z int32, def string) {
	w.roof(x, z, true)
	c := domain.Cell{X: x, Z: z}
	r := w.rows[c]
	r.Roof = domain.Known(def)
	w.rows[c] = r
}

func batchTarget(id uint64, x, z int32) RecoveryBatchTarget {
	return RecoveryBatchTarget{ID: fmt.Sprintf("Thing_Wall%d", id), Cell: domain.Cell{X: x, Z: z}}
}

// Two holders each safe alone but unsafe together, under a thin roof: the roof
// comes off first, then the batch is admitted whole.
func TestPlanRecoveryBatchRoofPrecedesBatch(t *testing.T) {
	w := newRoofWorld()
	w.roofDef(20, 20, "RoofConstructed")
	w.thing(21, 20, 1, true)
	w.thing(19, 20, 2, true)
	req := RecoveryBatchRequest{Grid: w.grid(), Rules: batchRoofRules, Targets: []RecoveryBatchTarget{batchTarget(1, 21, 20), batchTarget(2, 19, 20)}}
	got := PlanRecoveryBatch(req)
	if got.Stage != RecoveryBatchRoof || len(got.Batch) != 0 || !slices.Contains(got.Roof, domain.Cell{X: 20, Z: 20}) {
		t.Fatalf("want a roof stage with no removal, got %+v", got)
	}
	if got.Blocker != RoofBlockerUnsupported {
		t.Fatalf("blocker %q", got.Blocker)
	}
	w.roof(20, 20, false) // the roof is down
	got = PlanRecoveryBatch(req)
	if got.Stage != RecoveryBatchRemoval || len(got.Batch) != 2 || len(got.Roof) != 0 {
		t.Fatalf("want both removals once the roof is down, got %+v", got)
	}
}

// A thick roof cannot come off: the unsafe second removal is trimmed from the
// batch and held, the first still goes.
func TestPlanRecoveryBatchTrimsUnsafeJointly(t *testing.T) {
	w := newRoofWorld()
	w.roofDef(20, 20, "RoofRockThick")
	w.thing(21, 20, 1, true)
	w.thing(19, 20, 2, true)
	got := PlanRecoveryBatch(RecoveryBatchRequest{Grid: w.grid(), Rules: batchRoofRules, Targets: []RecoveryBatchTarget{batchTarget(1, 21, 20), batchTarget(2, 19, 20)}})
	if got.Stage != RecoveryBatchRemoval || !slices.Equal(got.Batch, []string{"Thing_Wall1"}) {
		t.Fatalf("want the first removal only, got %+v", got)
	}
	if len(got.Held) != 1 || got.Held[0] != (RecoveryBatchHold{"Thing_Wall2", RemoteHoldRoofSupport}) {
		t.Fatalf("held %+v", got.Held)
	}
}

// Ancient ruin pieces carry no roof: a batch of them goes together up to the
// bound, with no roof stage.
func TestPlanRecoveryBatchAncientBatch(t *testing.T) {
	w := newRoofWorld()
	var targets []RecoveryBatchTarget
	for i := int32(0); i < 15; i++ {
		w.thing(40+i, 40, uint64(i)+1, false)
		targets = append(targets, batchTarget(uint64(i)+1, 40+i, 40))
	}
	got := PlanRecoveryBatch(RecoveryBatchRequest{Grid: w.grid(), Rules: batchRoofRules, Targets: targets})
	if got.Stage != RecoveryBatchRemoval || len(got.Batch) != DefaultRecoveryBatch || len(got.Roof) != 0 || len(got.Held) != 0 {
		t.Fatalf("got %+v", got)
	}
	got = PlanRecoveryBatch(RecoveryBatchRequest{Grid: w.grid(), Rules: batchRoofRules, Targets: targets, Max: 3})
	if len(got.Batch) != 3 {
		t.Fatalf("Max bounds the batch, got %+v", got)
	}
}

// A colony-owned roof cell stays up, and an unknown roof def is never taken
// off: the target is held instead.
func TestPlanRecoveryBatchLeavesPlayerAndUnknownRoofs(t *testing.T) {
	w := newRoofWorld()
	w.roofDef(20, 20, "RoofConstructed")
	w.thing(21, 20, 1, true)
	r := w.rows[domain.Cell{X: 20, Z: 20}]
	r.Things = []Thing{{Def: "Bed", Category: ThingBuilding, Faction: FactionPlayer, ID: 50}}
	w.rows[domain.Cell{X: 20, Z: 20}] = r
	req := RecoveryBatchRequest{Grid: w.grid(), Rules: batchRoofRules, Targets: []RecoveryBatchTarget{batchTarget(1, 21, 20)}}
	if got := PlanRecoveryBatch(req); got.Stage != RecoveryBatchNone || len(got.Held) != 1 {
		t.Fatalf("player roof must stay, got %+v", got)
	}
	w.rows[domain.Cell{X: 20, Z: 20}] = SiteCell{Cell: domain.Cell{X: 20, Z: 20}, Roofed: domain.Known(true), Roof: domain.Known("RoofMystery")}
	if got := PlanRecoveryBatch(req); got.Stage != RecoveryBatchNone || len(got.Held) != 1 {
		t.Fatalf("unknown roof def must stay, got %+v", got)
	}
}

func TestRecoveryBatchTargetsFollowQueue(t *testing.T) {
	q := RecoveryQueue{Entries: []RecoveryEntry{
		{ID: "a", Kind: RemoteSalvage, Status: RecoveryAdmitted},
		{ID: "b", Kind: RemoteSalvage, Status: RecoveryHeld},
		{ID: "c", Kind: RemoteLoot, Status: RecoveryQueued},
		{ID: "d", Kind: RemoteSalvage, Status: RecoveryQueued},
	}}
	safe := &SalvageEvidence{Safe: domain.Known(true)}
	rows := []ClearanceTarget{
		{EntityID: "a", Deconstructible: true, Salvage: safe, Minimum: domain.Cell{X: 1, Z: 2}},
		{EntityID: "d", Deconstructible: true, Salvage: safe, Minimum: domain.Cell{X: 3, Z: 4}},
	}
	got := RecoveryBatchTargets(q, rows)
	if len(got) != 2 || got[0].ID != "a" || got[1] != (RecoveryBatchTarget{"d", domain.Cell{X: 3, Z: 4}}) {
		t.Fatalf("got %+v", got)
	}
	// The fresh read wins over the review's queue: a row that has gone unsafe
	// or lost its evidence drops out.
	rows[0].Salvage = &SalvageEvidence{Safe: domain.Known(false)}
	rows[1].Salvage = nil
	if got := RecoveryBatchTargets(q, rows); len(got) != 0 {
		t.Fatalf("fresh holds ignored: %+v", got)
	}
}

func TestRoomObstructionIDsAreForeignRowsOnPlannedGround(t *testing.T) {
	row := func(id string, min, max domain.Cell) ClearanceTarget {
		return ClearanceTarget{EntityID: id, Minimum: min, Maximum: max}
	}
	ground := []Rectangle{{X: 10, Z: 10, Width: 5, Height: 5}}
	got := RoomObstructionIDs([]ClearanceTarget{
		row("inside", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}),
		row("edge", domain.Cell{X: 8, Z: 12}, domain.Cell{X: 10, Z: 12}),
		row("outside", domain.Cell{X: 15, Z: 10}, domain.Cell{X: 16, Z: 10}),
	}, ground)
	if len(got) != 2 || !got["inside"] || !got["edge"] {
		t.Fatalf("got %v", got)
	}
}
