package policy

import (
	"fmt"
	"hash/fnv"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaxStatusKey is the native status row key limit (#1263).
const MaxStatusKey = 32

// StatusKey makes a row key native-valid (#1263): 1-32 printable ASCII.
// Other bytes become '_'; a longer key keeps its head and ends in '~' plus
// an FNV-32 hash of the original, so distinct long ids stay distinct.
func StatusKey(key string) string {
	b := []byte(key)
	for i, ch := range b {
		if ch < 0x21 || ch > 0x7e {
			b[i] = '_'
		}
	}
	if len(b) == 0 {
		return "_"
	}
	if len(b) <= MaxStatusKey {
		return string(b)
	}
	h := fnv.New32a()
	h.Write([]byte(key))
	return fmt.Sprintf("%s~%08x", b[:MaxStatusKey-9], h.Sum32())
}

// StatusSeverity colors a status strip row (#823).
type StatusSeverity uint8

const (
	StatusInfo StatusSeverity = iota + 1
	StatusWarning
	StatusCritical
)

// StatusRow is one line of the in-game status strip (#823): a stable key,
// one short ASCII line, a severity, an optional camera target and whether
// it shows only when the strip is expanded.
type StatusRow struct {
	Key      string
	Text     string
	Severity StatusSeverity
	Target   domain.Fact[domain.Cell]
	Detail   bool
}

// PanelAction is one button on the in-game status panel (#957): pressing it
// sends the controller a player request naming ID. ID is 1-64 printable
// ASCII characters; Label and Tip are short ASCII text.
type PanelAction struct {
	ID, Label, Tip string
}

// StatusInput is what the routine review already knows when it pushes the
// strip.
type StatusInput struct {
	Stage     *ColonyStageRecord
	Progress  []GoalProgress
	Colonists domain.Fact[int64]
	FoodDays  domain.Fact[float64]
	Wood      domain.Fact[int64]
	WoodFloor int64
	// Emergency names the needs that suspended the review's goals.
	Emergency []GoalID
	// Refusals are the live native refusals, newest first.
	Refusals []RefusalMarker
	// Pause is who stopped the clock and why (#847); zero while the clock
	// runs or only the governor's own window boundary stopped it.
	Pause ClockPause
	// Medicine is the usable medicine reserve and MedicineTarget its
	// target (ReviewMedicalReserve).
	Medicine       domain.Fact[int64]
	MedicineTarget int64
	// GoalCells are the cells the goals' active plans target (#847).
	GoalCells map[GoalID]domain.Cell
	// Outlook is the storyteller's population and capture outlook (#1033);
	// each unknown fact renders as "?".
	Outlook PopulationOutlook
	// Incidents are the open incidents (#1020); each gets a detail row.
	Incidents []domain.Incident
}

// populationRow is the headline population and capture outlook row
// (#1033) against the bot's own domain.PopulationTarget.
func populationRow(o PopulationOutlook) StatusRow {
	num := func(f domain.Fact[float64], format string, scale float64) string {
		if v, known := f.Value(); known {
			return fmt.Sprintf(format, v*scale)
		}
		return "?"
	}
	text := fmt.Sprintf("population %s/%d, intent %s, downed raiders die %s, unrecruitable %s",
		num(o.AdjustedPopulation, "%.1f", 1), domain.PopulationTarget,
		num(o.Intent, "%.2f", 1),
		num(o.DeathOnDownedChance, "%.0f%%", 100),
		num(o.UnrecruitableChance, "%.0f%%", 100))
	return StatusRow{Key: "population", Text: text, Severity: StatusInfo}
}

// ClockPause names who stopped the clock ("player", "letter", "hold",
// "governor") and why; Held marks a stop the player must clear or the
// colony is waiting out.
type ClockPause struct {
	By     string
	Reason string
	Held   bool
}

// RefusalMarker is one native refusal of a still-pending action with the
// cell it targeted (#823).
type RefusalMarker struct {
	Label string
	Cell  domain.Cell
	Tick  domain.Tick
}

// Food runway thresholds for the strip's food row, in days.
const (
	statusFoodWarnDays     = 5
	statusFoodCriticalDays = 2
)

// RefusalMarkerTTL is how long a refusal marker stays drawn (one game day).
const RefusalMarkerTTL domain.Tick = 60000

// StatusRows builds the strip rows: goal, pause, food, wood, medicine,
// population, emergency, refusal, then one detail row per active goal. Pure; same
// input, same rows.
func StatusRows(in StatusInput) []StatusRow {
	var rows []StatusRow
	var refusal domain.Fact[domain.Cell]
	if len(in.Refusals) > 0 {
		refusal = domain.Known(in.Refusals[0].Cell)
	}
	if top, ok := topGoal(in.Progress); ok {
		text := "goal " + string(top.Goal)
		if method := statusMethod(top); method != "" {
			text += ": " + method
		}
		if n, known := in.Colonists.Value(); known {
			text += fmt.Sprintf(" (%d pawns)", n)
		}
		severity := StatusInfo
		if top.Blocked.Actionable() {
			text += " - blocked: " + statusReason(top.Blocked)
			severity = StatusWarning
		} else if top.Blocked != "" {
			text += " - " + statusReason(top.Blocked)
		}
		rows = append(rows, StatusRow{Key: "goal", Text: text, Severity: severity, Target: goalCell(in.GoalCells, top.Goal)})
	} else if in.Stage != nil {
		rows = append(rows, StatusRow{Key: "goal", Text: "stage " + in.Stage.Stage.String(), Severity: StatusInfo})
	}
	if in.Pause.By != "" {
		severity, text := StatusInfo, "paused by "+in.Pause.By
		if in.Pause.Reason != "" {
			text += ": " + in.Pause.Reason
		}
		if in.Pause.Held {
			severity = StatusWarning
		}
		rows = append(rows, StatusRow{Key: "pause", Text: text, Severity: severity})
	}
	if days, known := in.FoodDays.Value(); known {
		severity := StatusInfo
		switch {
		case days < statusFoodCriticalDays:
			severity = StatusCritical
		case days < statusFoodWarnDays:
			severity = StatusWarning
		}
		rows = append(rows, StatusRow{Key: "food", Text: fmt.Sprintf("food %.1f days", days), Severity: severity})
	}
	if wood, known := in.Wood.Value(); known {
		severity, text := StatusInfo, fmt.Sprintf("wood %d", wood)
		if in.WoodFloor > 0 {
			text += fmt.Sprintf("/%d", in.WoodFloor)
			if wood < in.WoodFloor {
				severity = StatusWarning
			}
		}
		rows = append(rows, StatusRow{Key: "wood", Text: text, Severity: severity})
	}
	if stock, known := in.Medicine.Value(); known && in.MedicineTarget > 0 {
		severity := StatusInfo
		if stock < in.MedicineTarget {
			severity = StatusWarning
		}
		rows = append(rows, StatusRow{Key: "medicine", Text: fmt.Sprintf("medicine %d/%d", stock, in.MedicineTarget), Severity: severity})
	}
	rows = append(rows, populationRow(in.Outlook))
	if len(in.Emergency) > 0 {
		names := make([]string, len(in.Emergency))
		for i, id := range in.Emergency {
			names[i] = string(id)
		}
		rows = append(rows, StatusRow{Key: "emergency", Text: "EMERGENCY " + joinShort(names), Severity: StatusCritical})
	}
	if len(in.Refusals) > 0 {
		rows = append(rows, StatusRow{Key: "refusal", Text: "refused " + in.Refusals[0].Label, Severity: StatusWarning, Target: refusal})
	}
	// Detail rows, per domain: actionable blocked goals first (warning,
	// with the reason), then goals being worked, then open incidents, then
	// one dim row naming the goals held on purpose.
	return append(rows, detailRows(in)...)
}

// panelDomains are the status panel's sections in order (#1025). System
// goals are game plumbing and get no section; goals with no domain fall
// under "Other".
var panelDomains = []Domain{DomainFood, DomainShelter, DomainIndustry, DomainMilitary, DomainMedical, DomainPeople, DomainUpkeep, DomainUnknown}

// detailRows groups the expanded panel's rows under one heading row (key
// "domain.<name>") per domain that has any. Each row names its concept.
func detailRows(in StatusInput) []StatusRow {
	var rows []StatusRow
	for _, d := range panelDomains {
		var held []string
		var body []StatusRow
		for _, pass := range []int{0, 1} {
			for _, g := range in.Progress {
				if GoalDomain(g.Goal) != d {
					continue
				}
				actionable := g.Blocked.Actionable()
				if g.Blocked.Held() {
					if pass == 0 {
						held = append(held, string(g.Goal))
					}
					continue
				}
				if actionable != (pass == 0) {
					continue
				}
				text := string(g.Goal)
				if c := GoalConcept(g.Goal); c != ConceptUnknown {
					text = string(c) + " " + text
				}
				if method := statusMethod(g); method != "" {
					text += ": " + method
				}
				severity := StatusInfo
				if actionable {
					text += " - " + statusReason(g.Blocked)
					severity = StatusWarning
				}
				body = append(body, StatusRow{Key: "goal." + string(g.Goal), Text: text, Severity: severity, Target: goalCell(in.GoalCells, g.Goal), Detail: true})
			}
		}
		for _, inc := range in.Incidents {
			if inc.Closed || GoalDomain(inc.Kind) != d {
				continue
			}
			text := "Incident " + string(inc.Kind)
			if inc.Subject != "" {
				text += " " + string(inc.Subject)
			}
			body = append(body, StatusRow{Key: "incident." + string(inc.ID), Text: text, Severity: StatusInfo, Target: goalCell(in.GoalCells, inc.Kind), Detail: true})
		}
		name := string(d)
		if d == DomainUnknown {
			name = "Other"
		}
		if len(held) > 0 {
			body = append(body, StatusRow{Key: "goal.held." + name, Text: "held " + joinShort(held), Severity: StatusInfo, Detail: true})
		}
		if len(body) > 0 {
			rows = append(rows, StatusRow{Key: "domain." + name, Text: name, Severity: StatusInfo, Detail: true})
			rows = append(rows, body...)
		}
	}
	return rows
}

// statusMethod is the method a strip row names: the "assess" placeholder
// (no committed method) only once the goal's planner has run and said why.
func statusMethod(g GoalProgress) string {
	if g.Method == "assess" {
		return ""
	}
	return g.Method
}

// statusReason words a blocked reason for the player: the raw codes
// ("no_method", "planner:unknown_prerequisite") mean nothing on screen.
func statusReason(b BlockedReason) string {
	switch b {
	case BlockedNoMethod:
		return "nothing to do right now"
	case BlockedNoWorker:
		return "no colonist free to do it"
	case BlockedNativeIneligible:
		return "the game refuses the order"
	case BlockedReconciling:
		return "confirming the last change"
	case BlockedCooldown:
		return "retrying later"
	}
	raw := string(b)
	for prefix, lead := range map[string]string{blockedPlanner: "can't plan yet: ", blockedPrerequisite: "waiting on ", blockedHeld: "on hold: "} {
		if rest, ok := strings.CutPrefix(raw, prefix); ok {
			return lead + strings.ReplaceAll(rest, "_", " ")
		}
	}
	return strings.ReplaceAll(raw, "_", " ")
}

// goalCell is goal's target cell, unknown when its plans name none.
func goalCell(cells map[GoalID]domain.Cell, goal GoalID) domain.Fact[domain.Cell] {
	if cell, ok := cells[goal]; ok {
		return domain.Known(cell)
	}
	return domain.Fact[domain.Cell]{}
}

// topGoal is the goal being worked: the first unblocked progress record,
// else the first actionable blocked one, else the first.
func topGoal(progress []GoalProgress) (GoalProgress, bool) {
	for _, g := range progress {
		if g.Blocked == "" {
			return g, true
		}
	}
	for _, g := range progress {
		if g.Blocked.Actionable() {
			return g, true
		}
	}
	if len(progress) > 0 {
		return progress[0], true
	}
	return GoalProgress{}, false
}

func joinShort(names []string) string {
	out := ""
	for i, n := range names {
		if i == 3 {
			return out + fmt.Sprintf(" +%d", len(names)-3)
		}
		if i > 0 {
			out += ", "
		}
		out += n
	}
	return out
}

// LiveRefusals drops markers older than RefusalMarkerTTL at tick and orders
// the rest newest first.
func LiveRefusals(markers []RefusalMarker, tick domain.Tick) []RefusalMarker {
	out := make([]RefusalMarker, 0, len(markers))
	for _, m := range markers {
		if tick-m.Tick < RefusalMarkerTTL {
			out = append(out, m)
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Tick > out[j].Tick })
	return out
}

// refusalHue is the refusal markers' red.
var refusalHue = overlayHue{0.9, 0.15, 0.15}

// RefusalOverlay outlines each live refusal's cell in red with its reason
// as a label (#823).
func RefusalOverlay(markers []RefusalMarker, tick domain.Tick) LayoutOverlay {
	var out LayoutOverlay
	for _, m := range LiveRefusals(markers, tick) {
		out.Layers = append(out.Layers, OverlayLayer{Color: refusalHue.outline(), Style: OverlayOutline, Label: m.Label, Rects: []Rectangle{{X: m.Cell.X, Z: m.Cell.Z, Width: 1, Height: 1}}})
		out.Labels = append(out.Labels, OverlayLabel{Text: m.Label, Cell: m.Cell})
	}
	return out
}
