package combatlab

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A resumed lab checkpoint holds every raider already in Stance_Warmup on a
// target (combatsim A44), so the raiders' first targets are fixed by the saved
// checkpoint, not drawn per run. This is that input for the fixtures whose
// checkpoint save was read: for hostile i of the fixture (the sim's h<i>, in
// spawn order, which is thing id order) the colonist index it is aimed at
// (the sim's c<j>). Fixtures absent here have no recorded targets and the sim
// draws them.
type stagedTargets struct {
	// digest is the staged checkpoint's digest (combatmetrics.SeedRow
	// .StagedDigest of the committed baseline); a re-staged checkpoint is a
	// different digest and these targets no longer apply.
	digest string
	// aim is the colonist index each hostile is aimed at, by hostile index.
	aim []int
}

// stagedFirstTargetTable is read from the checkpoint save
// (RimGovernor-checkpoint-combatlab-metrics-<fixture>.rws): each raider's
// <curStance Class="Stance_Warmup"> <focusTarg>, with the pawns' thing ids
// mapped to indexes by id order. It agrees with the first shot of every raider
// in the real seed-1 shot ledger of the same digest, and every run of the
// committed baseline resumes that one checkpoint (the reseed does not touch a
// stance).
//
// lab-split-ranged, digest ca770629: raiders 7264 7273 7282 7294 7302 7307
// 7316 7325 aim at colonists 7198 7210 7210 7218 7210 7218 7198 7210.
//
// lab-ranged is not here: its ledger has two target sets (seeds 1 to 13 and 14
// to 20, A44) and the shot calibration feeds each seed's from the ledger.
var stagedFirstTargetTable = map[string]stagedTargets{
	"lab-split-ranged": {digest: "ca770629", aim: []int{0, 1, 1, 2, 1, 2, 0, 1}},
}

// stagedFirstTargets returns the raider-to-colonist targets of the named
// fixture's checkpoint, nil when none was recorded.
func stagedFirstTargets(fixture string) map[domain.PawnID]domain.PawnID {
	t, ok := stagedFirstTargetTable[fixture]
	if !ok {
		return nil
	}
	out := make(map[domain.PawnID]domain.PawnID, len(t.aim))
	for h, c := range t.aim {
		out[domain.PawnID(fmt.Sprintf("h%d", h))] = domain.PawnID(fmt.Sprintf("c%d", c))
	}
	return out
}
