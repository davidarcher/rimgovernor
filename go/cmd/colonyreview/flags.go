package main

import "fmt"

// Flag is one thing in an hour a reviewer should look at.
type Flag struct {
	Severity string // "bad" or "warn"
	Text     string
}

// Thresholds a flag fires on. They point a reviewer at an hour; they are
// not pass/fail gates.
const (
	lowMood     = 0.25 // near a minor break
	lowFoodDays = 2.0
	stuckHours  = 12 // a concern in deficit this long
	// forbiddenHours is how long starting supplies may stay forbidden: the
	// playtest that found them still forbidden was a day and a half in (#1581).
	forbiddenHours = 24
)

// flags are the reviewer flags for the last row of rows; prev is the row
// before it, or nil.
func flags(rows []Row, prev *Row) []Flag {
	r := rows[len(rows)-1]
	c := r.Census
	var out []Flag
	add := func(sev, format string, a ...any) { out = append(out, Flag{sev, fmt.Sprintf(format, a...)}) }
	if c.Error != "" || c.Colonists == nil {
		return out // no readings this hour: the card says so, no colony fact to flag
	}
	if prev != nil && colonists(r) < colonists(*prev) {
		add("bad", "colonists fell %d → %d", colonists(*prev), colonists(r))
	}
	if c.FoodRunwayDays != nil && *c.FoodRunwayDays < lowFoodDays {
		add("bad", "food runway %.1f days", *c.FoodRunwayDays)
	}
	for _, p := range c.Pawns {
		if p.Downed != nil && *p.Downed {
			add("bad", "%s downed", p.Label)
		}
		if p.Mood != nil && *p.Mood < lowMood {
			add("warn", "%s mood %.0f%%", p.Label, *p.Mood*100)
		}
	}
	for _, g := range r.Concerns {
		if n := deficitRun(rows, g.ID); n == stuckHours {
			add("warn", "%s in deficit %d hours running (%s)", g.ID, n, g.Status)
		}
	}
	if n := forbiddenRun(rows); n == forbiddenHours {
		add("warn", "starting supplies still forbidden after %d hours", n)
	}
	return out
}

// forbiddenRun is how many readings, ending at the last, report starting
// supplies forbidden.
func forbiddenRun(rows []Row) int {
	n := 0
	for i := len(rows) - 1; i >= 0; i-- {
		if f := rows[i].Census.ForbiddenSupplies; f == nil || !*f {
			break
		}
		n++
	}
	return n
}

// deficitRun is how many rows, ending at the last, hold concern id in deficit.
func deficitRun(rows []Row, id string) int {
	n := 0
	for i := len(rows) - 1; i >= 0; i-- {
		found := false
		for _, g := range rows[i].Concerns {
			if g.ID == id && g.Need == "unmet" {
				found = true
			}
		}
		if !found {
			break
		}
		n++
	}
	return n
}
