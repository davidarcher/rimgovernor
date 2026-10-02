package remoteaccept

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestSoakRatesCountsFinalAttemptPerRepetition(t *testing.T) {
	root, soak := t.TempDir(), t.TempDir()
	sel := Selection{Cases: []SelectedCase{{Name: "power/a"}, {Name: "power/b"}}, Shards: []PlannedShard{{ID: "s1", Cases: []string{"power/a"}}, {ID: "s2", Cases: []string{"power/b"}}}}
	if _, err := WriteJSON(root, "selection.json", sel); err != nil {
		t.Fatal(err)
	}
	write := func(dir, shard string, attempts ...Attempt) {
		t.Helper()
		if err := os.MkdirAll(filepath.Join(dir, shard), 0o755); err != nil {
			t.Fatal(err)
		}
		if _, err := WriteJSON(dir, shard+"/attempts.json", Attempts{ShardID: filepath.Base(shard), Attempts: attempts}); err != nil {
			t.Fatal(err)
		}
	}
	write(root, "s1", Attempt{Case: "power/a", Status: "passed"})
	write(root, "s2", Attempt{Case: "power/b", Status: "failed"})
	write(soak, "r2/s1", Attempt{Case: "power/a", Status: "failed"}, Attempt{Case: "power/a", Status: "passed"})
	write(soak, "r2/s2", Attempt{Case: "power/b", Status: "passed"})
	write(soak, "r3/s1", Attempt{Case: "power/a", Status: "passed"})
	// r3/s2 is missing: it counts as a run that did not pass.
	got, err := SoakRates(root, soak, 3)
	if err != nil {
		t.Fatal(err)
	}
	want := []SoakRate{{"power/a", 3, 3}, {"power/b", 1, 3}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
	if got[1].String() != "power/b: 1/3 passed" {
		t.Fatalf("line %q", got[1].String())
	}
}
