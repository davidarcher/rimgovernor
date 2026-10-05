// Package farmselect reads the field planner's site-type selections out of
// a live service's fields_select flight rows so a native run can assert which
// crop and site kind the controller chose and why, independently of whether
// the zone or building receipts later resolve.
package farmselect

import (
	"errors"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Candidate is one crop under one site kind with its score breakdown.
type Candidate struct {
	Kind   string             `json:"kind"`
	Crop   string             `json:"crop"`
	Needed int                `json:"needed"`
	Cells  int                `json:"cells"`
	Score  float64            `json:"score"`
	Terms  map[string]float64 `json:"terms,omitempty"`
	Reason string             `json:"reason,omitempty"`
}

// Selection is one fields_select row with the candidates it scored.
type Selection struct {
	Kind       string      `json:"kind"`
	Crop       string      `json:"crop"`
	Cells      int         `json:"cells"`
	Buildings  int         `json:"buildings"`
	Urgent     bool        `json:"urgent"`
	Candidates []Candidate `json:"candidates"`
}

// Parse extracts every selection from a service's flight rows: a
// fields_select row carries the winner (reason the site kind, target the
// crop) and its candidates in attrs. Other rows are ignored.
func Parse(rows []na.FlightRow) ([]Selection, error) {
	var out []Selection
	for _, row := range rows {
		if row.Kind != "fields_select" {
			continue
		}
		f := row.Fields()
		s := Selection{Kind: text(f["reason"]), Crop: text(f["target"]), Cells: int(na.AsNumber(f["cells"])), Buildings: int(na.AsNumber(f["buildings"]))}
		s.Urgent, _ = f["urgent"].(bool)
		for _, item := range na.AsSlice(f["candidates"]) {
			m, ok := na.AsMap(item)
			if !ok {
				return nil, fmt.Errorf("fields_select row %d: candidate is %T", row.Sequence, item)
			}
			c := Candidate{Kind: text(m["kind"]), Crop: text(m["crop"]), Needed: int(na.AsNumber(m["needed"])), Cells: int(na.AsNumber(m["cells"])), Score: na.AsNumber(m["score"]), Reason: text(m["reason"])}
			if terms, ok := na.AsMap(m["terms"]); ok && len(terms) > 0 {
				c.Terms = make(map[string]float64, len(terms))
				for name, v := range terms {
					c.Terms[name] = na.AsNumber(v)
				}
			}
			s.Candidates = append(s.Candidates, c)
		}
		out = append(out, s)
	}
	return out, nil
}

func text(v any) string { s, _ := v.(string); return s }

// Expectation is what a run must show in every selection.
type Expectation struct {
	Kind     string
	Crop     string
	MinCells int
	Terms    []string
}

// Check asserts every selection chose the expected kind (and crop when set),
// that the winning candidate carries a score breakdown, and that every
// unplantable candidate states its reason. It returns the last selection
// as evidence.
func Check(selections []Selection, want Expectation) (Selection, error) {
	if len(selections) == 0 {
		return Selection{}, errors.New("no field selection was recorded; is the field family on?")
	}
	for i, s := range selections {
		if want.Kind != "" && s.Kind != want.Kind {
			return s, fmt.Errorf("selection %d chose kind %s, want %s", i, s.Kind, want.Kind)
		}
		if want.Crop != "" && s.Crop != want.Crop {
			return s, fmt.Errorf("selection %d chose crop %s, want %s", i, s.Crop, want.Crop)
		}
		if s.Cells < want.MinCells {
			return s, fmt.Errorf("selection %d planted %d cells, want at least %d", i, s.Cells, want.MinCells)
		}
		if len(s.Candidates) == 0 {
			return s, fmt.Errorf("selection %d has no candidate breakdown", i)
		}
		best := s.Candidates[0]
		if best.Kind != s.Kind || best.Crop != s.Crop || best.Cells == 0 || len(best.Terms) == 0 {
			return s, fmt.Errorf("selection %d winner %s %s lacks a term breakdown", i, best.Kind, best.Crop)
		}
		for _, name := range want.Terms {
			if _, known := best.Terms[name]; !known {
				return s, fmt.Errorf("selection %d lacks %s term", i, name)
			}
		}
		for _, c := range s.Candidates {
			if c.Cells == 0 && c.Reason == "" {
				return s, fmt.Errorf("selection %d candidate %s %s is unplantable without a reason", i, c.Kind, c.Crop)
			}
		}
	}
	return selections[len(selections)-1], nil
}
