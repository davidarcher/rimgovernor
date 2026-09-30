package main

import (
	"bytes"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestParseProfile(t *testing.T) {
	root := t.TempDir()
	p, err := parseProfile([]string{"-root", root, "-n", "5", "-equality"}, io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if p.fixture.Op != profileOp || p.fixture.Args["count"] != 5 || p.fixture.Args["equality"] != true || p.Case != "sustained/colony" {
		t.Fatalf("parsed %+v", p)
	}
	if p.fixture.Output != filepath.Join(root, "acceptance", "fixture") {
		t.Fatalf("output %s", p.fixture.Output)
	}
	for _, args := range [][]string{
		{},
		{"-root", "relative"},
		{"-root", root, "-n", "0"},
		{"-root", root, "-save", "x", "-from", "t+7m"},
		{"-root", root, "extra"},
	} {
		if _, err := parseProfile(args, io.Discard); err == nil {
			t.Errorf("%v parsed", args)
		}
	}
}

func TestProfileBundleAndStage(t *testing.T) {
	root := t.TempDir()
	if _, err := profileBundle(root, "sustained/colony", ""); err == nil || !strings.Contains(err.Error(), "no checkpoint ring") {
		t.Fatalf("missing ring: %v", err)
	}
	dir := filepath.Join(root, "checkpoints", "sustained", "colony")
	ring := &na.Ring{Case: "sustained/colony", Entries: []na.Checkpoint{{Label: "t+7m"}, {Label: "t+14m"}}}
	if err := ring.Write(dir); err != nil {
		t.Fatal(err)
	}
	bundle, err := profileBundle(root, "sustained/colony", "")
	if err != nil || bundle != filepath.Join(dir, "t+14m") {
		t.Fatalf("newest: %s %v", bundle, err)
	}
	if bundle, err = profileBundle(root, "sustained/colony", "t+7m"); err != nil || bundle != filepath.Join(dir, "t+7m") {
		t.Fatalf("label: %s %v", bundle, err)
	}
	if _, err = profileBundle(root, "sustained/colony", "t+99m"); err == nil {
		t.Fatal("unknown label resolved")
	}
	os.MkdirAll(bundle, 0755)
	os.WriteFile(filepath.Join(bundle, na.CheckpointSaveName("sustained/colony")+".rws"), []byte("save"), 0644)
	if err := stageProfileSave(root, "sustained/colony", bundle); err != nil {
		t.Fatal(err)
	}
	if data, _ := os.ReadFile(filepath.Join(root, "profile", "Saves", profileSave+".rws")); string(data) != "save" {
		t.Fatalf("staged %q", data)
	}
}

func TestSummarizeProfile(t *testing.T) {
	capture := func(total, cf, upkeep, emergency float64) any {
		return map[string]any{"totalMs": total, "sections": []any{
			map[string]any{"name": "emergency", "ms": emergency, "rows": 3.0, "detail": false},
			map[string]any{"name": "colonyFacts", "ms": cf, "rows": 900.0, "detail": false},
			map[string]any{"name": "cf.upkeep.items", "ms": upkeep, "rows": 400.0, "detail": true},
		}}
	}
	var captures []any
	for i := 1; i <= 10; i++ {
		captures = append(captures, capture(float64(10*i), float64(5*i), float64(i), 0.5))
	}
	reply := map[string]any{"success": true, "tick": 12345.0, "frameBytes": 2048.0, "pawns": 9.0, "things": 1500.0,
		"captures": captures, "equality": map[string]any{"equal": true}}
	s, err := summarizeProfile(reply)
	if err != nil {
		t.Fatal(err)
	}
	if s.Count != 10 || s.Total.P50 != 50 || s.Total.P90 != 90 || s.Total.Max != 100 {
		t.Fatalf("total %+v", s.Total)
	}
	if len(s.Families) != 2 || s.Families[0].Name != "colonyFacts" || s.Families[0].Rows != 900 || s.Families[0].P90 != 45 {
		t.Fatalf("families %+v", s.Families)
	}
	if len(s.Details) != 1 || s.Details[0].Name != "cf.upkeep.items" || !s.Details[0].Detail || s.Details[0].Max != 10 {
		t.Fatalf("details %+v", s.Details)
	}
	var out bytes.Buffer
	printProfile(&out, s)
	for _, want := range []string{"10 captures at tick 12345", "total", "  colonyFacts", "detail spans:", "  cf.upkeep.items", `equality: {"equal":true}`} {
		if !strings.Contains(out.String(), want) {
			t.Errorf("output lacks %q:\n%s", want, out.String())
		}
	}
	if _, err := summarizeProfile(map[string]any{"success": true}); err == nil {
		t.Fatal("empty reply summarized")
	}
}
