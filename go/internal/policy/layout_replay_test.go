package policy

import "testing"

// Replay fixtures: the surveys the tiled-plan tests run over. A fixture is one
// replayFixtures entry.
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
	// The case runs only under the long tier because this whole test
	// does.
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
}

// centreRichSurvey: a rich patch at the map centre, plain soil around it.
func centreRichSurvey() MapSurvey {
	return zoningSurvey(140, func(x, z int32) SurveyCell {
		if x >= 50 && x < 90 && z >= 50 && z < 90 {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
}
