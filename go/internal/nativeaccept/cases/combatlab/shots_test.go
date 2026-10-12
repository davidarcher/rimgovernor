package combatlab

import (
	"bytes"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
)

// tags lists a row struct's json field names in order.
func tags(v any) []string {
	t := reflect.TypeOf(v)
	var out []string
	for i := 0; i < t.NumField(); i++ {
		out = append(out, strings.Split(t.Field(i).Tag.Get("json"), ",")[0])
	}
	return out
}

// TestShotRowSchema pins the fields of every row kind: the C# tap
// (LabShotLedger) writes them, the committed datasets and README describe
// them. Change all together.
func TestShotRowSchema(t *testing.T) {
	want := map[string][]string{
		KindShot:   {"seed", "k", "tick", "id", "shooter", "side", "x", "z", "target", "tx", "tz", "dx", "dz", "burst", "of", "weapon", "proj", "tti"},
		KindImpact: {"seed", "k", "tick", "id", "hit", "x", "z", "dealt"},
		KindJob:    {"seed", "k", "tick", "pawn", "side", "job", "forced", "thing", "x", "z"},
		KindStance: {"seed", "k", "tick", "pawn", "side", "stance", "left", "thing", "x", "z"},
		KindDraft:  {"seed", "k", "tick", "pawn", "drafted"},
		KindThink:  {"seed", "k", "pawn", "ticks", "count"},
	}
	got := map[string][]string{
		KindShot: tags(Shot{}), KindImpact: tags(Impact{}), KindJob: tags(Job{}),
		KindStance: tags(Stance{}), KindDraft: tags(Draft{}), KindThink: tags(Think{}),
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("row schema changed:\n got %v\nwant %v", got, want)
	}
}

func TestParseShotsIsStrict(t *testing.T) {
	good := `{"k":"shot","tick":5,"id":9,"shooter":"Human1","side":"colonist","x":1,"z":2,"target":"Human2","tx":3,"tz":4,"dx":3,"dz":4,"burst":0,"of":1,"weapon":"Gun_BoltActionRifle","proj":"Bullet_BoltActionRifle","tti":4}
{"k":"impact","tick":9,"id":9,"hit":"Human2","x":3,"z":4}
{"k":"think","pawn":"Human1","ticks":[3,18],"count":2}
`
	set, err := ParseShots(strings.NewReader(good))
	if err != nil {
		t.Fatal(err)
	}
	if len(set.Shots) != 1 || len(set.Impacts) != 1 || len(set.Thinks) != 1 || set.Shots[0].TTI != 4 {
		t.Fatalf("parsed %+v", set)
	}
	for _, bad := range []string{`{"k":"mystery","tick":1}`, `{"k":"impact","tick":1,"id":1,"hit":"","x":0,"z":0,"extra":1}`, `not json`} {
		if _, err := ParseShots(strings.NewReader(bad)); err == nil {
			t.Errorf("%q parsed", bad)
		}
	}
}

func TestStampShotsAddsSeedAndDealt(t *testing.T) {
	var out bytes.Buffer
	bundle := []byte(`{"k":"shot","tick":5,"id":9,"shooter":"Human1","side":"colonist","x":1,"z":2,"target":"Human2","tx":3,"tz":4,"dx":3,"dz":4,"burst":0,"of":1,"weapon":"w","proj":"p","tti":4}
{"k":"draft","tick":6,"pawn":"Human1","drafted":true}
{"k":"impact","tick":9,"id":9,"hit":"Human2","x":3,"z":4}
{"k":"impact","tick":9,"id":10,"hit":"","x":3,"z":5}
`)
	damage := []combatmetrics.Hit{
		{Tick: 9, Victim: "Human2", Instigator: "Human1", Dealt: 3},
		{Tick: 9, Victim: "Human2", Instigator: "Human1", Dealt: 2},
		{Tick: 9, Victim: "Human2", Instigator: "Human3", Dealt: 7},
		{Tick: 10, Victim: "Human2", Instigator: "Human1", Dealt: 7},
	}
	if err := StampShots(&out, 7, bundle, damage); err != nil {
		t.Fatal(err)
	}
	set, err := ParseShots(&out)
	if err != nil {
		t.Fatal(err)
	}
	if set.Shots[0].Seed != 7 || set.Drafts[0].Seed != 7 || set.Impacts[0].Dealt != 5 || set.Impacts[1].Dealt != 0 {
		t.Fatalf("stamped %+v", set)
	}
}

// TestCommittedShotDatasets parses every committed dataset strictly and
// checks it covers seeds 1..BaselineSeeds, each with shots from both sides
// and a think row per pawn, and that every impact belongs to a launch.
func TestCommittedShotDatasets(t *testing.T) {
	files, err := filepath.Glob(filepath.Join("calibration", "shots", "*.jsonl"))
	if err != nil || len(files) == 0 {
		t.Fatalf("no committed shot dataset (%v)", err)
	}
	for _, file := range files {
		data, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		set, err := ParseShots(bytes.NewReader(data))
		if err != nil {
			t.Fatalf("%s: %v", file, err)
		}
		sides := map[int]map[string]int{}
		launched := map[[2]int]bool{}
		for _, s := range set.Shots {
			if sides[s.Seed] == nil {
				sides[s.Seed] = map[string]int{}
			}
			sides[s.Seed][s.Side]++
			launched[[2]int{s.Seed, s.ID}] = true
			if s.Tick <= 0 || s.Of < 1 || s.Burst < 0 || s.Burst >= s.Of {
				t.Errorf("%s: seed %d shot %+v is not plausible", file, s.Seed, s)
			}
		}
		for seed := 1; seed <= BaselineSeeds; seed++ {
			if sides[seed]["colonist"] == 0 || sides[seed]["hostile"] == 0 {
				t.Errorf("%s: seed %d has shots %v, want both sides", file, seed, sides[seed])
			}
		}
		for _, im := range set.Impacts {
			if !launched[[2]int{im.Seed, im.ID}] {
				t.Errorf("%s: seed %d impact of projectile %d has no launch", file, im.Seed, im.ID)
			}
		}
		if len(set.Thinks) == 0 || len(set.Jobs) == 0 {
			t.Errorf("%s: no think or job rows", file)
		}
	}
}
