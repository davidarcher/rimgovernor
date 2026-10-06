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
	Faction                                string
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
// and the free outdoor Home footprint.
type ClearanceCensus struct {
	Targets   []ClearanceTarget
	Chunks    []ClearanceChunk
	DumpSites []domain.Cell
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

// ShellClaims is the census rows a starter ring claims as wall (#718): each
// building covering one of the ring's claimable ruin cells, outside any
// ancient danger. Ordered by identity.
func ShellClaims(rows []ClearanceTarget, cells []domain.Cell) []ClearanceTarget {
	var out []ClearanceTarget
	for _, row := range rows {
		if claimHold(shellRuinHold(row)) || !coversAny(row, cells) {
			continue
		}
		out = append(out, row)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].EntityID < out[j].EntityID })
	return out
}

// shellRuinHold is the census hold a starter ring's clear rung honours: every
// hold but lying outside Home, with the holds that also refuse a claim named
// first so claimHold sees them.
func shellRuinHold(row ClearanceTarget) string {
	switch {
	case row.AncientDanger:
		return "ancient_danger"
	case row.Class == "ancient_casket":
		return "casket"
	}
	row.InHome = true
	return ClearanceHoldReason(row)
}

// claimHold reports a shell hold that refuses a claim as well as a clearing.
func claimHold(hold string) bool { return hold == "ancient_danger" || hold == "casket" }

// ShellRuinHolds stamps each site cell a census building covers with that
// building's shell hold (#718), so the planned ring counts a ruin
// claimed exactly where PlannedLayout and ShellClaims would act on it. A cell
// under several buildings keeps a claim-refusing hold over any other, and
// otherwise the first in identity order.
func ShellRuinHolds(rows []ClearanceTarget, cells []SiteCell) []SiteCell {
	ordered := append([]ClearanceTarget(nil), rows...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].EntityID < ordered[j].EntityID })
	out := append([]SiteCell(nil), cells...)
	for i := range out {
		for _, row := range ordered {
			if !coversAny(row, []domain.Cell{out[i].Cell}) {
				continue
			}
			if hold := shellRuinHold(row); hold != "" && (out[i].RuinHold == "" || claimHold(hold) && !claimHold(out[i].RuinHold)) {
				out[i].RuinHold = hold
			}
		}
	}
	return out
}

func coversAny(row ClearanceTarget, cells []domain.Cell) bool {
	for _, c := range cells {
		if c.X >= row.Minimum.X && c.X <= row.Maximum.X && c.Z >= row.Minimum.Z && c.Z <= row.Maximum.Z {
			return true
		}
	}
	return false
}
