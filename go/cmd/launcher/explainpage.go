package main

import "time"

// The Now tab's per-concern drill-down: ExplainView (explaintail.go) turned
// into the strings the page prints. Read-only; the page ranks and infers
// nothing.

// plannerLabels name the planners that file transitions under their own name
// because they serve no single concern (the plannerCatalog entries with an
// empty concern). A target in neither this table nor concernLabels prints as
// written.
var plannerLabels = map[string]string{
	"fields":              "Plan the fields",
	"supplies":            "Keep supplies allowed or forbidden",
	"cookingBills":        "Queue cooking orders",
	"preservationBills":   "Queue food preservation orders",
	"butcherBills":        "Queue butchering orders",
	"cookAheadBills":      "Cook ahead of need",
	"workshop":            "Set up the workshop",
	"hospital":            "Set up the hospital",
	"defense":             "Respond to a threat",
	"tend":                "Tend the wounded and sick",
	"rescue":              "Rescue downed colonists",
	"armory":              "Stock the armory",
	"moodRelief":          "Relieve colonists' moods",
	"recovery":            "Bring colonists back to health and work",
	"husbandry":           "Look after the animals",
	"prisonerInteraction": "Handle prisoner interactions",
	"populationCustody":   "Capture and keep prisoners",
	"populationJoiner":    "Welcome new colonists",
	"storage-shelves":     "Build storage shelves",
	"dialog":              "Answer dialogs",
	"trade":               "Trade with visitors",
}

// targetLabel is a timeline target's plain name: a planner's, a concern's, or
// the target as written.
func targetLabel(target string) string {
	if l, ok := plannerLabels[target]; ok {
		return l
	}
	return concernLabel(target)
}

// ExplainEntryView is one change in a concern's story, newest first on the page.
type ExplainEntryView struct {
	When   string // local clock time of the change
	Tick   string // game tick, "" when unknown
	Text   string // what the bot found
	Before string // what it was before, "" when unknown
	Held   string // how long that earlier state stood (game time), "" when unknown
	Method string // the method the bot had, "" when none
}

// ExplainConcernView is a row in the concern picker.
type ExplainConcernView struct {
	ID      string
	Label   string
	Current string
	When    string
}

// ExplainDetail is the selected concern's timeline, newest first.
type ExplainDetail struct {
	ID      string
	Label   string
	Current string
	Entries []ExplainEntryView
}

// ExplainPage is the drill-down: the targets with history, newest activity
// first, and the selected one's timeline. Selected is nil when the requested
// id has no history.
type ExplainPage struct {
	Available bool
	Notice    string // why there is nothing to show, when not Available
	Concerns  []ExplainConcernView
	Selected  *ExplainDetail
}

func explainPage(v ExplainView, selected string) ExplainPage {
	p := ExplainPage{Available: v.Available, Notice: v.Empty, Concerns: []ExplainConcernView{}}
	for _, c := range v.Concerns {
		last := c.Entries[len(c.Entries)-1]
		p.Concerns = append(p.Concerns, ExplainConcernView{ID: c.Concern, Label: targetLabel(c.Concern), Current: c.Current, When: wallClock(last.Wall)})
	}
	if tl := v.Timeline(selected); tl != nil {
		d := &ExplainDetail{ID: tl.Concern, Label: targetLabel(tl.Concern), Current: tl.Current, Entries: []ExplainEntryView{}}
		for i := len(tl.Entries) - 1; i >= 0; i-- {
			e := tl.Entries[i]
			ev := ExplainEntryView{When: wallClock(e.Wall), Text: e.Text, Before: e.Previous, Held: e.Held}
			if e.HasTick {
				ev.Tick = group(e.Tick)
			}
			if e.PreviousClear {
				ev.Before = "Nothing was holding it up."
			}
			if e.Method != "" {
				ev.Method = "Method: " + e.Method
				if e.Outcome == "admitted" {
					// An admit row can carry the previous review's method.
					ev.Method += " (possibly from the previous review)"
				}
			}
			d.Entries = append(d.Entries, ev)
		}
		p.Selected = d
	}
	return p
}

func wallClock(unix float64) string {
	return time.Unix(int64(unix), 0).Format("15:04:05")
}
