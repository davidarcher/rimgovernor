// Package farmselect reads the field planner's site-type selections out of
// a live service's clock-scheduler trace so a native run can assert which
// crop and site kind the controller chose and why, independently of whether
// the zone or building receipts later resolve.
package farmselect

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"regexp"
	"strconv"
	"strings"
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

// Selection is one "Fields select:" trace with the candidates that followed it.
type Selection struct {
	Kind       string      `json:"kind"`
	Crop       string      `json:"crop"`
	Cells      int         `json:"cells"`
	Buildings  int         `json:"buildings"`
	Urgent     bool        `json:"urgent"`
	Candidates []Candidate `json:"candidates"`
}

var (
	selectLine    = regexp.MustCompile(`^\[clock-scheduler\] Fields select: kind=(\S+) crop=(\S+) cells=(\d+) buildings=(\d+) \| \S+ \S+ needed=\d+ urgent=(true|false) buildings=\d+$`)
	candidateLine = regexp.MustCompile(`^ (\S+) (\S+) needed=(\d+) cells=(\d+) score=(-?[0-9.]+)(.*)$`)
	termToken     = regexp.MustCompile(`^([a-z]+)=(-?[0-9.]+)$`)
)

// Parse extracts every selection from a service stderr log. Candidate lines
// are the indented continuation lines of the planner's Explain output; any
// other line ends the selection.
func Parse(r io.Reader) ([]Selection, error) {
	var out []Selection
	var current *Selection
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 1024*1024), 1024*1024)
	for scanner.Scan() {
		line := strings.TrimRight(scanner.Text(), "\r")
		if m := selectLine.FindStringSubmatch(line); m != nil {
			cells, _ := strconv.Atoi(m[3])
			buildings, _ := strconv.Atoi(m[4])
			out = append(out, Selection{Kind: m[1], Crop: m[2], Cells: cells, Buildings: buildings, Urgent: m[5] == "true"})
			current = &out[len(out)-1]
			continue
		}
		if current == nil {
			continue
		}
		m := candidateLine.FindStringSubmatch(line)
		if m == nil {
			current = nil
			continue
		}
		needed, _ := strconv.Atoi(m[3])
		cells, _ := strconv.Atoi(m[4])
		score, err := strconv.ParseFloat(m[5], 64)
		if err != nil {
			return nil, fmt.Errorf("candidate score: %w", err)
		}
		c := Candidate{Kind: m[1], Crop: m[2], Needed: needed, Cells: cells, Score: score}
		var reason []string
		for _, token := range strings.Fields(m[6]) {
			if t := termToken.FindStringSubmatch(token); t != nil && len(reason) == 0 {
				v, err := strconv.ParseFloat(t[2], 64)
				if err != nil {
					return nil, fmt.Errorf("candidate term: %w", err)
				}
				if c.Terms == nil {
					c.Terms = map[string]float64{}
				}
				c.Terms[t[1]] = v
				continue
			}
			reason = append(reason, token)
		}
		c.Reason = strings.Join(reason, " ")
		current.Candidates = append(current.Candidates, c)
	}
	return out, scanner.Err()
}

// Expectation is what a run must show in every selection.
type Expectation struct {
	Kind     string
	Crop     string
	MinCells int
}

// Check asserts every selection chose the expected kind (and crop when set),
// that the winning candidate carries a score breakdown, and that every
// unplantable candidate states its reason. It returns the last selection
// as evidence.
func Check(selections []Selection, want Expectation) (Selection, error) {
	if len(selections) == 0 {
		return Selection{}, errors.New("no field selection was traced; is RIMGOVERNOR_CLOCK_DEBUG set and the field family on?")
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
		for _, c := range s.Candidates {
			if c.Cells == 0 && c.Reason == "" {
				return s, fmt.Errorf("selection %d candidate %s %s is unplantable without a reason", i, c.Kind, c.Crop)
			}
		}
	}
	return selections[len(selections)-1], nil
}
