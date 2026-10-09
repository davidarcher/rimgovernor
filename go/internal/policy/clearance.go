package policy

import (
	"sort"

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
	// Designated is a standing Deconstruct designation, whoever placed it.
	// It suppresses another write; the building remains work until removed.
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
	// SalvageSkipped: native ran out of salvage time budget before computing
	// this row; Salvage is absent because it was not computed.
	SalvageSkipped bool
	// Count is the stack a foreign item holds (#2270); zero when unknown.
	Count int64
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

// ClearanceHoldReason is the census row's own native verdict, wherever it
// stands: "" when nothing about the thing itself refuses its removal.
func ClearanceHoldReason(row ClearanceTarget) string {
	switch {
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
