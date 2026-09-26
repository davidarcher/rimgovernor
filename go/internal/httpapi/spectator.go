package httpapi

import (
	"context"
	"net/http"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
)

// Spectator (#632): GET /api/spectator/now is the watcher's one read —
// what the colony is trying to do (the colony stage and the active goals'
// progress records), why the clock runs as it does (the pacing reason and
// the effective TPS) and the last clock stop with its latency split.
//
// It composes state the controller already keeps: the last review's records
// from the routine provider and the flight-recorder rows, plus the observed
// tick from the state snapshot. Like /api/state it is read-only and
// unauthenticated, and reading it issues no native call, writes no journal
// row and requests no speed — watching never changes the simulation
// contract. The pacing, stop and TPS fields need the flight recorder; a
// serve without one still answers, with the recorder-derived fields at
// their zero values and Pacing.Reason "unknown".
const spectatorNowPath = "/api/spectator/now"

func (s *Server) handleSpectator(w http.ResponseWriter, r *http.Request) bool {
	if r.URL.Path != spectatorNowPath {
		return false
	}
	if s.config.Routines == nil {
		s.failure(w, r, 404, "not_found", "Routine diagnostics are not enabled")
		return true
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.Header().Set("Allow", "GET, HEAD")
		s.failure(w, r, 405, "method_not_allowed", "The spectator panel is read-only")
		return true
	}
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) != 0 || r.URL.RawQuery != "" || r.URL.ForceQuery {
		s.failure(w, r, 400, "invalid_request", "This read requires a bounded URL, no query and no body")
		return true
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	status, err := s.config.Routines.RoutineStatus(ctx)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return true
	}
	in := spectator.Input{Stage: status.Stage, Progress: status.Progress, ReviewsEnabled: status.ReviewsEnabled}
	snapshot, err := s.snapshots.Snapshot(ctx)
	if err != nil {
		s.readFailure(w, r, err)
		return true
	}
	if tick, known := snapshot.Tick.Value(); known && snapshot.Connected {
		value := int64(tick)
		in.Tick = &value
	}
	// The holds awaiting review are a journal read, the same one the clock
	// review route serves; without supervision there are none.
	if s.config.ClockReview != nil {
		review, e := s.config.ClockReview.Read(ctx)
		if e != nil {
			s.readFailure(w, r, e)
			return true
		}
		for _, hold := range review.Holds {
			in.Holds = append(in.Holds, string(hold.Kind))
		}
	}
	var rows []bridge.TimelineRecord
	if s.config.FlightRecorder != "" && s.telemetry != nil {
		all, e := s.telemetry.Read()
		if e != nil {
			s.readFailure(w, r, e)
			return true
		}
		rows = currentRun(all)
		metrics := telemetryMetrics(rows, snapshot, 0, time.Now())
		in.TPS = metrics.TPS
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return true
	}
	s.write(w, r, 200, spectator.Project(rows, in))
	return true
}

// currentRun keeps the rows of the newest launch in the ring: the launch
// answering this request, whose pacing and stops are the live ones.
func currentRun(rows []bridge.TimelineRecord) []bridge.TimelineRecord {
	run := ""
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].HasSeq {
			run = rows[i].Run
			break
		}
	}
	current := rows[:0:0]
	for _, row := range rows {
		if row.HasSeq && row.Run == run {
			current = append(current, row)
		}
	}
	return current
}
