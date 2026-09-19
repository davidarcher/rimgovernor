package main

// acceptance run -repeat N (#281): a case runs N times fresh on the kept
// process, each attempt with its own output directory
// (cases.Options.CaseOutput), and the summary beside the first attempt's
// directory says how many passed and which world each attempt ran on, so
// a flake shows as a pass rate under one build rather than as a rerun by
// hand. The summary is bookkeeping: the exit code is the attempts'.

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// repeatAttempt is one attempt's row of the summary.
type repeatAttempt struct {
	Attempt int    `json:"attempt"`
	Passed  bool   `json:"passed"`
	WallMs  int64  `json:"wall_ms"`
	Output  string `json:"output"`
	Error   string `json:"error,omitempty"`
	// World is the attempt's world block (na.RecordWorld), absent when
	// the run never opened a game.
	World *na.WorldRecord `json:"world,omitempty"`
}

// repeatSummary is <output>/<case>.repeat.json.
type repeatSummary struct {
	Case     string          `json:"case"`
	Attempts []repeatAttempt `json:"attempts"`
	Passed   int             `json:"passed"`
	Failed   int             `json:"failed"`
	PassRate float64         `json:"pass_rate"`
	// Seeds are the distinct seeds the attempts ran on, in attempt order;
	// one seed over every attempt means the world did not vary (a cached
	// start, a save), so a mixed verdict is not the roll's.
	Seeds []string `json:"seeds"`
}

func newAttempt(attempt int, output string, report na.Report, code int, wall time.Duration) repeatAttempt {
	row := repeatAttempt{Attempt: attempt, Passed: code == 0, WallMs: wall.Milliseconds(), Output: output}
	row.Error, _ = report["error"].(string)
	if world, ok := na.WorldOf(report); ok {
		row.World = &world
	}
	return row
}

func summarizeRepeat(name string, attempts []repeatAttempt) repeatSummary {
	s := repeatSummary{Case: name, Attempts: attempts, Seeds: []string{}}
	seen := map[string]bool{}
	for _, a := range attempts {
		if a.Passed {
			s.Passed++
		} else {
			s.Failed++
		}
		if a.World != nil && a.World.Seed != "" && !seen[a.World.Seed] {
			seen[a.World.Seed] = true
			s.Seeds = append(s.Seeds, a.World.Seed)
		}
	}
	if len(attempts) > 0 {
		s.PassRate = float64(s.Passed) / float64(len(attempts))
	}
	return s
}

// String is the summary's line on stdout: the pass count and the seeds
// each attempt ran on.
func (s repeatSummary) String() string {
	seeds := make([]string, 0, len(s.Attempts))
	for _, a := range s.Attempts {
		seed := "-"
		if a.World != nil && a.World.Seed != "" {
			seed = a.World.Seed
		}
		if !a.Passed {
			seed += "(fail)"
		}
		seeds = append(seeds, seed)
	}
	return fmt.Sprintf("pass=%d/%d seeds=[%s]", s.Passed, len(s.Attempts), strings.Join(seeds, " "))
}

func (s repeatSummary) write(path string) error {
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0644)
}
