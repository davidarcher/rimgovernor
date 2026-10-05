package policy

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"testing"
)

// The replay score gate (#2094, epic #2092): testdata/layout-replay-scores.json
// records, per replayFixtures entry, the score terms, total, hard-tier verdict
// and room count. TestReplayScoreFixtures fails when a fixture's total drops by
// more than replayTolerance of the recorded one or its verdict flips; a plan
// that scores higher passes (output may change within the gate). Refresh the
// table after an intended change, from go/:
//
//	RIMGOVERNOR_UPDATE_SNAPSHOT=1 go test ./internal/policy -run TestReplayScoreFixtures
//
// Never edit the file by hand.

const (
	replayScoresPath = "testdata/layout-replay-scores.json"
	// replayTolerance is the share of the recorded total a fixture may lose.
	replayTolerance = 0.01
)

type replayRow struct {
	Pass      bool
	Total     int
	Missing   int
	RoutesErr string
	RichCells int
	Soil      int
	Footprint int
	Wall      int
	Edge      int
	Centre    int
	Defense   int
	Expansion int
	Rooms     int
}

func newReplayRow(sc PlanScore, rooms int) replayRow {
	return replayRow{sc.Passes(), sc.Total(), len(sc.Missing), sc.RoutesErr, sc.RichCells,
		sc.Soil, sc.Footprint, sc.Wall, sc.Edge, sc.Centre, sc.Defense, sc.Expansion, rooms}
}

func updateReplayScores() bool { return os.Getenv("RIMGOVERNOR_UPDATE_SNAPSHOT") == "1" }

func loadReplayScores(t *testing.T) map[string]replayRow {
	t.Helper()
	b, err := os.ReadFile(replayScoresPath)
	if err != nil {
		t.Fatalf("%v (record it with RIMGOVERNOR_UPDATE_SNAPSHOT=1)", err)
	}
	var out map[string]replayRow
	if err := json.Unmarshal(b, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func writeReplayScores(t *testing.T, rows map[string]replayRow) {
	t.Helper()
	b, err := json.MarshalIndent(rows, "", " ")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(replayScoresPath, append(bytes.TrimRight(b, "\n"), '\n'), 0o644); err != nil {
		t.Fatal(err)
	}
}

// checkReplayRow is the gate: nil when got holds against the recorded want.
func checkReplayRow(want, got replayRow) error {
	if want.Pass != got.Pass {
		return fmt.Errorf("hard-tier verdict flipped: pass=%v, recorded %v (%+v)", got.Pass, want.Pass, got)
	}
	slack := int(replayTolerance * float64(max(want.Total, -want.Total)))
	if got.Total < want.Total-slack {
		return fmt.Errorf("total %d fell below recorded %d by more than %d", got.Total, want.Total, slack)
	}
	return nil
}

func TestReplayGateCheck(t *testing.T) {
	want := replayRow{Pass: true, Total: -10000}
	if checkReplayRow(want, replayRow{Pass: true, Total: -10100}) != nil {
		t.Fatal("a drop inside the tolerance failed")
	}
	if checkReplayRow(want, replayRow{Pass: true, Total: -10101}) == nil {
		t.Fatal("a drop beyond the tolerance passed")
	}
	if checkReplayRow(want, replayRow{Pass: true, Total: 5}) != nil {
		t.Fatal("an improvement failed")
	}
	if checkReplayRow(want, replayRow{Pass: false, Total: -9000}) == nil {
		t.Fatal("a verdict flip passed")
	}
}
