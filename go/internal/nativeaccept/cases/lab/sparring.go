package lab

import (
	"context"
	"fmt"
	"sort"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// Bout formation (#2708): who spars with whom at the ring. The bout registry,
// matchmaking and the work giver are native; the fixture's watcher samples the
// registry every tick (the game runs thousands of ticks between two reads), so
// the case asserts what the watcher saw, not a poll.

var sparringOps = []string{"test/sparring_prepare", "test/sparring_inspect"}

func init() {
	cases.Register(cases.Case{
		Name: "lab/sparring",
		Scope: "Bout formation at the sparring ring (#2708): with no order from Go the vanilla work scan offers eligible colonists a marker, and " +
			"2, 3, 4 and 5 eligible colonists form bouts of 2; 3 (free-for-all); 4 (2v2, highest with lowest); and 3 + 2 at once. Every pawn and every " +
			"marker is in at most one bout, a colonist above the ceiling and a drafted one are never seated, a pawn drafted while its bout gathers is " +
			"dropped and the rest fight on, and every fighter's opponent is a member of another team. A Go snapshot test cannot see the work giver, " +
			"the registry or the walk to the markers.",
		Start:       cases.Lab{Colonists: 8},
		RequiredOps: sparringOps,
		QuietWorld:  true,
		Budget:      cases.LabBudget,
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
	draft      bool
}

func runSparring(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	stages := []sparringStage{
		{name: "pair", eligible: 2, levels: "5,3", sizes: []int{2}},
		{name: "three", eligible: 3, levels: "6,4,2", sizes: []int{3}},
		{name: "four", eligible: 4, levels: "7,5,4,2", sizes: []int{4}},
		{name: "five-two-bouts", eligible: 5, levels: "7,6,5,4,3", sizes: []int{3, 2}},
		{name: "ineligible", eligible: 2, levels: "5,4", sizes: []int{2}, ineligible: true},
		{name: "drafted-while-gathering", eligible: 4, levels: "7,5,4,2", sizes: []int{3}, draft: true},
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
		"eligible": stage.eligible, "levels": stage.levels, "ineligible": stage.ineligible, "draftOnGather": stage.draft,
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
	return last, checkSparringStage(stage, prepared, last)
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
