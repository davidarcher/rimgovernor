package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ClearHomeObstructions is a Standard whose target is no outstanding work
// (#1024): no obstruction left standing on home ground.
const ClearHomeObstructions ConcernID = "ClearHomeObstructions"

// ClearanceTarget is a complete native building observation. Admission remains
// subject to a fresh observation and the native counterfactual roof check.
type ClearanceTarget struct {
	EntityID, DefName                      string
	Minimum, Maximum                       domain.Cell
	Class                                  string
	Deconstructible, InHome, AncientDanger bool
	RoofBlocker                            string
	// Designated is a standing Deconstruct designation, whoever placed it: not a
	// hold, since the Deconstruction operation adopts it rather than placing a
	// second one, and no ownership ledger says whose it was.
	Designated bool
	// Player marks the colony's own building, reported only on planned
	// ground (#1365); EnclosesRoom is then a wall or door bounding an
	// indoor room.
	Player, EnclosesRoom bool
	// Packable and InUse are stamped by the clearance planner (#2103): the
	// def packs into a minified item, and the piece is an owned bed or a bench
	// with an active bill.
	Packable, InUse bool
	Salvage         *SalvageEvidence
	// Count is the stack a foreign item holds (#2270); zero when unknown.
	Count           int64
	SalvageSelected bool
}

// ClearanceChunk is one rock or slag chunk stack standing on a Home cell: a
// haul, never a deconstruction. Stored is vanilla's own valid-storage test and
// Destination whether ordinary hauling already has a better store cell, so a
// chunk lacking both is one no stockpile will take.
type ClearanceChunk struct {
	EntityID, DefName              string
	Cell                           domain.Cell
	Forbidden, Stored, Destination bool
}

// ClearanceCensus is one native clearance read: the buildings, the chunks
// in Home.
type ClearanceCensus struct {
	Targets []ClearanceTarget
	Chunks  []ClearanceChunk
	// Floors are the constructed floor cells on the planned ground the read
	// asked for (#1365); none without it.
	Floors []ClearanceFloor
}

type ClearanceHold struct{ Target, Reason string }
type ClearanceSelection struct {
	Targets []ClearanceTarget
	Holds   []ClearanceHold
}

func ClearanceHoldReason(row ClearanceTarget) string {
	switch {
	case !row.InHome && !row.SalvageSelected:
		return "outside_home"
	case !row.Deconstructible:
		return "not_deconstructible"
	case row.RoofBlocker != "":
		return "roof_blocker"
	case row.AncientDanger:
		return "ancient_danger"
	case row.Class == "ancient_casket":
		return "casket"
	}
	return ""
}

// homeClearanceBatch bounds how many ancient-ruin targets one method takes.
const homeClearanceBatch = 12

// SelectHomeClearance admits one target so removals cannot jointly invalidate
// the individually observed roof support, except that ancient ruin pieces,
// which carry no roof of ours, come down together (up to homeClearanceBatch)
// when the nearest target is one. Distance ties use stable identities.
func SelectHomeClearance(rows []ClearanceTarget, center domain.Cell) ClearanceSelection {
	out := ClearanceSelection{}
	for _, row := range rows {
		if reason := ClearanceHoldReason(row); reason != "" {
			out.Holds = append(out.Holds, ClearanceHold{row.EntityID, reason})
		} else {
			out.Targets = append(out.Targets, row)
		}
	}
	distance := func(r ClearanceTarget) float64 {
		x := (float64(r.Minimum.X)+float64(r.Maximum.X))/2 - float64(center.X)
		z := (float64(r.Minimum.Z)+float64(r.Maximum.Z))/2 - float64(center.Z)
		return x*x + z*z
	}
	sort.Slice(out.Targets, func(i, j int) bool {
		a, b := out.Targets[i], out.Targets[j]
		if distance(a) != distance(b) {
			return distance(a) < distance(b)
		}
		return a.EntityID < b.EntityID
	})
	sort.Slice(out.Holds, func(i, j int) bool { return out.Holds[i].Target < out.Holds[j].Target })
	if len(out.Targets) > 1 {
		keep := 1
		if strings.HasPrefix(out.Targets[0].Class, "ancient_") {
			for keep < len(out.Targets) && keep < homeClearanceBatch && strings.HasPrefix(out.Targets[keep].Class, "ancient_") {
				keep++
			}
		}
		out.Targets = out.Targets[:keep]
	}
	return out
}

// ChunkHoldReason names why a chunk is not a clearance deficit: forbidden
// stacks are the supply safety policy's (#336), stored stacks are already
// cleared and a stack with a destination is ordinary hauling's to move.
func ChunkHoldReason(row ClearanceChunk) string {
	switch {
	case row.Forbidden:
		return "forbidden"
	case row.Stored:
		return "stored"
	case row.Destination:
		return "hauling"
	}
	return ""
}

// PendingChunks are the chunks no stockpile will take, stable by identity.
func PendingChunks(rows []ClearanceChunk) []ClearanceChunk {
	var out []ClearanceChunk
	for _, row := range rows {
		if ChunkHoldReason(row) == "" {
			out = append(out, row)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].EntityID < out[j].EntityID })
	return out
}

// HaulableChunks are the unforbidden, unstored chunks a store will take (#702):
// vanilla lists an unstored chunk as haulable only while it carries a Haul
// designation, so a destination alone never draws a hauler. Stable by
// identity.
func HaulableChunks(rows []ClearanceChunk) []ClearanceChunk {
	var out []ClearanceChunk
	for _, row := range rows {
		if !row.Forbidden && !row.Stored && row.Destination {
			out = append(out, row)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].EntityID < out[j].EntityID })
	return out
}
