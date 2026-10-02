package snapshot

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRerecordPicksTheClosestReviewOfTheCase(t *testing.T) {
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()
	caseDir := filepath.Join(root, "shard-1", "fixture", "snapshots", "clean", "filthy")
	other := filepath.Join(root, "shard-1", "fixture", "snapshots", "clean", "other")
	for _, d := range []string{caseDir, other} {
		for _, tick := range []domain.Tick{100, 200, 300} {
			v := r
			v.Tick = tick
			if err := Record(d, v); err != nil {
				t.Fatal(err)
			}
		}
	}
	streams, err := CaseStreams(root, "clean/filthy")
	if err != nil || len(streams) != 1 || filepath.Dir(streams[0]) != caseDir {
		t.Fatal("want the case's one stream:", streams, err)
	}
	for _, c := range []struct{ tick, want int64 }{{200, 200}, {150, 200}, {900, 300}} {
		_, got, step, err := Pick(Recording{Out: "x", Case: "clean/filthy", Tick: c.tick}, streams)
		if err != nil || step != "" || int64(got.Tick) != c.want {
			t.Errorf("tick %d picked %v %q %v, want %d", c.tick, got, step, err, c.want)
		}
	}

	out := filepath.Join(t.TempDir(), "kitchen.json.gz")
	stream, got, _, _ := Pick(Recording{Case: "clean/filthy", Tick: 150}, streams)
	if err := TrimTo(stream, out, false, int64(got.Tick), got.Seq, ""); err != nil {
		t.Fatal(err)
	}
	if back, err := Load(out); err != nil || back.Tick != 200 {
		t.Fatal("trimmed review does not load:", err)
	}
}

func TestRegisterReplacesTheSameOut(t *testing.T) {
	path := filepath.Join(t.TempDir(), "recordings.json")
	if err := Register(path, Recording{Out: "b.json.gz", Case: "a/b", Tick: 1}); err != nil {
		t.Fatal(err)
	}
	if err := Register(path, Recording{Out: "a.json.gz", Case: "a/a", Step: "step-building-MaintainShelter-5-1"}); err != nil {
		t.Fatal(err)
	}
	if err := Register(path, Recording{Out: "b.json.gz", Case: "a/b", Tick: 2}); err != nil {
		t.Fatal(err)
	}
	recs, err := LoadRecordings(path)
	if err != nil || len(recs) != 2 || recs[0].Out != "a.json.gz" || recs[1].Tick != 2 {
		t.Fatal(recs, err)
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatal(err)
	}
	if p, g, tick, ok := splitStep(recs[0].Step); !ok || p != "building" || g != "MaintainShelter" || tick != 5 {
		t.Fatal(p, g, tick, ok)
	}
}
