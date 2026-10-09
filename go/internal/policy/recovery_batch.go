package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Roof-first batch sequencing, executed by the clearance
// planner. The recovery queue's
// admitted and queued removals come down as one batch, not one building per
// review: when the batch's joint roof check (RoofSupportGrid.RoofBlocker)
// would leave unsupported roof, the removable (non-thick) roofs over the area
// are designated off first, and the batch is admitted once they are down.
// Roof removal is the existing remove_roof action. The function is stateless:
// a roof already down just stops blocking, so the next review's call reaches
// the removal stage. Whatever still blocks after the roofs (a thick roof, an
// unknown cell) trims the batch, one holding the roof_support_risk word.
//
// The native roof-collapse buffer (IsMarkedToCollapse) is not in the mirror,
// so RoofSupportGrid.CollapsePending stays nil here:
// a short-lived guard, since native re-runs its own counterfactual roof check
// at admission and refuses a removal that would collapse, so a pending
// collapse costs a refused removal, not a collapse.

// DefaultRecoveryBatch bounds one removal batch.
const DefaultRecoveryBatch = 12

// RecoveryBatchStage is what the batch does this review.
type RecoveryBatchStage string

const (
	// RecoveryBatchNone has nothing to do (no candidate, or all held).
	RecoveryBatchNone RecoveryBatchStage = "none"
	// RecoveryBatchRoof designates the Roof cells off; no removal yet.
	RecoveryBatchRoof RecoveryBatchStage = "roof"
	// RecoveryBatchRemoval admits Batch for deconstruction.
	RecoveryBatchRemoval RecoveryBatchStage = "removal"
)

// RecoveryBatchTarget is one queue removal: its census load id and a cell it
// occupies (the census Minimum).
type RecoveryBatchTarget struct {
	ID   string
	Cell domain.Cell
}

// RecoveryBatchRequest is the batch input. Targets are in queue rank order.
// Rules decide which roofs are removable; a roof whose def is not in them is
// left alone. Max bounds the batch (zero reads DefaultRecoveryBatch).
type RecoveryBatchRequest struct {
	Grid    RoofSupportGrid
	Rules   RoofRules
	Targets []RecoveryBatchTarget
	Max     int
}

// RecoveryBatchHold is a target the batch refuses, in the RemoteHoldReason
// vocabulary.
type RecoveryBatchHold struct{ ID, Reason string }

// RecoveryBatch is the verdict: the stage, the roof cells to take off (stage
// roof), the target ids to deconstruct together (stage removal), and the
// refused targets. Blocker is the first joint-check word that trimmed or
// staged anything.
type RecoveryBatch struct {
	Stage   RecoveryBatchStage
	Roof    []domain.Cell       `json:",omitempty"`
	Batch   []string            `json:",omitempty"`
	Held    []RecoveryBatchHold `json:",omitempty"`
	Blocker string              `json:",omitempty"`
}

// RecoveryBatchTargets are the queue's admitted and queued salvage entries in
// rank order, resolved against the fresh census rows. A row that has since
// gained a hold of its own (a native verdict, no evidence, an unsafe route)
// drops out, so the review's queue never overrides the fresh read. Loot is a
// haul, not a removal.
func RecoveryBatchTargets(q RecoveryQueue, rows []ClearanceTarget) []RecoveryBatchTarget {
	byID := make(map[string]ClearanceTarget, len(rows))
	for _, r := range rows {
		byID[r.EntityID] = r
	}
	var out []RecoveryBatchTarget
	for _, e := range q.Entries {
		if e.Kind != RemoteSalvage || e.Status != RecoveryAdmitted && e.Status != RecoveryQueued {
			continue
		}
		r, ok := byID[e.ID]
		if !ok {
			continue
		}
		if t, ok := RecoveryClearanceThing(r, false); ok && recoveryHold(t, RecoveryRequest{}) == "" {
			out = append(out, RecoveryBatchTarget{ID: e.ID, Cell: r.Minimum})
		}
	}
	return out
}

// buildingAt is the mirror building with load id at cell.
func (g RoofSupportGrid) buildingAt(c domain.Cell, loadID string) (Thing, bool) {
	row, ok := g.Cell(c)
	if !ok {
		return Thing{}, false
	}
	for _, t := range row.Things {
		if t.Category == ThingBuilding && t.LoadID() == loadID {
			return t, true
		}
	}
	return Thing{}, false
}

// removableRoofs are the roofed, removable cells within the support radius of
// the building's footprint, bar a cell carrying a player thing (the colony's
// own roof stays).
func (g RoofSupportGrid) removableRoofs(origin domain.Cell, id uint64, rules RoofRules) []domain.Cell {
	seen := map[domain.Cell]bool{}
	var out []domain.Cell
	r := int32(g.Radius)
	for _, f := range g.Footprint(origin, id) {
		for dz := -r; dz <= r; dz++ {
			for dx := -r; dx <= r; dx++ {
				c := domain.Cell{X: f.X + dx, Z: f.Z + dz}
				if seen[c] || !g.within(c, f) || !g.inBounds(c) {
					continue
				}
				seen[c] = true
				row, ok := g.Cell(c)
				if !ok {
					continue
				}
				if v, known := row.Roofed.Value(); !known || !v {
					continue
				}
				name, known := row.Roof.Value()
				if rule, in := rules[name]; !known || !in || !rule.Removable() {
					continue
				}
				player := false
				for _, t := range row.Things {
					player = player || t.Faction == FactionPlayer
				}
				if !player {
					out = append(out, c)
				}
			}
		}
	}
	return out
}

// PlanRecoveryBatch is the roof-first batch over the request. Targets are
// taken greedily in rank order: one is kept while the kept set plus it passes
// the joint roof check. One the check refuses because of unsupported roof
// stages the removable roofs around it; any other refusal, or an unsupported
// roof nothing can take off, holds it roof_support_risk. With roofs to take
// off the stage is roof and no removal is admitted until they are down.
func PlanRecoveryBatch(req RecoveryBatchRequest) RecoveryBatch {
	limit := req.Max
	if limit <= 0 {
		limit = DefaultRecoveryBatch
	}
	out := RecoveryBatch{Stage: RecoveryBatchNone}
	var kept []RemovedBuilding
	roofSet := map[domain.Cell]bool{}
	for _, t := range req.Targets {
		if len(out.Batch) >= limit {
			break
		}
		b, ok := req.Grid.buildingAt(t.Cell, t.ID)
		if !ok {
			out.Held = append(out.Held, RecoveryBatchHold{t.ID, RemoteHoldRoofSupport})
			continue
		}
		next := append(kept[:len(kept):len(kept)], RemovedBuilding{Cell: t.Cell, ID: b.ID})
		blocker, _ := req.Grid.RoofBlocker(next)
		if blocker == "" {
			kept = next
			out.Batch = append(out.Batch, t.ID)
			continue
		}
		if out.Blocker == "" {
			out.Blocker = blocker
		}
		var cells []domain.Cell
		if blocker == RoofBlockerUnsupported {
			cells = req.Grid.removableRoofs(t.Cell, b.ID, req.Rules)
		}
		if len(cells) == 0 {
			out.Held = append(out.Held, RecoveryBatchHold{t.ID, RemoteHoldRoofSupport})
			continue
		}
		for _, c := range cells {
			if !roofSet[c] {
				roofSet[c] = true
				out.Roof = append(out.Roof, c)
			}
		}
	}
	switch {
	case len(out.Roof) > 0:
		sort.Slice(out.Roof, func(i, j int) bool {
			a, b := out.Roof[i], out.Roof[j]
			return a.X < b.X || a.X == b.X && a.Z < b.Z
		})
		out.Stage, out.Batch = RecoveryBatchRoof, nil
	case len(out.Batch) > 0:
		out.Stage = RecoveryBatchRemoval
	}
	return out
}
