package cost

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func write(t *testing.T, name, body string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), name)
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestLoadSuiteResultPricesRows(t *testing.T) {
	table, err := Load(write(t, "result.json", `{"cases":[
		{"name":"a/one","wall_ms":240000,"boot_ms":5000},
		{"name":"a/two","wall_ms":1080000},
		{"name":"","wall_ms":10},
		{"name":"a/failed"}]}`))
	if err != nil {
		t.Fatal(err)
	}
	if r, ok := table.Of("a/one"); !ok || r.Wall != 4*time.Minute || r.Boot != 5*time.Second || r.Run() != 4*time.Minute-5*time.Second {
		t.Errorf("a/one = %+v %v", r, ok)
	}
	if _, ok := table.Of("a/failed"); ok {
		t.Error("a zero wall row is untimed")
	}
	if names := table.Names(); strings.Join(names, ",") != "a/one,a/two" {
		t.Errorf("names = %v", names)
	}
	s := Summarize(table, []string{"a/one", "a/two", "b/new"})
	if s.Timed != 2 || s.Untimed != 1 || s.Wall != 22*time.Minute || s.Boot != 5*time.Second {
		t.Errorf("summary = %+v", s)
	}
	if got := s.String(); got != "3 cases, 22m0s wall (boot 5s), 1 untimed" {
		t.Errorf("String() = %q", got)
	}
	if got := (Summary{Untimed: 1}).String(); got != "1 case, 1 untimed" {
		t.Errorf("all-untimed String() = %q", got)
	}
}

func TestLoadSeriesTakesTrailingMedianOfPasses(t *testing.T) {
	var lines []string
	// Eleven passes of a/one: the window drops the first (huge) row.
	lines = append(lines, `{"case":"a/one","passed":true,"metrics":{"wall_ms":9000000,"boot_ms":0}}`)
	for i := 0; i < 10; i++ {
		lines = append(lines, `{"case":"a/one","passed":true,"metrics":{"wall_ms":60000,"boot_ms":1000}}`)
	}
	lines = append(lines,
		`{"case":"a/two","passed":false,"metrics":{"wall_ms":1}}`,
		`{"case":"a/two","passed":true,"metrics":{"wall_ms":30000}}`,
		`{"case":"a/two","passed":true,"metrics":{"wall_ms":50000}}`,
		`not json`)
	table, err := Load(write(t, "metrics.jsonl", strings.Join(lines, "\n")+"\n"))
	if err != nil {
		t.Fatal(err)
	}
	if r, _ := table.Of("a/one"); r.Wall != time.Minute || r.Boot != time.Second {
		t.Errorf("a/one = %+v", r)
	}
	if r, _ := table.Of("a/two"); r.Wall != 40*time.Second || r.Boot != 0 {
		t.Errorf("a/two = %+v", r)
	}
}

func TestLoadRejectsMissingAndMalformed(t *testing.T) {
	if _, err := Load(filepath.Join(t.TempDir(), "missing.json")); err == nil {
		t.Error("missing result.json loaded")
	}
	if _, err := Load(filepath.Join(t.TempDir(), "missing.jsonl")); err == nil {
		t.Error("missing series loaded")
	}
	if _, err := Load(write(t, "result.json", `[1,2]`)); err == nil {
		t.Error("malformed result.json loaded")
	}
	var nilTable *Table
	if _, ok := nilTable.Of("a/one"); ok || nilTable.Names() != nil {
		t.Error("a nil table times nothing")
	}
	if s := Summarize(nilTable, []string{"x"}); s.Untimed != 1 {
		t.Errorf("nil summary = %+v", s)
	}
}
