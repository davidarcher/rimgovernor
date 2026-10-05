package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// The flight recorder records the build tier once per change (#604): the
// first known reading, then only a different tier; an unknown tier is silent.
func TestRounderLogsBuildTierOncePerChange(t *testing.T) {
	rows := telemetrytest.Install(t)
	r := &Rounder{}
	reading := func(finished ...policy.ResearchProjectID) observation.ColonyProjection {
		p := observation.ColonyProjection{PlayerTechLevel: domain.Known("Neolithic")}
		p.Facts.Research = domain.Known(policy.ResearchFacts{Finished: finished})
		p.BuildTier = policy.SelectBuildTier(observation.FinishedResearch(p.Facts.Research), p.PlayerTechLevel)
		return p
	}
	ctx := context.Background()
	r.logBuildTier(ctx, observation.ColonyProjection{})
	if len(rows.All()) != 0 {
		t.Fatalf("unknown tier logged: %+v", rows.All())
	}
	r.logBuildTier(ctx, reading())
	r.logBuildTier(ctx, reading())
	r.logBuildTier(ctx, reading("Stonecutting"))
	r.logBuildTier(ctx, reading("Stonecutting"))
	r.logBuildTier(ctx, reading("Stonecutting", "Electricity"))
	got := rows.Of("build_tier")
	want := [][2]string{{"Camp", ""}, {"Masonry", "Stonecutting"}, {"Powered", "Electricity"}}
	if len(got) != len(want) || len(rows.All()) != len(want) {
		t.Fatalf("rows: %+v", rows.All())
	}
	for i, row := range got {
		if row.Payload["tier"] != want[i][0] || row.Payload["evidence"] != want[i][1] {
			t.Fatalf("row %d: %+v", i, row.Payload)
		}
	}
}
