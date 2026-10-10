package lab

import (
	"context"
	"fmt"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// Bout formation (#2708) and the spar job (#2709): who spars with whom at the
// ring, and what the fight does to the gear. The bout registry, matchmaking, the
// work giver and the job driver are native; the fixture's watcher samples the
// registry every tick (the game runs thousands of ticks between two reads) and
// records what each spar job left behind the tick it ended, so the case asserts
// what the watcher saw, not a poll.

var sparringOps = []string{"test/sparring_prepare", "test/sparring_inspect"}

func init() {
	cases.Register(cases.Case{
		Name: "lab/sparring",
		Scope: "Bout formation at the sparring ring (#2708): with no order from Go the vanilla work scan offers eligible colonists a marker, and " +
			"2, 3, 4 and 5 eligible colonists form bouts of 2; 3 (free-for-all); 4 (2v2, highest with lowest); and 3 + 2 at once. Every pawn and every " +
			"marker is in at most one bout, a colonist above the ceiling, a drafted one and one whose chosen skill is Shooting are never seated, a pawn drafted while its bout gathers is " +
			"dropped and the rest fight on, and every fighter's opponent is a member of another team. The bouts then run end to end (#2709): each " +
			"fighter swaps at its marker into the practice weapon and apparel set (its own gear held in its inventory), swings through the vanilla " +
			"melee verb and is paid vanilla melee XP, the struck pawn neither flees nor fights back nor drops its job, and the gear is fully restored " +
			"after a normal end (the exchange cap), a pawn leaving for pain or bleeding while the rest fight on, a pawn drafted mid-bout and a pawn " +
			"killed mid-bout. Every member whose session completed holds the shared trained-with thought (#2710); a pawn drafted or killed mid-bout does not. A Go snapshot test cannot see the work giver, the registry, the walk, the swap or the vanilla verb.",
		Start:       cases.Lab{Colonists: 8},
		RequiredOps: sparringOps,
		QuietWorld:  true,
		Budget:      12 * time.Minute,
		Crew:        cases.Crew{Size: 3},
		Run:         runSparring,
	})
}

type sparringStage struct {
	name     string
	eligible int
	levels   string
	// sizes are the first round's bout sizes, largest first.
	sizes      []int
	ineligible bool
	// shooter adds a trainee whose chosen skill is Shooting (level above its
	// Melee): it must never be seated while the melee-chosen ones are.
	shooter bool
	draft   bool
	// scenario names a fight stage (#2709): the fixture's mid-bout event, and
	// the stage waits for every spar job to end instead of the bouts to finish.
	scenario string
}

func runSparring(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	stages := []sparringStage{
		{name: "pair", eligible: 2, levels: "5,3", sizes: []int{2}},
		{name: "three", eligible: 3, levels: "6,4,2", sizes: []int{3}},
		{name: "four", eligible: 4, levels: "7,5,4,2", sizes: []int{4}},
		{name: "five-two-bouts", eligible: 5, levels: "7,6,5,4,3", sizes: []int{3, 2}},
		{name: "ineligible", eligible: 2, levels: "5,4", sizes: []int{2}, ineligible: true},
		{name: "shooter-never-seated", eligible: 3, levels: "6,4,2", sizes: []int{3}, shooter: true},
		{name: "drafted-while-gathering", eligible: 4, levels: "7,5,4,2", sizes: []int{3}, draft: true},
		{name: "fight-cap", eligible: 2, levels: "5,4", sizes: []int{2}, scenario: "cap"},
		{name: "fight-pain", eligible: 3, levels: "6,5,4", sizes: []int{3}, scenario: "pain"},
		{name: "fight-bleed", eligible: 2, levels: "5,4", sizes: []int{2}, scenario: "bleed"},
		{name: "fight-draft", eligible: 3, levels: "6,5,4", sizes: []int{3}, scenario: "draft"},
		{name: "fight-kill", eligible: 3, levels: "6,5,4", sizes: []int{3}, scenario: "kill"},
	}
	// A freshly loaded world has no bouts: the registry is runtime only.
	fresh, err := h.Call(ctx, "sparring-inspect-fresh", "test/sparring_inspect", map[string]any{})
	if err != nil {
		return err
	}
	if na.AsNumber(fresh["active"]) != 0 {
		return fmt.Errorf("registry not empty on a fresh world: %#v", fresh)
	}
	report := map[string]any{}
	for _, stage := range stages {
		got, err := runSparringStage(ctx, s, h, stage)
		report[stage.name] = got
		if err != nil {
			s.Report()["sparring"] = report
			return fmt.Errorf("%s: %w", stage.name, err)
		}
	}
	s.Report()["sparring"] = report
	return nil
}

func prepareSparring(ctx context.Context, s cases.Session, h *na.Harness, args map[string]any) (map[string]any, error) {
	center, _ := na.AsMap(s.Prepared()["center"])
	args["x"], args["z"] = int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
	prepared, err := h.Call(ctx, "sparring-prepare", "test/sparring_prepare", args)
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return nil, fmt.Errorf("sparring_prepare: %#v", prepared)
	}
	return prepared, nil
}

func runSparringStage(ctx context.Context, s cases.Session, h *na.Harness, stage sparringStage) (map[string]any, error) {
	prepared, err := prepareSparring(ctx, s, h, map[string]any{
		"eligible": stage.eligible, "levels": stage.levels, "ineligible": stage.ineligible, "shooter": stage.shooter, "draftOnGather": stage.draft,
		"scenario": stage.scenario,
	})
	if err != nil {
		return nil, err
	}
	var last map[string]any
	if _, err := na.RunUntil(ctx, h, "sparring-"+stage.name, 20000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "sparring-inspect", "test/sparring_inspect", map[string]any{})
		if err != nil {
			return "", false, err
		}
		last = got
		if stage.scenario != "" {
			ended := 0
			for _, p := range sparringPawns(got) {
				if p.ended {
					ended++
				}
			}
			done := ended >= stage.eligible
			if stage.scenario == "raid" {
				// The attacker is removed a few hundred ticks after the victim's job ends.
				raid, _ := na.AsMap(got["raid"])
				gone, _ := na.AsBool(raid["raiderGone"])
				done = done && gone
			}
			return fmt.Sprint(got["active"], ended), done, nil
		}
		done := 0
		for _, b := range sparringBouts(got) {
			if b.fighting != nil && b.finished {
				done++
			}
		}
		return fmt.Sprint(got["active"], done), done >= len(stage.sizes), nil
	}); err != nil {
		return last, err
	}
	if err := checkSparringStage(stage, prepared, last); err != nil {
		return last, err
	}
	if stage.scenario == "raid" {
		return last, checkSparringRaid(last)
	}
	if stage.scenario != "" {
		return last, checkSparringFight(stage, last)
	}
	return last, nil
}

// checkSparringRaid: a real attacker beside a sparring pawn (#2711). The victim's
// spar job ends (the stop rule, or it is downed) and its own gear is back; the
// numbers it saw go to the report.
func checkSparringRaid(got map[string]any) error {
	raid, _ := na.AsMap(got["raid"])
	if raid == nil {
		return fmt.Errorf("no attacker was ever spawned: %#v", got)
	}
	victim := na.AsString(raid["victim"])
	for _, p := range sparringPawns(got) {
		switch {
		case !p.ended:
			return fmt.Errorf("%s's spar job never ended: %#v", p.id, got)
		case !p.noPractice || !p.originals:
			return fmt.Errorf("%s left practice gear behind or lost a piece: %#v", p.id, got)
		case !p.dead && !p.restored:
			return fmt.Errorf("%s's gear is not restored: %#v", p.id, got)
		case p.id == victim && p.dead:
			return fmt.Errorf("the attacker killed %s: %#v", p.id, got)
		}
	}
	return nil
}

type sparringPawn struct {
	id         string
	ended      bool
	stop       string
	exchanges  int
	swapped    bool
	swapSeen   bool
	atMarker   bool
	held       bool
	dead       bool
	drafted    bool
	restored   bool
	noPractice bool
	originals  bool
	threatNull bool
	xp         bool
	trained    bool
}

func sparringPawns(got map[string]any) []sparringPawn {
	var out []sparringPawn
	for _, raw := range na.AsSlice(got["pawns"]) {
		row, _ := na.AsMap(raw)
		flag := func(key string) bool { v, _ := na.AsBool(row[key]); return v }
		out = append(out, sparringPawn{
			id: na.AsString(row["id"]), ended: flag("ended"), stop: na.AsString(row["stop"]), exchanges: int(na.AsNumber(row["exchanges"])),
			swapped: flag("swapped"), swapSeen: flag("swapSeen"), atMarker: flag("swapAtMarker"), held: flag("originalsHeld"),
			dead: flag("dead"), drafted: flag("drafted"), restored: flag("restored"), noPractice: flag("noPractice"),
			originals: flag("originals"), threatNull: flag("threatNull"), xp: flag("xp"), trained: flag("trainedWith"),
		})
	}
	return out
}

// checkSparringFight asserts what the spar job did in a fight stage: the swap at
// the marker, the gear after the job ended, vanilla XP, and the scenario's own
// claim. A pawn that never spars (the stage's eligible set is all of them) fails.
func checkSparringFight(stage sparringStage, got map[string]any) error {
	pawns := sparringPawns(got)
	if len(pawns) != stage.eligible {
		return fmt.Errorf("%d pawns tracked, want %d: %#v", len(pawns), stage.eligible, got)
	}
	if n := na.AsNumber(got["abandoned"]); n != 0 {
		return fmt.Errorf("a fighter dropped its spar job %v times (the struck pawn must not flee or abandon): %#v", n, got)
	}
	if n := na.AsNumber(got["attackJobs"]); n != 0 {
		return fmt.Errorf("a fighter took a real AttackMelee job %v times (meleeThreat reaction): %#v", n, got)
	}
	injured, hit := na.AsString(got["injected"]), na.AsString(got["hit"])
	for _, p := range pawns {
		killed := stage.scenario == "kill" && p.id == hit
		switch {
		case p.dead != killed:
			return fmt.Errorf("%s dead=%v, want %v: %#v", p.id, p.dead, killed, got)
		case !p.swapped || !p.swapSeen:
			return fmt.Errorf("%s never swapped into practice gear: %#v", p.id, got)
		case !p.atMarker:
			return fmt.Errorf("%s did not swap at its marker: %#v", p.id, got)
		case !p.held:
			return fmt.Errorf("%s's own weapon and apparel were not held in its inventory while it wore practice gear: %#v", p.id, got)
		case !p.noPractice:
			return fmt.Errorf("%s left practice gear behind: %#v", p.id, got)
		case !p.originals:
			return fmt.Errorf("%s lost an original weapon or apparel piece: %#v", p.id, got)
		case !killed && !p.restored:
			return fmt.Errorf("%s's gear is not restored: %#v", p.id, got)
		case !p.threatNull:
			return fmt.Errorf("%s still had a meleeThreat when its job ended: %#v", p.id, got)
		case p.exchanges > 10:
			return fmt.Errorf("%s made %d exchanges, past the cap: %#v", p.id, p.exchanges, got)
		case p.exchanges > 0 && !p.xp:
			return fmt.Errorf("%s swung %d times and was paid no melee XP: %#v", p.id, p.exchanges, got)
		// #2710: a bout member whose session was completed (cap, stop rule, bout end)
		// holds the trained-with thought; one drafted or killed mid-bout does not.
		case p.trained != (!killed && !(stage.scenario == "draft" && p.id == hit)):
			return fmt.Errorf("%s trained-with thought is %v after a %q stage: %#v", p.id, p.trained, stage.scenario, got)
		}
	}
	by := map[string]sparringPawn{}
	for _, p := range pawns {
		by[p.id] = p
	}
	switch stage.scenario {
	case "cap":
		capped := 0
		for _, p := range pawns {
			if p.stop == "Pain" || p.stop == "Bleeding" {
				return fmt.Errorf("%s stopped for %s with no pain possible: %#v", p.id, p.stop, got)
			}
			if p.stop == "Exchanges" && p.exchanges == 10 {
				capped++
			}
		}
		if capped == 0 {
			return fmt.Errorf("no pawn ended at the 10-exchange cap: %#v", got)
		}
		if na.AsNumber(got["struck"]) == 0 {
			return fmt.Errorf("no blow landed, so the struck pawn was never tested: %#v", got)
		}
	case "pain", "bleed":
		want := map[string]string{"pain": "Pain", "bleed": "Bleeding"}[stage.scenario]
		first, ok := by[injured]
		if !ok || first.stop != want {
			return fmt.Errorf("injured %q did not leave for %s: %#v", injured, want, got)
		}
		swings := 0
		for _, p := range pawns {
			if p.id != injured {
				swings += p.exchanges
			}
		}
		if stage.eligible > 2 && swings == 0 {
			return fmt.Errorf("the rest of the bout did not fight on after %s left: %#v", injured, got)
		}
	case "draft", "kill":
		victim, ok := by[hit]
		if !ok || victim.exchanges < 2 {
			return fmt.Errorf("%q was not hit mid-bout after swinging: %#v", hit, got)
		}
		if stage.scenario == "draft" && !victim.drafted {
			return fmt.Errorf("%s was not drafted: %#v", hit, got)
		}
		swings := 0
		for _, p := range pawns {
			if p.id != hit {
				swings += p.exchanges
			}
		}
		if swings <= victim.exchanges {
			return fmt.Errorf("the rest of the bout did not fight on after %s left: %#v", hit, got)
		}
	}
	return nil
}

type sparringBout struct {
	id       int
	finished bool
	formed   []string
	fighting *sparringFight
}

type sparringFight struct {
	members   []string
	teams     []int
	melee     []int
	opponents []string
	markers   []string
}

func stringList(v any) []string {
	var out []string
	for _, e := range na.AsSlice(v) {
		out = append(out, na.AsString(e))
	}
	return out
}

func intList(v any) []int {
	var out []int
	for _, e := range na.AsSlice(v) {
		out = append(out, int(na.AsNumber(e)))
	}
	return out
}

func sparringBouts(got map[string]any) []sparringBout {
	var out []sparringBout
	for _, raw := range na.AsSlice(got["bouts"]) {
		row, _ := na.AsMap(raw)
		b := sparringBout{id: int(na.AsNumber(row["id"])), formed: stringList(row["formed"])}
		b.finished, _ = na.AsBool(row["finished"])
		if f, ok := na.AsMap(row["fighting"]); ok && f != nil {
			b.fighting = &sparringFight{members: stringList(f["members"]), teams: intList(f["teams"]), melee: intList(f["melee"]),
				opponents: stringList(f["opponents"]), markers: stringList(f["markers"])}
		}
		out = append(out, b)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].id < out[j].id })
	return out
}

func sparringHas(list []string, v string) bool {
	for _, e := range list {
		if e == v {
			return true
		}
	}
	return false
}

func checkSparringStage(stage sparringStage, prepared, got map[string]any) error {
	if n := na.AsNumber(got["doubleBooked"]); n != 0 {
		return fmt.Errorf("a pawn or marker was in two bouts at once %v times: %#v", n, got)
	}
	eligible := stringList(prepared["eligible"])
	if stage.shooter {
		shooters := stringList(prepared["shooter"])
		if len(shooters) != 1 {
			return fmt.Errorf("stage staged %d shooters, want 1: %#v", len(shooters), prepared)
		}
		for _, id := range stringList(got["seated"]) {
			if sparringHas(shooters, id) {
				return fmt.Errorf("shooting-chosen %s was seated in a bout: %#v", id, got)
			}
		}
		if len(stringList(got["seated"])) < stage.eligible {
			return fmt.Errorf("the melee-chosen pawns were not all seated: %#v", got)
		}
	}
	for _, id := range stringList(got["seated"]) {
		if !sparringHas(eligible, id) {
			return fmt.Errorf("%s was seated but is not eligible: %#v", id, got)
		}
	}
	bouts := sparringBouts(got)
	if len(bouts) < len(stage.sizes) {
		return fmt.Errorf("want %d bouts, saw %d: %#v", len(stage.sizes), len(bouts), got)
	}
	// The first round is the lowest ids; the pawns re-form after it.
	round := bouts[:len(stage.sizes)]
	var sizes []int
	drafted := na.AsString(got["drafted"])
	if stage.draft && drafted == "" {
		return fmt.Errorf("no bout was seen gathering to draft from: %#v", got)
	}
	if stage.draft && na.AsNumber(got["peakActive"]) < 1 {
		return fmt.Errorf("no bout formed: %#v", got)
	}
	var markers []string
	for _, b := range round {
		if b.fighting == nil {
			return fmt.Errorf("bout %d never fought: %#v", b.id, got)
		}
		f := b.fighting
		sizes = append(sizes, len(f.members))
		if stage.draft && sparringHas(f.members, drafted) {
			return fmt.Errorf("drafted %s still fought in bout %d: %#v", drafted, b.id, got)
		}
		if len(f.members) < 2 || len(f.members) > 4 {
			return fmt.Errorf("bout %d has %d fighters: %#v", b.id, len(f.members), got)
		}
		for _, m := range f.markers {
			if sparringHas(markers, m) {
				return fmt.Errorf("marker %s used by two bouts: %#v", m, got)
			}
			markers = append(markers, m)
		}
		if err := checkTeams(b, f); err != nil {
			return fmt.Errorf("%w: %#v", err, got)
		}
	}
	sort.Sort(sort.Reverse(sort.IntSlice(sizes)))
	if fmt.Sprint(sizes) != fmt.Sprint(stage.sizes) {
		return fmt.Errorf("first-round bout sizes %v, want %v: %#v", sizes, stage.sizes, got)
	}
	return nil
}

func checkTeams(b sparringBout, f *sparringFight) error {
	count := map[int]int{}
	for _, t := range f.teams {
		count[t]++
	}
	switch len(f.members) {
	case 2, 3:
		if len(count) != len(f.members) {
			return fmt.Errorf("bout %d of %d is not free-for-all, teams %v", b.id, len(f.members), f.teams)
		}
	case 4:
		if len(count) != 2 || count[0] != 2 || count[1] != 2 {
			return fmt.Errorf("bout %d of 4 is not 2v2, teams %v", b.id, f.teams)
		}
		hi, lo := 0, 0
		for i := range f.melee {
			if f.melee[i] > f.melee[hi] {
				hi = i
			}
			if f.melee[i] < f.melee[lo] {
				lo = i
			}
		}
		if f.teams[hi] != f.teams[lo] {
			return fmt.Errorf("bout %d of 4: highest and lowest Melee %v are not teammates, teams %v", b.id, f.melee, f.teams)
		}
	}
	for i, opponent := range f.opponents {
		at := -1
		for j, m := range f.members {
			if m == opponent {
				at = j
			}
		}
		if at < 0 || f.teams[at] == f.teams[i] {
			return fmt.Errorf("bout %d: %s's opponent %q is not on another team", b.id, f.members[i], opponent)
		}
	}
	return nil
}
