package buildingruntime

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// Combat replays (#853): a fight recorded from a fixture run
// (RIMGOVERNOR_SNAPSHOT_DIR on a served combat case, promoted with
// `trim -combat <name>`) replayed stop by stop through DecideCombat with
// no game in the loop. Each stop rebuilds the view from the recorded frame
// at its tick with the planner's own code (combatFrameInputs, combatView,
// combatStop), feeds it, the recorded memory and the recorded geometry
// reply to DecideCombat, and checks the case's declarative assertions.
// Recorded facts are fixed: a stop whose frame is not at its tick, whose
// stop event no longer derives from the frame's events, or where
// DecideCombat asks another geometry question than the recorded one fails
// with "re-record".

// combatReplayStop is one replayed stop: the recording, the view as
// rebuilt, and what DecideCombat decided on it now.
type combatReplayStop struct {
	snap.RecordedStop
	Index  int
	View   policy.CombatView
	Orders []policy.CombatOrder
	Memory policy.CombatMemory
}

// replayCombat replays every stop of the recording at path.
func replayCombat(path string) ([]combatReplayStop, error) {
	stops, err := snap.CombatStops(path)
	if err != nil {
		return nil, err
	}
	out := make([]combatReplayStop, 0, len(stops))
	for i, s := range stops {
		if s.Frame == nil || domain.Tick(s.Frame.GetContext().GetTick()) != s.Tick {
			return nil, fmt.Errorf("stop %d (tick %d): re-record: no frame at the stop's tick", i, s.Tick)
		}
		combat, err := decodeCombatWithCatalog(s.Frame)
		if err != nil {
			return nil, fmt.Errorf("stop %d (tick %d): %w", i, s.Tick, err)
		}
		in, reason, err := combatFrameInputs(combat, nil)
		if err != nil || !reason.IsZero() {
			return nil, fmt.Errorf("stop %d (tick %d): re-record: the frame holds no fight (%s %v)", i, s.Tick, reason, err)
		}
		var layout domain.Fact[policy.CombatLayout]
		if s.Layout != nil {
			layout = domain.Known(*s.Layout)
		}
		view := combatView(combat, in, s.Orderable, layout)
		if stop := combatStop(combat, s.MemoryIn.Tick); !reflect.DeepEqual(stop, s.Stop) {
			return nil, fmt.Errorf("stop %d (tick %d): re-record: the frame's events answer %+v, the recording %+v", i, s.Tick, stop, s.Stop)
		}
		orders, ask, memory := policy.DecideCombat(view, policy.GeometryReply{}, s.Stop, s.MemoryIn)
		if !reflect.DeepEqual(ask, s.Ask) {
			// Recordings older than sparing contained bleeders (#1035)
			// answer asks that still name them: replay those with the
			// population unknown. The rule itself is proven on a recorded
			// frame by TestCombatFrameSparesFleeingBleeder; lab-ranged
			// (#1152) is recorded after it and spares.
			view.Population = domain.Unknown[int]()
			orders, ask, memory = policy.DecideCombat(view, policy.GeometryReply{}, s.Stop, s.MemoryIn)
		}
		if !reflect.DeepEqual(ask, s.Ask) {
			return nil, fmt.Errorf("stop %d (tick %d): re-record: DecideCombat asks %+v, the recording answered %+v", i, s.Tick, ask, s.Ask)
		}
		if ask != nil {
			orders, _, memory = policy.DecideCombat(view, s.Reply, s.Stop, s.MemoryIn)
		}
		out = append(out, combatReplayStop{RecordedStop: s, Index: i, View: view, Orders: orders, Memory: memory})
	}
	return out, nil
}

// combatAssertion is one declarative claim over a fight: check holds at
// every stop at selects (nil: every stop).
type combatAssertion struct {
	name  string
	at    func(combatReplayStop) bool
	check func(combatReplayStop) error
}

// checkCombat replays path and checks each assertion; an assertion that
// selects no stop fails, so a re-recorded fight cannot pass vacuously.
func checkCombat(t *testing.T, path string, asserts ...combatAssertion) []combatReplayStop {
	t.Helper()
	stops, err := replayCombat(path)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range asserts {
		selected := 0
		for _, s := range stops {
			if a.at != nil && !a.at(s) {
				continue
			}
			selected++
			if err := a.check(s); err != nil {
				t.Errorf("%s: stop %d (tick %d, %q): %v", a.name, s.Index, s.Tick, s.Stop.Kind, err)
			}
		}
		if selected == 0 {
			t.Errorf("%s: selects no stop of %d", a.name, len(stops))
		}
	}
	return stops
}

func firstStop(s combatReplayStop) bool { return s.Index == 0 }

func withOrders(s combatReplayStop) bool { return len(s.Orders) > 0 }

// attacksOnPresentHostiles: no attack names a hostile gone from the view's
// threats, the order native refuses as not_found (#904).
func attacksOnPresentHostiles() combatAssertion {
	return combatAssertion{name: "attacks name present hostiles", check: func(s combatReplayStop) error {
		for _, o := range s.Orders {
			if o.Kind == policy.OrderAttack && o.Reason != policy.ReasonEnrage && !slices.ContainsFunc(s.View.Threats, func(t policy.SquadThreatFacts) bool { return domain.PawnID(t.ID) == o.Target }) {
				return fmt.Errorf("%s ordered to attack missing %s", o.Pawn, o.Target)
			}
		}
		return nil
	}}
}

// noAimInterrupt: no order interrupts a pawn in aim warmup or cooldown
// unless the reason is retreat or rescue.
func noAimInterrupt() combatAssertion {
	return combatAssertion{name: "no order interrupts aim", check: func(s combatReplayStop) error {
		for _, o := range s.Orders {
			if o.Reason == policy.ReasonRetreat || o.Reason == policy.ReasonRescue {
				continue
			}
			for _, p := range s.View.Pawns {
				if p.ID == o.Pawn && (p.Stance == policy.StanceWarmup || p.Stance == policy.StanceCooldown) {
					return fmt.Errorf("%s ordered %s in %s", o.Pawn, o.Kind, p.Stance)
				}
			}
		}
		return nil
	}}
}

// changesOnly: no order repeats what its pawn is doing (on the target, at
// the cell).
func changesOnly() combatAssertion {
	return combatAssertion{name: "orders are changes only", check: func(s combatReplayStop) error {
		for _, o := range s.Orders {
			for _, p := range s.View.Pawns {
				if p.ID != o.Pawn {
					continue
				}
				at, known := p.Cell.Value()
				if o.Kind == policy.OrderAttack && p.Target == o.Target || o.Kind == policy.OrderMove && known && at == o.Cell {
					return fmt.Errorf("%s already doing %+v", o.Pawn, o)
				}
			}
		}
		return nil
	}}
}

// ordersOwnedDrafts: every order names a pawn whose draft the fight owns.
func ordersOwnedDrafts() combatAssertion {
	return combatAssertion{name: "orders name owned drafts", check: func(s combatReplayStop) error {
		for _, o := range s.Orders {
			if o.Pawn != "" && !containsPawn(s.View.Orderable, o.Pawn) { // a door order names none
				return fmt.Errorf("%s is not orderable", o.Pawn)
			}
		}
		return nil
	}}
}

// formsTactic: the stops at selects end in tactic.
func formsTactic(at func(combatReplayStop) bool, tactic policy.CombatTactic) combatAssertion {
	return combatAssertion{name: "forms " + string(tactic), at: at, check: func(s combatReplayStop) error {
		if s.Memory.Tactic != tactic {
			return fmt.Errorf("tactic %q (refusal %q)", s.Memory.Tactic, s.Memory.Refusal)
		}
		return nil
	}}
}

// rolesOnLiveHostiles: the formation has roles, and every role's target
// is a hostile threat standing in the view.
func rolesOnLiveHostiles(at func(combatReplayStop) bool) combatAssertion {
	return combatAssertion{name: "roles on live hostiles", at: at, check: func(s combatReplayStop) error {
		if len(s.Memory.Roles) == 0 {
			return fmt.Errorf("no roles")
		}
		live := map[domain.PawnID]bool{}
		for _, t := range s.View.Threats {
			live[domain.PawnID(t.ID)] = !positiveFact(t.Dead) && !positiveFact(t.Downed)
		}
		for _, r := range s.Memory.Roles {
			if !live[r.Target] {
				return fmt.Errorf("%s targets %q, not a live hostile", r.Pawn, r.Target)
			}
		}
		return nil
	}}
}

func containsPawn(ids []domain.PawnID, id domain.PawnID) bool {
	for _, x := range ids {
		if x == id {
			return true
		}
	}
	return false
}

// Every committed combat recording stays within the #853 cap.
func TestCombatRecordingsSizeCap(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	paths, err := filepath.Glob("testdata/combat/*.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	for _, path := range paths {
		info, err := os.Stat(path)
		if err != nil {
			t.Fatal(err)
		}
		if info.Size() > snap.CombatRecordingMaxBytes {
			t.Errorf("%s: %d bytes gzipped, over %d", path, info.Size(), snap.CombatRecordingMaxBytes)
		}
		stops, err := snap.CombatStops(path)
		if err != nil {
			t.Fatal(err)
		}
		if len(stops) == 0 || len(stops) > snap.CombatRecordingMaxStops {
			t.Errorf("%s: %d stops, want 1..%d", path, len(stops), snap.CombatRecordingMaxStops)
		}
	}
}

// The harness itself: the crossed-hold fight, served by the planner with
// recording on, promoted with TrimCombat, replays to the memory the fight
// recorded at every stop (the hold, its orders, the squad re-formation);
// a recording whose geometry ask no longer matches fails with "re-record".
func TestCombatReplayHarness(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	dir := t.TempDir()
	t.Setenv(snap.DirEnv, dir)
	crossedHoldFight(t)
	snap.Flush()
	streams, _ := filepath.Glob(filepath.Join(dir, "routine-stream-*.jsonl"))
	if len(streams) != 1 {
		t.Fatal(streams)
	}
	data, err := snap.TrimCombat(streams[0], "")
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dir, "fight.json.gz")
	if err = os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
	last := func(s combatReplayStop) bool { return s.Evidence && s.Memory.Tactic == policy.TacticSquad }
	stops := checkCombat(t, path,
		formsTactic(firstStop, policy.TacticHold),
		formsTactic(last, policy.TacticSquad),
		rolesOnLiveHostiles(last),
		ordersOwnedDrafts(),
		changesOnly(),
		noAimInterrupt(),
		combatAssertion{name: "a stop sends orders", at: withOrders, check: func(combatReplayStop) error { return nil }},
	)
	// The admission stop (its formation; the drafts and first orders
	// ride its batch, #910) and the squad re-formation: the steady stop
	// between them records nothing.
	if len(stops) != 2 {
		t.Fatalf("%d stops", len(stops))
	}
	for _, s := range stops {
		if !reflect.DeepEqual(s.Memory, s.MemoryOut) {
			t.Errorf("stop %d: replayed %+v, recorded %+v", s.Index, s.Memory, s.MemoryOut)
		}
		if s.Evidence != (len(s.Orders) > 0) {
			t.Errorf("stop %d: evidence %v with %d orders", s.Index, s.Evidence, len(s.Orders))
		}
	}
	if stops[0].Ask == nil {
		t.Fatal("the hold formed without its geometry round trip")
	}
	// The same fight recorded without its layout: the formation asks
	// nothing, which is not the recorded question.
	lines, err := gunzipLines(data)
	if err != nil {
		t.Fatal(err)
	}
	for i, line := range lines {
		var fields map[string]json.RawMessage
		if err = json.Unmarshal(line, &fields); err != nil {
			t.Fatal(err)
		}
		if fields["Combat"] == nil {
			continue
		}
		var s snap.CombatStop
		if err = snap.Decode(fields["Combat"], &s); err != nil {
			t.Fatal(err)
		}
		s.Layout = nil
		if fields["Combat"], err = snap.Encode(s); err != nil {
			t.Fatal(err)
		}
		if lines[i], err = json.Marshal(fields); err != nil {
			t.Fatal(err)
		}
		break
	}
	var tampered bytes.Buffer
	z := gzip.NewWriter(&tampered)
	for _, line := range lines {
		_, _ = z.Write(append(line, '\n'))
	}
	if err = z.Close(); err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(path, tampered.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err = replayCombat(path); err == nil || !strings.Contains(err.Error(), "re-record") {
		t.Fatal("a changed geometry ask replayed:", err)
	}
}

func gunzipLines(data []byte) ([][]byte, error) {
	z, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		return nil, err
	}
	raw, err := io.ReadAll(z)
	if err != nil {
		return nil, err
	}
	return bytes.Split(bytes.TrimSpace(raw), []byte("\n")), nil
}

// lab-open (#854): three riflemen against three melee raiders on an open
// field, served by the routine defense planner. With no defense layout the
// formation is squad defense on every raider, and its orders are changes
// only, to owned drafts, never throwing a shot away.
func TestCombatReplayLabOpen(t *testing.T) {
	t.Parallel()
	checkCombat(t, "testdata/combat/lab-open.json.gz",
		formsTactic(firstStop, policy.TacticSquad),
		rolesOnLiveHostiles(firstStop),
		ordersOwnedDrafts(),
		changesOnly(),
		noAimInterrupt(),
	)
	// Every combat stop steps DecideCombat since #890, but on an open field
	// the drafted riflemen are already on the squad's targets, so the stops
	// change nothing and send no orders; lab-choke's do.
}

// lab-choke (#854, #890): two longsword blockers and a reserve at the gap
// of a walled room, two riflemen behind, six club raiders, served with
// the fixture's defense layout. The hold forms at once with its blocker
// and reserve duties, later stops send orders (fire mode), and the
// raid_phase re-formation with raiders in melee at the choke keeps the hold (#905). A serious
// injury retreating is staged in policy (TestDecideCombatSwapsHurtBlocker),
// not asserted here: a recording holds one only by luck (#1196).
func TestCombatReplayLabChoke(t *testing.T) {
	t.Parallel()
	checkCombat(t, "testdata/combat/lab-choke.json.gz",
		formsTactic(firstStop, policy.TacticHold),
		formsTactic(func(s combatReplayStop) bool { return s.Stop.Kind == policy.StopRaidPhase }, policy.TacticHold),
		combatAssertion{name: "blockers and a reserve", at: firstStop, check: func(s combatReplayStop) error {
			duties := map[string]int{}
			for _, r := range s.Memory.Roles {
				duties[string(r.Duty)]++
			}
			if duties["blocker"] < 2 || duties["reserve"] < 1 {
				return fmt.Errorf("duties %v", duties)
			}
			return nil
		}},
		ordersOwnedDrafts(),
		changesOnly(),
		attacksOnPresentHostiles(),
		noAimInterrupt(),
		// Hold fire holds while the raider fights our blocker between
		// swings (#903): no fire-at-will at stops 2 and 5, except for a
		// gunner passed on to a raider not on a blocker (#978).
		combatAssertion{name: "hold fire holds", at: func(s combatReplayStop) bool { return s.Index == 2 || s.Index == 5 }, check: func(s combatReplayStop) error {
			for _, o := range s.Orders {
				if o.Kind != policy.OrderFireMode || o.FireMode != policy.FireAtWill {
					continue
				}
				i := slices.IndexFunc(s.Memory.Roles, func(r policy.CombatRole) bool { return r.Pawn == o.Pawn })
				if i >= 0 && s.Memory.Roles[i].Target == "" {
					// Autonomous holders have no policy target; only an
					// observed shot at a melee-locked target keeps them held.
					shooter := slices.IndexFunc(s.View.Pawns, func(p policy.CombatPawnState) bool { return p.ID == o.Pawn })
					if shooter >= 0 && (s.View.Pawns[shooter].Target == "" || s.View.Pawns[shooter].Stance == policy.StanceIdle) {
						continue
					}
					if shooter >= 0 {
						j := slices.IndexFunc(s.View.Pawns, func(p policy.CombatPawnState) bool { return p.ID == s.View.Pawns[shooter].Target })
						if j >= 0 && !slices.ContainsFunc(s.Memory.Roles, func(r policy.CombatRole) bool { return r.Pawn == s.View.Pawns[j].Target && !r.Ranged }) {
							continue
						}
					}
					return fmt.Errorf("%s resumes autonomous fire on a blocker", o.Pawn)
				}
				j := -1
				if i >= 0 {
					j = slices.IndexFunc(s.View.Pawns, func(p policy.CombatPawnState) bool { return p.ID == s.Memory.Roles[i].Target })
				}
				blocker := func(id domain.PawnID) bool {
					return slices.ContainsFunc(s.Memory.Roles, func(r policy.CombatRole) bool { return r.Pawn == id && !r.Ranged })
				}
				if j < 0 || blocker(s.View.Pawns[j].Target) {
					return fmt.Errorf("%s back to fire at will", o.Pawn)
				}
			}
			return nil
		}},
		combatAssertion{name: "a stop sends orders", at: withOrders, check: func(combatReplayStop) error { return nil }},
	)
}

// lab-pods (#870, #897): four riflemen between a walled landing room and a
// safe room, an unarmed colonist inside the landing room, four rifle
// raiders dropped into it. The pods tactic forms at the first stop with
// the landing room from the frame's standing rooms: the civilian's
// evacuee cell is outside the landing room (moved there once drafted, #911), two
// riflemen take the doorway flanks (standable per the
// geometry read) and the landing door is held open. Not asserted:
// drafting before the open tick (the recording predates #908 and starts
// at the open) and a strike (no raider fled, looted or went down in the
// recording, and four against four never waits).
func TestCombatReplayLabPods(t *testing.T) {
	t.Parallel()
	inside := func(c domain.Cell) bool { return c.X >= 45 && c.X <= 55 && c.Z >= 55 && c.Z <= 65 }
	var evacuee domain.PawnID
	stops := checkCombat(t, "testdata/combat/lab-pods.json.gz",
		formsTactic(firstStop, policy.TacticPods),
		combatAssertion{name: "doorway pair and an evacuee", at: firstStop, check: func(s combatReplayStop) error {
			duties := map[string]int{}
			for _, r := range s.Memory.Roles {
				duties[string(r.Duty)]++
				if r.Duty == "evacuee" {
					evacuee = r.Pawn
				}
			}
			if duties["doorway"] != 2 || duties["evacuee"] != 1 {
				return fmt.Errorf("duties %v", duties)
			}
			return nil
		}},
		combatAssertion{name: "the landing door is held open", at: firstStop, check: func(s combatReplayStop) error {
			for _, o := range s.Orders {
				if o.Door == "hold_open" && o.Cell == (domain.Cell{X: 50, Z: 54}) {
					return nil
				}
			}
			return fmt.Errorf("orders %+v", s.Orders)
		}},
		ordersOwnedDrafts(),
		changesOnly(),
		// The raiders stand behind the landing room's walls: no attack goes
		// out along a line the game answered blocked, the order native
		// refused cannot_hit at every stop of the recording (#912).
		combatAssertion{name: "no attack along a blocked line", check: func(s combatReplayStop) error {
			cells := map[domain.PawnID]domain.Cell{}
			for _, p := range s.View.Pawns {
				if c, ok := p.Cell.Value(); ok {
					cells[p.ID] = c
				}
			}
			for _, o := range s.Orders {
				for _, l := range s.Reply.Lines {
					if o.Kind == policy.OrderAttack && l.Hostile == o.Target && l.Cell == cells[o.Pawn] && !l.LineOfFire {
						return fmt.Errorf("%s ordered to attack %s with no line of fire", o.Pawn, o.Target)
					}
				}
			}
			return nil
		}},
	)
	// The evacuee's cell is the safe room's (the recording predates #910,
	// so the fight never drafted it); the admission batch drafts every
	// role pawn, and decides as they will be once drafted: the evacuee's
	// move out of the landing room goes out with it (#911).
	for _, r := range stops[0].Memory.Roles {
		if r.Pawn == evacuee && (r.Cell == nil || inside(*r.Cell)) {
			t.Fatalf("evacuee %s sent to %v, inside the landing room", evacuee, r.Cell)
		}
	}
	first := stops[0]
	var pawns []domain.PawnID
	for _, r := range first.Memory.Roles {
		pawns = append(pawns, r.Pawn)
	}
	view := first.View
	view.Orderable = pawns
	orders, _, _ := policy.DecideCombat(view, first.Reply, policy.StopEvent{}, first.Memory)
	if !slices.ContainsFunc(orders, func(o policy.CombatOrder) bool {
		return o.Pawn == evacuee && o.Kind == policy.OrderMove && !inside(o.Cell)
	}) {
		t.Fatalf("admission orders %+v move no evacuee %s out", orders, evacuee)
	}
}
