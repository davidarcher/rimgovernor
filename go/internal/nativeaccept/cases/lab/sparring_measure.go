package lab

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The sparring numbers (#2711): the stop rule's limits, the practice weapons'
// powers and the practice apparel's armor are tuned from what this case measures
// over many bouts, not guessed. The fixture runs in measure mode: it keeps the
// bouts going, tallies what every ended spar session left (injuries, destroyed
// parts, scars, gear wear, nakedness, carried mass, vanilla XP) and makes the pawn
// unhurt again the same tick, so each bout is an independent sample. The report
// carries every number; the assertions are the epic's stakes (no deaths, no
// destroyed vital or protected part, nobody naked, real armor untouched).

func init() {
	register := func(name, scope string, colonists int, run func(context.Context, cases.Session) error) {
		cases.Register(cases.Case{
			Name: name, Scope: scope + " A Go snapshot test cannot see the verb's hits, the heal, the vanilla XP or the formation over a running ring.",
			Start: cases.Lab{Colonists: colonists}, RequiredOps: sparringOps, QuietWorld: true, Budget: 15 * time.Minute, Crew: cases.Crew{Size: 3}, Run: run,
		})
	}
	register("lab/sparring-risk", "The sparring risk measured over many bouts (#2711), per practice tier and outfit (a tribal garment and spacer recon armor): zero deaths, "+
		"no destroyed head, neck or torso, no lost eye, ear, nose or finger, bruises at tier 0 and cuts at the sharp tiers, scars counted by a simulated heal, practice gear wear, "+
		"real armor never worn, nobody naked after the swap, carried mass under capacity, and a Royalty title requirement that does not pull a fighter out of its job.", 8, runSparringMeasure)
	register("lab/sparring-xp", "Vanilla melee XP per swing and the day's saturation (xpSinceMidnight, the 0.2 factor) over a day of bouts (#2711).", 3, runSparringXP)
	register("lab/sparring-scale", "Bout formation for 2 to 12 eligible colonists (every one seated, no double booking, opponents rotating), the partner shortage at levels 16 to 20, "+
		"and a hostile attacker mid-bout (#2711).", 12, runSparringScale)
}

// sparringSessions is how many ended sessions a risk stage tallies. A death or a
// destroyed protected part must not appear in this many; the report gives the
// observed rates, which are the tuning data.
const sparringSessions = 100

type measureStage struct {
	name     string
	tier     int
	outfit   string
	levels   string
	eligible int
	sessions int
	title    bool
	xpDay    bool
}

func runSparringMeasure(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	tierLevels := []string{"7,6,5,4,3,2,5,4", "11,9,7,5,4,3,8,6", "15,12,9,7,5,4,10,8", "19,17,14,10,7,5,12,9"}
	var stages []measureStage
	// Tiers only unlock, so they run in ascending order.
	for tier := 0; tier < 4; tier++ {
		for _, outfit := range []string{"tribal", "spacer"} {
			stages = append(stages, measureStage{name: fmt.Sprintf("risk-t%d-%s", tier, outfit), tier: tier, outfit: outfit,
				levels: tierLevels[tier], eligible: 8, sessions: sparringSessions, title: tier == 1 && outfit == "spacer"})
		}
	}
	report := map[string]any{}
	defer func() { s.Report()["sparring-risk"] = report }()
	// Every stage runs even after a failed check: the numbers of all of them are the tuning data.
	var first error
	for _, st := range stages {
		got, err := runMeasureStage(ctx, s, h, st)
		report[st.name] = summarizeMeasure(got)
		if err != nil {
			return fmt.Errorf("%s: %w", st.name, err)
		}
		if err := checkMeasureRisk(st, got); err != nil && first == nil {
			first = fmt.Errorf("%s: %w", st.name, err)
		}
	}
	return first
}

// runSparringXP measures vanilla melee XP per swing and the day's saturation.
func runSparringXP(ctx context.Context, s cases.Session) error {
	report := map[string]any{}
	defer func() { s.Report()["sparring-xp"] = report }()
	st := measureStage{name: "xp-day", tier: 3, outfit: "tribal", levels: "10,10", eligible: 2, sessions: 12, xpDay: true}
	got, err := runMeasureStage(ctx, s, s.Harness(), st)
	report[st.name] = summarizeMeasure(got)
	report[st.name+"-xp"] = xpDaySummary(got)
	return err
}

func runSparringScale(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	report := map[string]any{}
	defer func() { s.Report()["sparring-scale"] = report }()
	// Formation at scale and the partner shortage, at the top tier (ceiling 20).
	scale := []measureStage{}
	for _, n := range []int{2, 3, 4, 5, 6, 8, 10, 12} {
		scale = append(scale, measureStage{name: fmt.Sprintf("scale-%d", n), tier: 3, levels: "9,7,5,4,3,2,12,10,8,6,11,13", eligible: n, sessions: 2 * n})
	}
	scale = append(scale,
		measureStage{name: "shortage-4-high-2-low", tier: 3, levels: "19,18,17,16,3,2", eligible: 6, sessions: 12},
		measureStage{name: "shortage-1-high-2-low", tier: 3, levels: "19,3,2", eligible: 3, sessions: 6},
		measureStage{name: "shortage-pair", tier: 3, levels: "19,3", eligible: 2, sessions: 4},
		measureStage{name: "shortage-all-high", tier: 3, levels: "19,18,17,16", eligible: 4, sessions: 8})
	var failures []error
	for _, st := range scale {
		got, err := runMeasureStage(ctx, s, h, st)
		report[st.name] = summarizeFormation(got)
		if err != nil {
			return fmt.Errorf("%s: %w", st.name, err)
		}
		if err := checkMeasureFormation(st, got); err != nil {
			failures = append(failures, fmt.Errorf("%s: %w", st.name, err))
		}
	}
	// A real attacker mid-bout: the spar job's override mode is Never, so what the
	// struck pawn does is measured, not assumed.
	raid := sparringStage{name: "fight-raid", eligible: 3, levels: "6,5,4", sizes: []int{3}, scenario: "raid"}
	got, err := runSparringStage(ctx, s, h, raid)
	report[raid.name] = map[string]any{"raid": got["raid"], "abandoned": got["abandoned"], "pawns": got["pawns"]}
	if err != nil {
		failures = append(failures, fmt.Errorf("%s: %w", raid.name, err))
	}
	return errors.Join(failures...)
}

func runMeasureStage(ctx context.Context, s cases.Session, h *na.Harness, st measureStage) (map[string]any, error) {
	args := map[string]any{
		"eligible": st.eligible, "levels": st.levels, "markers": maxInt(st.eligible, 6), "tier": st.tier, "outfit": st.outfit,
		"measure": true, "xpDay": st.xpDay, "title": st.title,
	}
	if _, err := prepareSparring(ctx, s, h, args); err != nil {
		return nil, err
	}
	var last map[string]any
	_, err := na.RunUntil(ctx, h, "sparring-"+st.name, 1200000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "sparring-inspect", "test/sparring_inspect", map[string]any{})
		if err != nil {
			return "", false, err
		}
		last = got
		n := tallyNumber(got, "sessions")
		return fmt.Sprint(n, got["active"]), int(n) >= st.sessions, nil
	})
	return last, err
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func tallyNumber(got map[string]any, key string) float64 {
	tally, _ := na.AsMap(got["tally"])
	n, _ := na.AsMap(tally["n"])
	return max(0, na.AsNumber(n[key]))
}

func tallyHist(got map[string]any, name string) map[string]int {
	tally, _ := na.AsMap(got["tally"])
	hist, _ := na.AsMap(tally["hist"])
	row, _ := na.AsMap(hist[name])
	out := map[string]int{}
	for k, v := range row {
		out[k] = int(na.AsNumber(v))
	}
	return out
}

func summarizeMeasure(got map[string]any) map[string]any {
	tally, _ := na.AsMap(got["tally"])
	out := map[string]any{"n": tally["n"], "hist": tally["hist"], "bouts": len(sparringBouts(got)), "abandoned": got["abandoned"], "attackJobs": got["attackJobs"]}
	return out
}

// checkMeasureRisk asserts the stakes and leaves the rates to the report.
func checkMeasureRisk(st measureStage, got map[string]any) error {
	n := func(key string) float64 { return tallyNumber(got, key) }
	if n("sessions") < float64(st.sessions) {
		return fmt.Errorf("%v sessions, want %d: %#v", n("sessions"), st.sessions, got)
	}
	if n("deaths") != 0 {
		return fmt.Errorf("%v deaths in %v sessions: %#v", n("deaths"), n("sessions"), got["tally"])
	}
	protected := map[string]bool{"Head": true, "Neck": true, "Torso": true, "Skull": true, "Brain": true}
	if st.tier >= 1 {
		for _, part := range []string{"Eye", "Ear", "Nose", "Jaw", "Finger", "Hand"} {
			protected[part] = true
		}
	}
	for part, count := range tallyHist(got, "destroyedPart") {
		if protected[part] {
			return fmt.Errorf("%d %s destroyed in %v sessions: %#v", count, part, n("sessions"), got["tally"])
		}
	}
	injuries := tallyHist(got, "injury")
	if st.tier == 0 && injuries["Cut"] > 0 {
		return fmt.Errorf("the blunt club cut %d times: %#v", injuries["Cut"], got["tally"])
	}
	if st.tier == 0 && injuries["Bruise"] == 0 {
		return fmt.Errorf("no bruise in %v sessions at tier 0: %#v", n("sessions"), got["tally"])
	}
	if st.tier >= 1 && injuries["Cut"] == 0 {
		return fmt.Errorf("no cut in %v sessions at tier %d: %#v", n("sessions"), st.tier, got["tally"])
	}
	switch {
	case n("nudeTicks") != 0:
		return fmt.Errorf("a pawn was naked for %v ticks wearing the practice set: %#v", n("nudeTicks"), got["tally"])
	case n("realArmorLost") != 0:
		return fmt.Errorf("real armor lost %v (worn %v): %#v", n("realArmorLost"), n("realArmorWorn"), got["tally"])
	case n("maxMassFraction") >= 1:
		return fmt.Errorf("carried mass reached %.2f of capacity: %#v", n("maxMassFraction"), got["tally"])
	case n("swapped") == 0:
		return fmt.Errorf("no session swapped into practice gear: %#v", got["tally"])
	}
	if a := na.AsNumber(got["abandoned"]); a != 0 {
		return fmt.Errorf("a fighter dropped its spar job %v times: %#v", a, got)
	}
	return nil
}

// xpDaySummary reads the trace (pawn index | exchanges | xp | xpSinceMidnight after) of
// one pawn: the XP a swing paid before and after the day's saturation.
func xpDaySummary(got map[string]any) map[string]any {
	tally, _ := na.AsMap(got["tally"])
	type row struct {
		exchanges int
		xp, mid   float64
	}
	by := map[int][]row{}
	for _, raw := range na.AsSlice(tally["trace"]) {
		var idx, exchanges int
		var xp, mid float64
		if _, err := fmt.Sscanf(na.AsString(raw), "%d|%d|%f|%f", &idx, &exchanges, &xp, &mid); err == nil {
			by[idx] = append(by[idx], row{exchanges, xp, mid})
		}
	}
	out := map[string]any{}
	var idxs []int
	for idx := range by {
		idxs = append(idxs, idx)
	}
	sort.Ints(idxs)
	for _, idx := range idxs {
		var swings int
		var firstXP float64
		for i, r := range by[idx] {
			if i == 0 && r.exchanges > 0 {
				firstXP = r.xp / float64(r.exchanges)
			}
			swings += r.exchanges
		}
		var rows []map[string]any
		for _, r := range by[idx] {
			rows = append(rows, map[string]any{"exchanges": r.exchanges, "xp": r.xp, "xpSinceMidnight": r.mid})
		}
		out[fmt.Sprint("pawn", idx)] = map[string]any{"sessions": rows, "swings": swings, "firstSessionXpPerSwing": firstXP}
	}
	return out
}

func summarizeFormation(got map[string]any) map[string]any {
	var bouts []map[string]any
	for _, b := range sparringBouts(got) {
		if b.fighting == nil {
			continue
		}
		lo, hi := b.fighting.melee[0], b.fighting.melee[0]
		for _, m := range b.fighting.melee {
			lo, hi = min(lo, m), max(hi, m)
		}
		bouts = append(bouts, map[string]any{"id": b.id, "members": b.fighting.members, "melee": b.fighting.melee, "teams": b.fighting.teams, "gap": hi - lo})
	}
	return map[string]any{"bouts": bouts, "seated": got["seated"], "doubleBooked": got["doubleBooked"], "peakActive": got["peakActive"], "sessions": tallyNumber(got, "sessions")}
}

// checkMeasureFormation: every eligible pawn is seated in some bout of 2 to 4, none
// twice at once, and from six pawns up the second round is a different grouping.
func checkMeasureFormation(st measureStage, got map[string]any) error {
	if n := na.AsNumber(got["doubleBooked"]); n != 0 {
		return fmt.Errorf("a pawn or marker was in two bouts %v times: %#v", n, got)
	}
	if seated := len(stringList(got["seated"])); seated < st.eligible {
		return fmt.Errorf("%d of %d eligible pawns were ever seated: %#v", seated, st.eligible, got)
	}
	var rounds [][][]string
	var round [][]string
	size := 0
	for _, b := range sparringBouts(got) {
		if b.fighting == nil {
			continue
		}
		if len(b.fighting.members) < 2 || len(b.fighting.members) > 4 {
			return fmt.Errorf("bout %d has %d fighters", b.id, len(b.fighting.members))
		}
		round = append(round, append([]string(nil), b.fighting.members...))
		size += len(b.fighting.members)
		if size >= st.eligible {
			rounds = append(rounds, round)
			round, size = nil, 0
		}
	}
	if len(rounds) == 0 {
		return fmt.Errorf("no complete first round: %#v", got)
	}
	// Rotation: some bout of the second round is a grouping the first did not have. The
	// shortage stages are exempt: with one high partner left the same trio is right.
	if st.eligible >= 6 && len(rounds) >= 2 && !strings.HasPrefix(st.name, "shortage") {
		fresh := false
		for _, b := range rounds[1] {
			repeat := false
			for _, a := range rounds[0] {
				repeat = repeat || sameSet(a, b)
			}
			fresh = fresh || !repeat
		}
		if !fresh {
			return fmt.Errorf("the second round only repeated first-round bouts: %#v", got)
		}
	}
	return nil
}

func sameSet(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for _, x := range a {
		if !sparringHas(b, x) {
			return false
		}
	}
	return true
}
