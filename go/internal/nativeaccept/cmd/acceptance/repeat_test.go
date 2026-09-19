package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestParseRunRepeatAndSeedForceFreshAttempts(t *testing.T) {
	root := absRoot()
	var stderr bytes.Buffer
	_, opts, err := parseRun([]string{"smoke/identity", "-root", root, "-repeat", "3"}, &stderr)
	if err != nil {
		t.Fatalf("parseRun: %v (%s)", err, stderr.String())
	}
	if opts.Repeat != 3 || !opts.Fresh || opts.CheckpointEvery != 0 {
		t.Errorf("repeat opts = %+v", opts)
	}
	_, opts, err = parseRun([]string{"smoke/identity", "-root", root, "-seed", "tangent"}, &stderr)
	if err != nil {
		t.Fatalf("parseRun: %v (%s)", err, stderr.String())
	}
	if opts.Seed != "tangent" || !opts.Fresh || opts.Repeat != 1 || opts.CheckpointEvery != na.DefaultCheckpointEvery {
		t.Errorf("seed opts = %+v", opts)
	}
	if _, _, err := parseRun([]string{"smoke/identity", "-root", root, "-repeat", "0"}, &stderr); err == nil {
		t.Error("-repeat 0 accepted")
	}
}

func TestRepeatSummaryCountsAttemptsAndSeeds(t *testing.T) {
	attempts := []repeatAttempt{
		newAttempt(1, "out/a/b", na.Report{"world": na.WorldRecord{Seed: "one", Save: "s"}}, 0, 1500*time.Millisecond),
		newAttempt(2, "out/repeat/2/a/b", na.Report{"world": na.WorldRecord{Seed: "two"}, "error": "boom"}, 1, time.Second),
		newAttempt(3, "out/repeat/3/a/b", na.Report{"world": na.WorldRecord{Seed: "one"}}, 0, time.Second),
		newAttempt(4, "out/repeat/4/a/b", na.Report{"error": "no game"}, 1, time.Second),
	}
	s := summarizeRepeat("a/b", attempts)
	if s.Passed != 2 || s.Failed != 2 || s.PassRate != 0.5 || len(s.Seeds) != 2 || s.Seeds[0] != "one" || s.Seeds[1] != "two" {
		t.Errorf("summary = %+v", s)
	}
	if got := s.String(); got != "pass=2/4 seeds=[one two(fail) one -(fail)]" {
		t.Errorf("summary line = %q", got)
	}
	if attempts[0].WallMs != 1500 || attempts[1].Error != "boom" || attempts[3].World != nil {
		t.Errorf("attempts = %+v", attempts)
	}
	path := filepath.Join(t.TempDir(), "b.repeat.json")
	if err := s.write(path); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(path)
	var decoded repeatSummary
	if err := json.Unmarshal(data, &decoded); err != nil || decoded.Case != "a/b" || len(decoded.Attempts) != 4 || decoded.Attempts[0].World.Save != "s" {
		t.Errorf("written summary = %+v, %v", decoded, err)
	}
}
