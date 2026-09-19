package main

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestParseRunBreak(t *testing.T) {
	root := absRoot()
	var stderr bytes.Buffer
	_, opts, err := parseRun([]string{"smoke/identity", "-root", root, "-break", "minute=7"}, &stderr)
	if err != nil || opts.Break != (na.Breakpoint{Minute: 7 * time.Minute}) {
		t.Fatalf("%+v %v", opts.Break, err)
	}
	for name, args := range map[string][]string{
		"bad spec":        {"smoke/identity", "-root", root, "-break", "roofed"},
		"with repeat":     {"smoke/identity", "-root", root, "-break", "tick=100", "-repeat", "2"},
		"with postmortem": {"smoke/identity", "-root", root, "-break", "tick=100", "-postmortem-only"},
		"ring off":        {"smoke/identity", "-root", root, "-break", "tick=100", "-checkpoint-every", "0"},
	} {
		var stderr bytes.Buffer
		if _, _, err := parseRun(args, &stderr); err == nil {
			t.Errorf("%s: parseRun(%v) = nil", name, args)
		}
	}
}

// pauseRing writes a ring of the named case under root that records a
// breakpoint, with its break bundle's sidecar.
func pauseRing(t *testing.T, root, name, spec string) {
	t.Helper()
	dir := filepath.Join(root, "checkpoints", filepath.FromSlash(name))
	bundle := filepath.Join(dir, na.BreakCheckpoint)
	if err := os.MkdirAll(bundle, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(bundle, na.CheckpointSidecar), []byte(`{"case":"`+name+`","label":"break"}`), 0644); err != nil {
		t.Fatal(err)
	}
	ring := &na.Ring{Case: name, Next: na.BreakCheckpoint, Entries: []na.Checkpoint{{Case: name, Label: na.BreakCheckpoint, Tick: 500}},
		Break: &na.BreakRecord{Spec: spec, Reason: "test", Tick: 500}}
	if err := ring.Write(dir); err != nil {
		t.Fatal(err)
	}
}

func TestBreakRingsAndDiscard(t *testing.T) {
	root := t.TempDir()
	if paused, err := breakRings(root); err != nil || len(paused) != 0 {
		t.Fatalf("empty root: %v %v", paused, err)
	}
	pauseRing(t, root, "smoke/identity", "minute=7")
	pauseRing(t, root, "light/dark", "stage=lit")
	// A ring without a breakpoint is not listed.
	plain := filepath.Join(root, "checkpoints", "a", "b")
	if err := (&na.Ring{Case: "a/b", Next: "t+7m"}).Write(plain); err != nil {
		t.Fatal(err)
	}
	paused, err := breakRings(root)
	if err != nil || len(paused) != 2 || paused[0].name != "light/dark" || paused[1].name != "smoke/identity" || paused[1].ring.Break.Spec != "minute=7" {
		t.Fatalf("%+v %v", paused, err)
	}
	var out bytes.Buffer
	if err := discardBreaks(root, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "discarded the breakpoint of light/dark (stage=lit)") || !strings.Contains(out.String(), "smoke/identity (minute=7)") {
		t.Fatalf("%s", out.String())
	}
	if paused, _ := breakRings(root); len(paused) != 0 {
		t.Fatalf("still paused: %+v", paused)
	}
	if _, err := os.Stat(filepath.Join(plain, na.CheckpointRingFile)); err != nil {
		t.Fatal("discard removed a ring without a breakpoint")
	}
}

// resume refuses before touching a game: no -root, nothing paused, an
// ambiguous root, a case not paused, and flags that would start over.
func TestResumeRefuses(t *testing.T) {
	root := t.TempDir()
	run := func(args ...string) (int, string) {
		var stdout, stderr bytes.Buffer
		code := resume(context.Background(), args, &stdout, &stderr)
		return code, stderr.String()
	}
	if code, msg := run(); code != 2 || !strings.Contains(msg, "-root is required") {
		t.Fatalf("%d %s", code, msg)
	}
	if code, msg := run("-root", root); code != 2 || !strings.Contains(msg, "nothing is paused") {
		t.Fatalf("%d %s", code, msg)
	}
	pauseRing(t, root, "smoke/identity", "minute=7")
	pauseRing(t, root, "light/dark", "stage=lit")
	if code, msg := run("-root", root); code != 2 || !strings.Contains(msg, "several cases are paused") || !strings.Contains(msg, "light/dark (stage=lit)") {
		t.Fatalf("%d %s", code, msg)
	}
	if code, msg := run("smoke/nope", "-root", root); code != 2 || !strings.Contains(msg, "not paused at a breakpoint") {
		t.Fatalf("%d %s", code, msg)
	}
	if code, msg := run("smoke/identity", "-root", root, "-fresh"); code != 2 || !strings.Contains(msg, "none of -fresh") {
		t.Fatalf("%d %s", code, msg)
	}
}

func TestFlagValue(t *testing.T) {
	for _, args := range [][]string{{"-root", "x"}, {"--root", "x"}, {"-root=x"}, {"-a", "-root", "x", "-b"}} {
		if got := flagValue(args, "root"); got != "x" {
			t.Errorf("%v: %q", args, got)
		}
	}
	if got := flagValue([]string{"-rootless", "y", "-root"}, "root"); got != "" {
		t.Errorf("%q", got)
	}
}
