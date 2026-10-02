package main

import (
	"fmt"
	"strings"
)

// Flag is one thing in an hour a reviewer should look at.
type Flag struct {
	Severity string // "bad" or "warn"
	Text     string
}

// Thresholds a flag fires on. They point a reviewer at an hour; they are
// not pass/fail gates.
const (
	lowMood      = 0.25 // a colonist near a minor break
	lowHealth    = 0.5
	lowFoodDays  = 2.0
	idleHours    = 6  // the same idle report this many hours running
	stuckHours   = 12 // blueprints present and the building count flat
	idleJobWords = "wandering|standing|idle|waiting"
)

// flags are the reviewer flags for the last row of rows; prev is the row
// before it, or nil.
func flags(rows []Row, prev *Row) []Flag {
	r := rows[len(rows)-1]
	var out []Flag
	add := func(sev, format string, a ...any) { out = append(out, Flag{sev, fmt.Sprintf(format, a...)}) }
	if prev != nil {
		if d := r.DeadColonists - prev.DeadColonists; d > 0 {
			add("bad", "%d colonist(s) died", d)
		}
		if r.ColonistCount < prev.ColonistCount && r.DeadColonists == prev.DeadColonists {
			add("warn", "colonist count fell %d → %d (left, kidnapped or off map)", prev.ColonistCount, r.ColonistCount)
		}
		if r.Buildings < prev.Buildings-2 {
			add("warn", "colony buildings fell %d → %d", prev.Buildings, r.Buildings)
		}
	}
	if r.ColonistCount > 0 && r.FoodDays < lowFoodDays {
		add("bad", "food %.1f days (%.0f nutrition for %d)", r.FoodDays, r.Nutrition, r.ColonistCount)
	}
	if r.Hostiles > 0 {
		add("warn", "%d hostile(s) on the map", r.Hostiles)
	}
	if r.Fires > 0 {
		add("bad", "%d fire(s)", r.Fires)
	}
	for _, c := range r.Colonists {
		switch {
		case c.Mental != "":
			add("bad", "%s: %s", c.Name, c.Mental)
		case c.Mood != nil && *c.Mood < lowMood:
			add("warn", "%s mood %.0f%%", c.Name, *c.Mood*100)
		}
		if c.Downed {
			add("bad", "%s downed", c.Name)
		} else if c.Health != nil && *c.Health < lowHealth {
			add("warn", "%s health %.0f%%", c.Name, *c.Health*100)
		}
		if n := idleRun(rows, c.Name); n == idleHours {
			add("warn", "%s idle %d hours running (%s)", c.Name, n, c.Job)
		}
	}
	if n := stuckRun(rows); n == stuckHours {
		add("warn", "%d blueprint(s) and no new building for %d hours", r.Blueprints, n)
	}
	return out
}

func idle(job string) bool {
	job = strings.ToLower(job)
	if job == "" {
		return true
	}
	for _, w := range strings.Split(idleJobWords, "|") {
		if strings.Contains(job, w) {
			return true
		}
	}
	return false
}

// idleRun is how many rows, ending at the last, find name idle.
func idleRun(rows []Row, name string) int {
	n := 0
	for i := len(rows) - 1; i >= 0; i-- {
		found := false
		for _, c := range rows[i].Colonists {
			if c.Name == name {
				found = idle(c.Job) && !c.Downed
			}
		}
		if !found {
			break
		}
		n++
	}
	return n
}

// stuckRun is how many rows, ending at the last, hold blueprints with the
// building count no higher than it was at the start of the run.
func stuckRun(rows []Row) int {
	last := rows[len(rows)-1]
	n := 0
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].Blueprints == 0 || rows[i].Buildings < last.Buildings {
			break
		}
		n++
	}
	return n
}
