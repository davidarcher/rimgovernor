package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// Score replay harness: every fixture survey runs Zone,
// SiteCore and Score, and `go test ./internal/policy -run Replay -v` prints
// one row of per-term values and the tier verdict per fixture, so
// planWeights are tuned by evidence. A fixture is one replayFixtures entry.
//
// The baseline survey is the only real one. To capture another, set
// RIMGOVERNOR_SURVEY_DUMP to a gzip path before running startersite against
// a save (testdata/baseline-survey.json.gz), copy
// the file into testdata/ and add an entry that calls loadSurvey. Synthetic
// surveys are built on zoningSurvey and need no live game.

type replayFixture struct {
	name   string
	survey func(testing.TB) MapSurvey
	pawns  int
	// mayFail marks a fixture whose map cannot pass the hard tier (a room
	// does not fit, or every candidate stands on rich soil); the row still
	// prints, but the tier is not asserted.
	mayFail bool
}

func syntheticSurvey(n int32, cell func(x, z int32) SurveyCell) func(testing.TB) MapSurvey {
	return func(testing.TB) MapSurvey { return zoningSurvey(n, cell) }
}

var replayFixtures = []replayFixture{
	{name: "baseline", pawns: 3, survey: func(tb testing.TB) MapSurvey { return loadSurvey(tb, baselineSurveyPath) }},
	{name: "rich-courtyard", pawns: 3, survey: func(testing.TB) MapSurvey { return centreRichSurvey() }},
	{name: "river", pawns: 3, survey: syntheticSurvey(140, func(x, z int32) SurveyCell {
		if x >= 95 && x < 100 {
			return SurveyCell{Footing: FootingNone}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})},
	{name: "mountain-edge", pawns: 3, survey: syntheticSurvey(140, func(x, z int32) SurveyCell {
		if x >= 85 {
			return SurveyCell{Walkable: true, Rock: true, ThickRoof: x >= 100}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})},
	{name: "all-rich-valley", pawns: 3, mayFail: true, survey: syntheticSurvey(140, func(x, z int32) SurveyCell {
		return SurveyCell{Walkable: true, Fertility: 1.4}
	})},
	// A full SiteCore on 300x300 takes ~24 s; the case runs only under
	// the long tier because this whole test does.
	{name: "hundred-colonists", pawns: 100, survey: func(testing.TB) MapSurvey { return plusSurvey(300, 80) }},
	// A closed ice pack: one walkable plate in a ring of open water, no rock.
	{name: "sea-ice", pawns: 3, survey: syntheticSurvey(140, func(x, z int32) SurveyCell {
		if x >= 30 && x < 110 && z >= 30 && z < 110 {
			return SurveyCell{Walkable: true}
		}
		return SurveyCell{Footing: FootingNone}
	})},
	// Irregular terrain: deterministic blobs of water, rock and rich soil.
	{name: "irregular", pawns: 3, survey: syntheticSurvey(140, func(x, z int32) SurveyCell {
		switch h := (x/7*73 + z/5*151 + x/11*z/13) % 11; {
		case h == 0:
			return SurveyCell{Footing: FootingNone}
		case h == 1:
			return SurveyCell{Walkable: true, Rock: true}
		case h == 2:
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})},
	{name: "tiny", pawns: 3, mayFail: true, survey: syntheticSurvey(40, func(x, z int32) SurveyCell {
		return SurveyCell{Walkable: true, Fertility: 1}
	})},
}

func TestReplayScoreFixtures(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	recorded := map[string]replayRow{}
	if !updateReplayScores() {
		recorded = loadReplayScores(t)
	}
	got := map[string]replayRow{}
	for _, f := range replayFixtures {
		t.Run(f.name, func(t *testing.T) {
			s := f.survey(t)
			zones := Zone(s)
			before := Score(siteCore(LayoutPlan{Zones: zones}, s, f.pawns, 1, TechTierCamp, 0), s)
			layoutCounters.routed.Store(0)
			layoutCounters.spineFallbacks.Store(0)
			layoutCounters.rings.Store(0)
			layoutCounters.secondDoors.Store(0)
			plan := SiteCore(LayoutPlan{Zones: zones}, s, f.pawns, 1, TechTierCamp)
			sc := Score(plan, s)
			t.Logf("%-16s unsearched %s", f.name, before)
			if before.Better(sc) {
				t.Fatalf("searched plan scores below the unsearched one: %d < %d", sc.Total(), before.Total())
			}
			t.Logf("%-16s %dx%d rooms=%d %s", f.name, s.Bounds.Width, s.Bounds.Height, len(plan.AllRooms()), sc)
			t.Logf("%-16s hallways=%d rings=%d secondDoors=%d routedEntrances=%d spineFallbacks=%d", f.name, len(plan.Hallways()),
				layoutCounters.rings.Load(), layoutCounters.secondDoors.Load(), layoutCounters.routed.Load(), layoutCounters.spineFallbacks.Load())
			row := newReplayRow(sc, len(plan.AllRooms()))
			got[f.name] = row
			if want, ok := recorded[f.name]; !updateReplayScores() {
				if !ok {
					t.Errorf("no recorded row (RIMGOVERNOR_UPDATE_SNAPSHOT=1 records one)")
				} else if err := checkReplayRow(want, row); err != nil {
					t.Error(err)
				}
			}
			if !f.mayFail && !sc.Passes() {
				t.Fatalf("hard-fail tier: missing=%v routes=%q rich=%d", sc.Missing, sc.RoutesErr, sc.RichCells)
			}
			if sc.RoutesErr == "" && len(plan.AllRooms()) > 0 && !plan.Valid() {
				t.Fatal("plan is not Valid")
			}
			if again := Score(plan, s); again.Total() != sc.Total() {
				t.Fatal("Score is not deterministic")
			}
			for _, r := range plan.AllRooms() {
				for _, c := range rectCells(r.Region.Bounds()) {
					if c.X < 0 || c.Z < 0 || c.X >= s.Bounds.Width || c.Z >= s.Bounds.Height {
						t.Fatal("room cell off the map", r.Role, domain.Cell{X: c.X, Z: c.Z})
					}
				}
			}
		})
	}
	if updateReplayScores() {
		writeReplayScores(t, got)
	}
}
