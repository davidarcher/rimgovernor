package main

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestParseDevResolvesCaseAndModule(t *testing.T) {
	root := absRoot()
	var stderr bytes.Buffer
	d, err := parseDev([]string{"upkeep/campaign", "-root", root, "-from", "t+7m", "-watch"}, &stderr)
	if err != nil {
		t.Fatalf("parseDev: %v (%s)", err, stderr.String())
	}
	if d.c.Name != "upkeep/campaign" || !d.watch || d.opts.From != "t+7m" || !d.opts.Dev {
		t.Fatalf("resolved %+v", d)
	}
	if d.opts.Rimgovernor != filepath.Join(root, "dev", "rimgovernor"+exeSuffix()) {
		t.Errorf("binary %s", d.opts.Rimgovernor)
	}
	if _, err := os.Stat(filepath.Join(d.module, "go.mod")); err != nil {
		t.Errorf("module %s: %v", d.module, err)
	}
	if d.opts.Output != filepath.Join(root, "acceptance") {
		t.Errorf("output %s", d.opts.Output)
	}
}

func TestParseDevRejects(t *testing.T) {
	root := absRoot()
	for _, tc := range []struct {
		args []string
		want string
	}{
		{[]string{"-root", root}, "exactly one case name"},
		{[]string{"a/b", "c/d", "-root", root}, "exactly one case name"},
		{[]string{"smoke/identity"}, "-root is required"},
		{[]string{"nope/nope", "-root", root}, "unknown case"},
		{[]string{"smoke/identity", "-root", root, "-module", t.TempDir()}, "holds no go.mod"},
		{[]string{"smoke/identity", "-root", root, "-from"}, "flag needs an argument"},
	} {
		var stderr bytes.Buffer
		_, err := parseDev(tc.args, &stderr)
		if err == nil || !strings.Contains(err.Error()+stderr.String(), tc.want) {
			t.Errorf("%v: want %q, got %v (%s)", tc.args, tc.want, err, stderr.String())
		}
	}
}

func TestNewestGoFileAndWaitForChange(t *testing.T) {
	module := t.TempDir()
	if newestGoFile(module).IsZero() != true {
		t.Fatal("empty module has a newest file")
	}
	file := filepath.Join(module, "a.go")
	if err := os.WriteFile(file, []byte("package a"), 0644); err != nil {
		t.Fatal(err)
	}
	old := time.Now().Add(-time.Hour)
	if err := os.Chtimes(file, old, old); err != nil {
		t.Fatal(err)
	}
	if got := newestGoFile(module); !got.Equal(old) {
		t.Fatalf("newest %v, want %v", got, old)
	}
	// A non-Go file does not count.
	if err := os.WriteFile(filepath.Join(module, "a.txt"), []byte("x"), 0644); err != nil {
		t.Fatal(err)
	}
	if got := newestGoFile(module); !got.Equal(old) {
		t.Fatalf("txt counted: %v", got)
	}

	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, ok := waitForChange(ctx, module, old); ok {
		t.Error("a cancelled wait reported a change")
	}
}
