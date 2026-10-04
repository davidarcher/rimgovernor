package main

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestRunPassesFastSuite(t *testing.T) {
	t.Parallel()
	in := strings.NewReader(`{"Action":"run","Package":"p","Test":"TestA"}
{"Action":"pass","Package":"p","Test":"TestA","Elapsed":0.5}
{"Action":"pass","Package":"p","Elapsed":0.5}
`)
	var out bytes.Buffer
	if code := run(in, &out, 10*time.Second); code != 0 {
		t.Fatalf("code = %d, output = %s", code, out.String())
	}
}

func TestRunFailsOnSlowTest(t *testing.T) {
	t.Parallel()
	in := strings.NewReader(`{"Action":"pass","Package":"p","Test":"TestSlow","Elapsed":11.2}
`)
	var out bytes.Buffer
	if code := run(in, &out, 10*time.Second); code != 1 {
		t.Fatalf("code = %d, want 1", code)
	}
	if !strings.Contains(out.String(), "TestSlow") {
		t.Fatalf("output missing offending test: %s", out.String())
	}
}

// A cached package replays an earlier run's Elapsed (the package ok line
// carries "(cached)"), which may have been measured under load; the budget
// judges only this run (#334).
func TestRunIgnoresCachedTimings(t *testing.T) {
	t.Parallel()
	in := strings.NewReader(`{"Action":"pass","Package":"p","Test":"TestSlow","Elapsed":32.2}
{"Action":"output","Package":"p","Output":"ok  \tp\t(cached)\n"}
{"Action":"pass","Package":"p","Elapsed":0}
`)
	var out bytes.Buffer
	if code := run(in, &out, 10*time.Second); code != 0 {
		t.Fatalf("code = %d, output = %s", code, out.String())
	}
}

func TestRunFailsOnTestFailure(t *testing.T) {
	t.Parallel()
	in := strings.NewReader(`{"Action":"fail","Package":"p","Test":"TestBroken","Elapsed":0.1}
`)
	var out bytes.Buffer
	if code := run(in, &out, 10*time.Second); code != 1 {
		t.Fatalf("code = %d, want 1", code)
	}
}

func TestRunFailsOnPackageBuildFailure(t *testing.T) {
	t.Parallel()
	in := strings.NewReader(`{"Action":"output","Package":"p","Output":"p/broken.go:3: syntax error\n"}
{"Action":"fail","Package":"p","Elapsed":0}
`)
	var out bytes.Buffer
	if code := run(in, &out, 10*time.Second); code != 1 {
		t.Fatalf("code = %d, want 1", code)
	}
	if !strings.Contains(out.String(), "syntax error") || !strings.Contains(out.String(), "FAIL p") {
		t.Fatalf("expected build failure to surface, got %s", out.String())
	}
}

func TestRunPassesThroughUnparseableLines(t *testing.T) {
	t.Parallel()
	in := strings.NewReader("go: downloading module x\n")
	var out bytes.Buffer
	if code := run(in, &out, 10*time.Second); code != 0 {
		t.Fatalf("code = %d, output = %s", code, out.String())
	}
	if !strings.Contains(out.String(), "go: downloading module x") {
		t.Fatalf("expected passthrough line, got %s", out.String())
	}
}

func TestBudgetFailsUnmarkedSlowTest(t *testing.T) {
	t.Parallel()
	in := `{"Action":"pass","Package":"p","Test":"TestSlow","Elapsed":1.5}
{"Action":"pass","Package":"p","Test":"TestFast","Elapsed":0.2}
`
	var out bytes.Buffer
	if code := runOpts(strings.NewReader(in), &out, options{hang: time.Minute, budget: time.Second}); code != 1 {
		t.Fatalf("code = %d, output = %s", code, out.String())
	}
	if !strings.Contains(out.String(), "p.TestSlow") || strings.Contains(out.String(), "p.TestFast") {
		t.Fatalf("output = %s", out.String())
	}
	out.Reset()
	if code := run(strings.NewReader(in), &out, time.Minute); code != 0 {
		t.Fatalf("no budget: code = %d, output = %s", code, out.String())
	}
}

func TestSkippedReportListsSlowMarkers(t *testing.T) {
	t.Parallel()
	in := `{"Action":"output","Package":"p","Test":"TestGit","Output":"    a_test.go:12: slow: git-heavy | clones\n"}
{"Action":"skip","Package":"p","Test":"TestGit","Elapsed":0}
{"Action":"output","Package":"p","Test":"TestOther","Output":"    a_test.go:20: needs the game\n"}
{"Action":"skip","Package":"p","Test":"TestOther","Elapsed":0}
`
	path := filepath.Join(t.TempDir(), "skipped.md")
	var out bytes.Buffer
	if code := runOpts(strings.NewReader(in), &out, options{hang: time.Minute, skippedPath: path}); code != 0 {
		t.Fatalf("code = %d, output = %s", code, out.String())
	}
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	s := string(got)
	if !strings.Contains(s, ": 1") || !strings.Contains(s, "`p.TestGit` | git-heavy \\| clones") || strings.Contains(s, "TestOther") {
		t.Fatalf("report = %s", s)
	}
}
