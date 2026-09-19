package main

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cost"
)

func TestCostLineTotalsAffectedAreas(t *testing.T) {
	path := filepath.Join(t.TempDir(), "result.json")
	if err := os.WriteFile(path, []byte(`{"cases":[
		{"name":"light/basic","wall_ms":60000,"boot_ms":5000},
		{"name":"light/dim","wall_ms":120000},
		{"name":"smoke/identity","wall_ms":10000}]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	table, err := cost.Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if got := costLine(table, affected.Selection{Cases: []string{"light"}}); got != "cost: 2 cases, 3m0s wall (boot 5s) timed by "+path {
		t.Errorf("area total = %q", got)
	}
	if got := costLine(table, affected.Selection{AllHarnesses: true}); got != "cost: 3 cases, 3m10s wall (boot 5s) timed by "+path {
		t.Errorf("all-harness total = %q", got)
	}
	if got := costLine(table, affected.Selection{Cases: []string{"defense"}}); got != "cost: no affected case timed by "+path {
		t.Errorf("untimed area = %q", got)
	}
}
