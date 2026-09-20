package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

const ClearHomeObstructions GoalID = "ClearHomeObstructions"

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
	Designated      bool
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
// and the free outdoor Home footprint a dumping stockpile could take.
type ClearanceCensus struct {
	Targets   []ClearanceTarget
	Chunks    []ClearanceChunk
	DumpSites []domain.Cell
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

// SelectHomeClearance admits one target so removals cannot jointly invalidate
// the individually observed roof support. Distance ties use stable identities.
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
		out.Targets = out.Targets[:1]
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

// Dump footprint bounds: at least a vanilla-sized corner, never more cells
// than the native flood reports.
const (
	minChunkDumpCells = 4
	maxChunkDumpCells = 16
)

// SelectChunkDump sizes and shapes the dumping stockpile for the pending
// chunks: one cell per pending stack within the bounds, taken from the native
// footprint in flood order after protected cells (held building footprints)
// are dropped, keeping only the connected run from the first free cell so the
// zone stays a single stockpile. Allow is every pending chunk definition plus
// steel slag, so a smelter bill can draw from the same dump. ok is false when
// nothing is pending or no connected cell remains.
func SelectChunkDump(rows []ClearanceChunk, sites []domain.Cell, protected []domain.Cell) (cells []domain.Cell, allow []string, ok bool) {
	pending := PendingChunks(rows)
	if len(pending) == 0 {
		return nil, nil, false
	}
	blocked := map[domain.Cell]bool{}
	for _, cell := range protected {
		blocked[cell] = true
	}
	wanted := len(pending)
	if wanted < minChunkDumpCells {
		wanted = minChunkDumpCells
	}
	if wanted > maxChunkDumpCells {
		wanted = maxChunkDumpCells
	}
	free := map[domain.Cell]bool{}
	var first *domain.Cell
	for i := range sites {
		if blocked[sites[i]] {
			continue
		}
		free[sites[i]] = true
		if first == nil {
			first = &sites[i]
		}
	}
	if first == nil {
		return nil, nil, false
	}
	reached := map[domain.Cell]bool{*first: true}
	queue := []domain.Cell{*first}
	for len(queue) > 0 && len(cells) < wanted {
		cell := queue[0]
		queue = queue[1:]
		cells = append(cells, cell)
		for _, delta := range []domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			next := domain.Cell{X: cell.X + delta.X, Z: cell.Z + delta.Z}
			if free[next] && !reached[next] {
				reached[next] = true
				queue = append(queue, next)
			}
		}
	}
	seen := map[string]bool{"ChunkSlagSteel": true}
	allow = []string{"ChunkSlagSteel"}
	for _, row := range pending {
		if !seen[row.DefName] {
			seen[row.DefName] = true
			allow = append(allow, row.DefName)
		}
	}
	sort.Strings(allow)
	return cells, allow, true
}
