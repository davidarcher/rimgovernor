package lab

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// Watching a sparring bout (#2732): the watch-sparring joy giver and its driver.
// The fixture's recorder reads every watch job the tick it runs, so the case
// asserts what it saw: where the spectator stood, that joy rose, that the watch
// ended with the bout and earned the thought.

var sparringWatchOps = []string{"test/sparring_prepare", "test/sparring_inspect", "test/sparring_watch_prepare", "test/sparring_watch_inspect"}

func init() {
	cases.Register(cases.Case{
		Name: "lab/sparring-watch",
		Scope: "The watch-sparring joy giver (#2732): with a fighting bout and a free colonist at joy time, vanilla's joy pick sends the bystander to a " +
			"spectator cell outside the ring, 3 to 8 cells from a fighter, in sight of the bout; Joy rises while it watches; the watch ends when the bout " +
			"ends and a watch of a few hundred ticks leaves the watched-a-sparring-match thought. A drafted colonist and one on an all-work schedule " +
			"never watch, no fighter is ever a spectator, and with no bout fighting nobody watches. A Go snapshot test cannot see the joy pick, the walk, the Joy need or the memory.",
		Start:       cases.Lab{Colonists: 8},
		RequiredOps: sparringWatchOps,
		QuietWorld:  true,
		Budget:      10 * time.Minute,
		Keep:        []string{"Joy"},
		Crew:        cases.Crew{Size: 3},
		Run:         runSparringWatch,
	})
}

type sparringWatcher struct {
	id          string
	started     int
	ended       int
	joyStart    float64
	joyMax      float64
	cellOK      bool
	cellNote    string
	dist        float64
	fighter     bool
	atCell      int
	fightingEnd bool
	thought     bool
}

func sparringWatchers(got map[string]any) []sparringWatcher {
	var out []sparringWatcher
	for _, raw := range na.AsSlice(got["watchers"]) {
		row, _ := na.AsMap(raw)
		flag := func(key string) bool { v, _ := na.AsBool(row[key]); return v }
		w := sparringWatcher{
			id: na.AsString(row["id"]), started: int(na.AsNumber(row["started"])), ended: int(na.AsNumber(row["ended"])),
			joyStart: na.AsNumber(row["joyStart"]), joyMax: na.AsNumber(row["joyMax"]), cellOK: flag("cellOk"), cellNote: na.AsString(row["cellNote"]),
			dist: na.AsNumber(row["dist"]), fighter: flag("fighter"), atCell: int(na.AsNumber(row["atCellTicks"])), fightingEnd: flag("fightingAtEnd"),
			thought: flag("thought"),
		}
		out = append(out, w)
	}
	return out
}

func runSparringWatch(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	report := map[string]any{}
	defer func() { s.Report()["sparringWatch"] = report }()

	// Stage 1: a fighting bout, a free bystander, a drafted one and an all-work one.
	prepared, err := prepareSparring(ctx, s, h, map[string]any{"eligible": 2, "levels": "5,4", "scenario": "cap"})
	if err != nil {
		return err
	}
	center, _ := na.AsMap(s.Prepared()["center"])
	watch, err := h.Call(ctx, "sparring-watch-prepare", "test/sparring_watch_prepare", map[string]any{
		"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])), "bystanders": 1, "drafted": true, "working": true,
	})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(watch["success"]); !ok {
		return fmt.Errorf("sparring_watch_prepare: %#v", watch)
	}
	free := stringList(watch["free"])
	var last map[string]any
	if _, err := na.RunUntil(ctx, h, "sparring-watch", 20000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "sparring-watch-inspect", "test/sparring_watch_inspect", map[string]any{})
		if err != nil {
			return "", false, err
		}
		last = got
		done := false
		count := 0
		for _, w := range sparringWatchers(got) {
			count++
			// A long watch ended, with time to read its thought.
			if w.id == free[0] && w.ended >= 0 && w.atCell >= 350 && na.AsNumber(got["tick"]) > float64(w.ended)+5 {
				done = true
			}
		}
		return fmt.Sprint(count), done, nil
	}); err != nil {
		report["watch"] = last
		return fmt.Errorf("watch stage: %w", err)
	}
	report["watch"] = last
	if err := checkSparringWatch(prepared, watch, last); err != nil {
		return err
	}

	// Stage 2: a ring with no bout (nobody to fight): a bystander at joy time watches nothing.
	if _, err := prepareSparring(ctx, s, h, map[string]any{"eligible": 0, "levels": "5"}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "sparring-watch-prepare-idle", "test/sparring_watch_prepare", map[string]any{
		"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])), "bystanders": 1,
	}); err != nil {
		return err
	}
	start := float64(-1)
	if _, err := na.RunUntil(ctx, h, "sparring-watch-none", 6000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "sparring-watch-inspect-wait", "test/sparring_watch_inspect", map[string]any{})
		if err != nil {
			return "", false, err
		}
		tick := na.AsNumber(got["tick"])
		if start < 0 {
			start = tick
		}
		return "", tick >= start+2500, nil
	}); err != nil {
		return err
	}
	idle, err := h.Call(ctx, "sparring-watch-inspect-idle", "test/sparring_watch_inspect", map[string]any{})
	if err != nil {
		return err
	}
	report["noBout"] = idle
	if n := na.AsNumber(idle["watchJobs"]); n != 0 {
		return fmt.Errorf("a colonist watched for %v ticks with no bout fighting: %#v", n, idle)
	}
	return nil
}

func checkSparringWatch(prepared, watch, got map[string]any) error {
	free := stringList(watch["free"])
	fighters := stringList(prepared["eligible"])
	if n := na.AsNumber(got["fighterWatchTicks"]); n != 0 {
		return fmt.Errorf("a pawn in a bout was on a watch job for %v ticks: %#v", n, got)
	}
	long := 0
	for _, w := range sparringWatchers(got) {
		switch {
		case w.id == na.AsString(watch["drafted"]) || w.id == na.AsString(watch["working"]):
			return fmt.Errorf("%s (drafted or on an all-work schedule) watched: %#v", w.id, got)
		case sparringHas(fighters, w.id) || w.fighter:
			return fmt.Errorf("fighter %s watched: %#v", w.id, got)
		case w.id != free[0]:
			continue
		case !w.cellOK:
			return fmt.Errorf("spectator cell is not valid (%s, %.1f cells from a fighter): %#v", w.cellNote, w.dist, got)
		case w.ended < 0:
			continue
		case w.fightingEnd:
			return fmt.Errorf("the watch ended at tick %d while a bout was still fighting: %#v", w.ended, got)
		// The thought needs a few hundred ticks of watching; the edges are left unjudged.
		case w.atCell >= 350 && !w.thought:
			return fmt.Errorf("no watched-a-sparring-match thought after %d watching ticks: %#v", w.atCell, got)
		case w.atCell <= 250 && w.thought:
			return fmt.Errorf("watched-a-sparring-match thought after only %d watching ticks: %#v", w.atCell, got)
		case w.atCell >= 350 && w.joyMax <= w.joyStart:
			return fmt.Errorf("joy did not rise while watching (%.3f to %.3f): %#v", w.joyStart, w.joyMax, got)
		}
		if w.atCell >= 350 {
			long++
		}
	}
	if long == 0 {
		return fmt.Errorf("the free bystander never completed a long watch: %#v", got)
	}
	return nil
}
