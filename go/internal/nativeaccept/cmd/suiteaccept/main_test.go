package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestOrderLongestFirst(t *testing.T) {
	path := filepath.Join(t.TempDir(), "result.json")
	if err := os.WriteFile(path, []byte(`{"harnesses":[{"name":"a","wall_ms":10},{"name":"b","wall_ms":30},{"name":"c","wall_ms":20}]}`), 0644); err != nil {
		t.Fatal(err)
	}
	list := []harness{{Name: "a"}, {Name: "b"}, {Name: "c"}, {Name: "new"}}
	if err := orderLongestFirst(list, path); err != nil {
		t.Fatal(err)
	}
	got := ""
	for _, h := range list {
		got += h.Name + " "
	}
	if got != "new b c a " {
		t.Errorf("order = %q", got)
	}
	if err := orderLongestFirst(list, filepath.Join(t.TempDir(), "missing.json")); err == nil {
		t.Error("expected an error for a missing file")
	}
}
