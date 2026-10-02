package remoteaccept

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

// SoakRate is one selected case's pass count over a repeat=N soak run.
type SoakRate struct {
	Case   string
	Passed int
	Runs   int
}

func (r SoakRate) String() string { return fmt.Sprintf("%s: %d/%d passed", r.Case, r.Passed, r.Runs) }

// SoakRates tallies per-case passes over repeat soak repetitions. Repetition 1
// is the verdict evidence under root; repetition k > 1 lives under
// soak/r<k>/<shard>/attempts.json. A missing or unreadable repetition counts as
// a run that did not pass. Display only: the verdict is Evaluate's.
func SoakRates(root, soak string, repeat int) ([]SoakRate, error) {
	if repeat < 1 {
		return nil, fmt.Errorf("repeat must be at least 1")
	}
	b, err := os.ReadFile(filepath.Join(root, "selection.json"))
	if err != nil {
		return nil, err
	}
	var s Selection
	if err = json.Unmarshal(b, &s); err != nil {
		return nil, err
	}
	rates := make([]SoakRate, 0, len(s.Cases))
	index := map[string]int{}
	for _, c := range s.Cases {
		index[c.Name] = len(rates)
		rates = append(rates, SoakRate{Case: c.Name, Runs: repeat})
	}
	for rep := 1; rep <= repeat; rep++ {
		dir := root
		if rep > 1 {
			dir = filepath.Join(soak, fmt.Sprintf("r%d", rep))
		}
		for _, sh := range s.Shards {
			b, err := os.ReadFile(filepath.Join(dir, sh.ID, "attempts.json"))
			if err != nil {
				continue
			}
			var a Attempts
			if json.Unmarshal(b, &a) != nil || a.ShardID != sh.ID {
				continue
			}
			final := map[string]string{}
			for _, at := range a.Attempts {
				final[at.Case] = at.Status
			}
			for _, name := range sh.Cases {
				if i, ok := index[name]; ok && final[name] == "passed" {
					rates[i].Passed++
				}
			}
		}
	}
	return rates, nil
}
