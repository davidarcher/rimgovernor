package main

import (
	"bytes"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func absRoot() string {
	if runtime.GOOS == "windows" {
		return `C:\bridge`
	}
	return "/bridge"
}

func TestParseRunResolvesFlagsAndCases(t *testing.T) {
	root := absRoot()
	var stderr bytes.Buffer
	selected, opts, err := parseRun([]string{"smoke/identity", "-root", root, "-budget", "4m", "-stall", "90s", "-headless=false"}, &stderr)
	if err != nil {
		t.Fatalf("parseRun: %v (%s)", err, stderr.String())
	}
	if len(selected) != 1 || selected[0].Name != "smoke/identity" {
		t.Fatalf("selected = %v", selected)
	}
	if opts.Root != root || opts.Output != filepath.Join(root, "acceptance") {
		t.Fatalf("root/output = %q/%q", opts.Root, opts.Output)
	}
	if opts.Budget != 4*time.Minute || opts.Stall != 90*time.Second || opts.Headless || opts.GameID != "rimgovernor-trial" {
		t.Fatalf("opts = %+v", opts)
	}
	if got := opts.CaseOutput(selected[0]); got != filepath.Join(root, "acceptance", "smoke", "identity") {
		t.Fatalf("CaseOutput = %q", got)
	}
}

func TestParseRunRejects(t *testing.T) {
	root := absRoot()
	for name, args := range map[string][]string{
		"no case":         {"-root", root},
		"unknown case":    {"smoke/nope", "-root", root},
		"missing root":    {"smoke/identity"},
		"relative root":   {"smoke/identity", "-root", "bridge"},
		"case after flag": {"-root", root, "smoke/identity"},
		"unknown flag":    {"smoke/identity", "-root", root, "-bogus"},
	} {
		var stderr bytes.Buffer
		if _, _, err := parseRun(args, &stderr); err == nil {
			t.Errorf("%s: parseRun(%v) = nil", name, args)
		}
	}
}

func TestListPrintsRegistry(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := run([]string{"list"}, &stdout, &stderr); code != 0 {
		t.Fatalf("list exit %d: %s", code, stderr.String())
	}
	if !strings.Contains(stdout.String(), "smoke/identity\t") {
		t.Fatalf("list output: %q", stdout.String())
	}
	if code := run(nil, &stdout, &stderr); code != 2 {
		t.Fatalf("no command exit %d", code)
	}
}
