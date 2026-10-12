package combatlab

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// The recorded targets belong to one staged checkpoint: when a baseline is
// re-recorded on a re-staged world (a new digest) the table is stale and must
// be re-read from the new checkpoint save.
func TestStagedFirstTargetsMatchTheBaselineCheckpoint(t *testing.T) {
	for name, tab := range stagedFirstTargetTable {
		base, err := Baseline(name)
		if err != nil {
			t.Fatal(err)
		}
		for _, row := range base.Rows {
			if row.StagedDigest != tab.digest {
				t.Errorf("%s seed %d resumed checkpoint %s, the staged targets were read from %s: re-read the checkpoint save's Stance_Warmup focusTarg", name, row.Seed, row.StagedDigest, tab.digest)
			}
		}
		f, err := Build(name, 0, 0)
		if err != nil {
			t.Fatal(err)
		}
		hostiles, colonists := 0, 0
		for _, p := range f.Pawns {
			if p.Side == Hostile {
				hostiles++
			} else if p.Side == Colonist {
				colonists++
			}
		}
		if len(tab.aim) != hostiles {
			t.Errorf("%s has %d hostiles, the table %d", name, hostiles, len(tab.aim))
		}
		for h, c := range tab.aim {
			if c < 0 || c >= colonists {
				t.Errorf("%s hostile %d aims at colonist %d of %d", name, h, c, colonists)
			}
		}
	}
}

// The import carries the table to the raiders, and a raider's tick-0 decision
// is its staged target.
func TestSplitRangedRaidersStartAimedAtTheirStagedTargets(t *testing.T) {
	cx, cz := labCenter()
	fight, err := importFight(recordedcatalog.Catalog(t), "lab-split-ranged", cx, cz)
	if err != nil {
		t.Fatal(err)
	}
	got := fight.EnemyAI.FirstTargets
	want := map[domain.PawnID]domain.PawnID{"h0": "c0", "h1": "c1", "h2": "c1", "h3": "c2", "h4": "c1", "h5": "c2", "h6": "c0", "h7": "c1"}
	if len(got) != len(want) {
		t.Fatalf("first targets %v, want %v", got, want)
	}
	for h, c := range want {
		if got[h] != c {
			t.Errorf("%s aims at %s, want %s", h, got[h], c)
		}
	}
}
