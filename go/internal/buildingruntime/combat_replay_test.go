package buildingruntime

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
		combat, err := bridge.DecodeCombat(s.Frame)
		if err != nil {
			return nil, fmt.Errorf("stop %d (tick %d): %w", i, s.Tick, err)
		}
		in, reason, err := combatFrameInputs(combat)
		if err != nil || reason != "" {
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
			if !containsPawn(s.View.Orderable, o.Pawn) {
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
	dir := t.TempDir()
	t.Setenv(snap.DirEnv, dir)
	crossedHoldFight(t)
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
	if len(stops) != 3 {
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
	// The served run (#869) decides once: it admits the squad plan at the
	// first stop and later stops change nothing, so no stop sends orders.
}
