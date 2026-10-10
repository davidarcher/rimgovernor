package bridge

import "path/filepath"

// ExplainStream tags a TimelineRecord that came from the explanation ring
// (explain.jsonl); rows of flight.jsonl carry the empty stream. Sequences are
// per stream, so a reader that tracks sequences keys them by Stream.
const ExplainStream = "explain"

// ExplainPath is the explanation ring that sits beside the flight recording at
// flightPath (see docs/developers/contracts/flight-rows.md, "Explanation rows").
func ExplainPath(flightPath string) string {
	return filepath.Join(filepath.Dir(flightPath), "explain.jsonl")
}

// ReadMergedTimeline reads flightPath's ring merged with the explanation ring
// beside it; with no explain.jsonl it is ReadTimeline.
func ReadMergedTimeline(flightPath string) ([]TimelineRecord, error) {
	return NewMergedTimelineReader(flightPath).Read()
}

// MergedTimelineReader reads a flight ring and its sibling explanation ring
// repeatedly. Each ring is assembled on its own, so its sequence gaps are
// judged against its own sequence only; the two row lists are then merged by
// wall time, ties keeping the flight row first. A recording_gap row sorts at
// the time of the row that follows it in its ring.
type MergedTimelineReader struct {
	flight, explain *TimelineReader
}

// NewMergedTimelineReader prepares a reader over flightPath and ExplainPath(flightPath).
func NewMergedTimelineReader(flightPath string) *MergedTimelineReader {
	return &MergedTimelineReader{flight: NewTimelineReader(flightPath), explain: NewTimelineReader(ExplainPath(flightPath))}
}

// Read returns both rings' rows in wall-time order; explanation rows carry Stream == ExplainStream.
func (r *MergedTimelineReader) Read() ([]TimelineRecord, error) {
	flight, err := r.flight.Read()
	if err != nil {
		return nil, err
	}
	explain, err := r.explain.Read()
	if err != nil {
		return nil, err
	}
	if len(explain) == 0 {
		return flight, nil
	}
	fa, ea := mergeTimes(flight), mergeTimes(explain)
	out := make([]TimelineRecord, 0, len(flight)+len(explain))
	i, j := 0, 0
	for i < len(flight) || j < len(explain) {
		if j >= len(explain) || (i < len(flight) && fa[i] <= ea[j]) {
			out = append(out, flight[i])
			i++
			continue
		}
		row := explain[j]
		row.Stream = ExplainStream
		out = append(out, row)
		j++
	}
	return out, nil
}

// mergeTimes is each row's merge time: its wall time, or for a synthetic gap
// row the wall time of the next timed row in its ring (the previous one when
// none follows). Times are made non-decreasing so each ring keeps its order.
func mergeTimes(rows []TimelineRecord) []float64 {
	times := make([]float64, len(rows))
	next, have := 0.0, false
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].Kind == "recording_gap" {
			if have {
				times[i] = next
			}
			continue
		}
		next, have = rows[i].WallTime, true
		times[i] = next
	}
	prev := 0.0
	for i := range rows {
		if rows[i].Kind == "recording_gap" && times[i] == 0 {
			times[i] = prev
		}
		if times[i] < prev {
			times[i] = prev
		}
		prev = times[i]
	}
	return times
}
