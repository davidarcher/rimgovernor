package main

import (
	"bytes"
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
