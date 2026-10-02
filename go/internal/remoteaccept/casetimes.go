package remoteaccept

import (
	_ "embed"
	"encoding/json"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"time"
)

// caseTimesJSON is the measured wall time of each case on the CI runners,
// in milliseconds, from a recent full run (CaseTimes regenerates it). The
// shard planner balances on it: a case's Budget is its worst-case ceiling,
// often ten times what it takes, so balancing on budgets alone left two
// shards at 19 min and two at 75 min.
//
//go:embed casetimes.json
var caseTimesJSON []byte

var measured = func() map[string]int64 {
	out := map[string]int64{}
	_ = json.Unmarshal(caseTimesJSON, &out)
	return out
}()

// ShardCost is a case's planning weight: its measured wall time when the
// table has it, else its budget.
func ShardCost(name string, budget time.Duration) int64 {
	if ms, ok := measured[name]; ok && ms > 0 {
		return int64(time.Duration(ms) * time.Millisecond)
	}
	return int64(budget)
}

// Measured reports whether the table has a time for name.
func Measured(name string) bool { return measured[name] > 0 }

// CaseTimes reads every suite result.json under root (a downloaded run's
// shard artifacts) and returns the table merged over the current one.
func CaseTimes(root string) (map[string]int64, error) {
	out := map[string]int64{}
	for name, ms := range measured {
		out[name] = ms
	}
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() || d.Name() != "result.json" {
			return err
		}
		b, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		var suite struct {
			Cases []struct {
				Name   string `json:"name"`
				WallMS int64  `json:"wall_ms"`
			} `json:"cases"`
		}
		if json.Unmarshal(b, &suite) != nil {
			return nil
		}
		for _, c := range suite.Cases {
			if c.Name != "" && c.WallMS > 0 {
				out[c.Name] = c.WallMS
			}
		}
		return nil
	})
	return out, err
}

// EncodeCaseTimes renders the table one sorted case per line.
func EncodeCaseTimes(times map[string]int64) []byte {
	names := make([]string, 0, len(times))
	for name := range times {
		names = append(names, name)
	}
	sort.Strings(names)
	b := []byte("{\n")
	for i, name := range names {
		key, _ := json.Marshal(name)
		val, _ := json.Marshal(times[name])
		b = append(b, "  "...)
		b = append(b, key...)
		b = append(b, ": "...)
		b = append(b, val...)
		if i < len(names)-1 {
			b = append(b, ',')
		}
		b = append(b, '\n')
	}
	return append(b, "}\n"...)
}
