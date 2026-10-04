package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// countingSurvey counts the survey reads.
type countingSurvey struct {
	observation.RoundsSource
	survey policy.MapSurvey
	reads  *int
}

func (n countingSurvey) ReadMapSurvey(context.Context, *c.Identity, policy.Bounds) (policy.MapSurvey, bridge.Result, error) {
	*n.reads++
	return n.survey, bridge.Result{}, nil
}

func openSurvey(n int32) policy.MapSurvey {
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: 1})
		}
	}
	return s
}

// #1290: every layout trigger is hourly. An unchanged colony reads the
// survey at most once an hour and replans nothing; a new pawn or a new
// tier replans at the next hourly review.
func TestLayoutTriggersHourly(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	survey := openSurvey(120)
	reads := 0
	r := &Rounder{player: s.player, native: countingSurvey{survey: survey, reads: &reads}}
	snapshot := s.player.session.State().Snapshot
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load}, Bounds: survey.Bounds}
	projection.Facts.Colonists = domain.Known(int64(3))
	projection.BuildTier = domain.Known(policy.BuildTierCamp)
	review := func(tick domain.Tick) {
		t.Helper()
		projection.Identity.Tick = tick
		if err := r.reviewLayoutPlan(ctx, snapshot, &projection); err != nil {
			t.Fatal(err)
		}
	}
	planTick := func() domain.Tick {
		t.Helper()
		layout, ok, err := r.layoutPlan(ctx, snapshot, projection.Identity.Tick)
		if err != nil || !ok {
			t.Fatal("no plan", err)
		}
		return layout.Tick
	}

	review(100)
	if reads != 1 {
		t.Fatal("no plan derived", reads)
	}
	// Unchanged: the first hourly check replans once (inputs unknown), then
	// the survey is read at most once an hour and replans nothing.
	review(100 + layoutReplanEvery)
	settled := planTick()
	for tick := 200 + layoutReplanEvery; tick < 100+5*layoutReplanEvery; tick += 500 {
		review(tick)
	}
	if reads > 1+4 {
		t.Fatal("unchanged colony read the survey more than hourly", reads)
	}
	if planTick() != settled {
		t.Fatal("unchanged colony replanned")
	}

	// A new pawn replans at the next hourly review.
	projection.Facts.Colonists = domain.Known(int64(4))
	before := reads
	review(100 + 6*layoutReplanEvery)
	if reads != before+1 || r.planPawns != 4 {
		t.Fatal("a new pawn did not replan", reads, r.planPawns)
	}

	// A tier change replans at the next hourly review.
	grown := r.planGrownFor
	projection.BuildTier = domain.Known(policy.BuildTierCamp + 1)
	review(100 + 6*layoutReplanEvery + 100)
	if r.planGrownFor != grown {
		t.Fatal("a tier change replanned before the hour")
	}
	review(100 + 7*layoutReplanEvery)
	if r.planGrownFor == grown {
		t.Fatal("a tier change did not replan")
	}
}

func TestLayoutReasons(t *testing.T) {
	if got := layoutReasons(map[string]bool{"tomb": true, "pawns": true, "terrain": false}); got != "pawns,tomb" {
		t.Fatal(got)
	}
}
