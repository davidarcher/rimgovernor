package startuplabor

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SamplesFromPawnSnapshot reads pawn samples out of one
// rimgovernor/observations_list_pawns reply the service already made --
// the labor census every routine review takes. Nothing here calls native:
// the diagnosis rides the observation the review paid for, so no review
// grows a per-pawn RPC or a map scan.
//
// Only free, living colonists are sampled; an animal or a prisoner is not
// the labor this epic accounts for. An absent job block is a pawn with
// nothing to do (protojson drops the empty message) -- unless the row
// also carries read issues, in which case the job was not read and the
// sample is unknown rather than idle.
func SamplesFromPawnSnapshot(tick domain.Tick, reply map[string]any, restart bool) []PawnSample {
	rows := findPawnRows(reply, 0)
	if rows == nil {
		return nil
	}
	var out []PawnSample
	for _, raw := range rows {
		row, ok := raw.(map[string]any)
		if !ok {
			continue
		}
		if dead, _ := row["dead"].(bool); dead {
			continue
		}
		if free, _ := row["freeColonist"].(bool); !free {
			continue
		}
		ref, _ := row["pawn"].(map[string]any)
		id, _ := ref["id"].(string)
		if id == "" {
			continue
		}
		s := PawnSample{Pawn: id, Tick: tick, Restart: restart}
		s.Drafted, _ = row["drafted"].(bool)
		s.Downed, _ = row["downed"].(bool)
		s.InBed, _ = row["inBed"].(bool)
		s.Mental, _ = row["mentalState"].(string)
		issues, _ := row["issues"].([]any)
		if job, present := row["job"].(map[string]any); present {
			s.JobKnown = true
			s.Job, _ = job["defName"].(string)
		} else {
			s.JobKnown = len(issues) == 0
		}
		out = append(out, s)
	}
	return out
}

// findPawnRows locates the PawnSnapshot's pawns array inside a reply: at
// the top level for a bare ListPawnsReply, or under whatever envelope a
// flight-recorder row wrapped it in. The walk is depth-bounded so a large
// payload costs nothing to scan.
func findPawnRows(value any, depth int) []any {
	if depth > 4 {
		return nil
	}
	m, ok := value.(map[string]any)
	if !ok {
		return nil
	}
	if observed, ok := m["observed"].(map[string]any); ok {
		if rows, ok := observed["pawns"].([]any); ok {
			return rows
		}
	}
	for _, key := range []string{"result", "structured", "reply", "data", "response"} {
		if rows := findPawnRows(m[key], depth+1); rows != nil {
			return rows
		}
	}
	return nil
}
