package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// The service log records the build tier once per change (#604): the first
// known reading, then only a different tier; an unknown tier is silent.
func TestRoutineReviewerLogsBuildTierOncePerChange(t *testing.T) {
	var out bytes.Buffer
	previous := slog.Default()
	slog.SetDefault(telemetry.New(&out, slog.LevelInfo, nil))
	defer slog.SetDefault(previous)
	r := &RoutineReviewer{}
	reading := func(finished ...policy.ResearchProjectID) observation.ColonyProjection {
		p := observation.ColonyProjection{PlayerTechLevel: domain.Known("Neolithic")}
		p.Facts.Research = domain.Known(policy.ResearchFacts{Finished: finished})
		p.BuildTier = policy.SelectBuildTier(observation.FinishedResearch(p.Facts.Research), p.PlayerTechLevel)
		return p
	}
	ctx := context.Background()
	r.logBuildTier(ctx, observation.ColonyProjection{})
	if out.Len() != 0 {
		t.Fatalf("unknown tier logged: %s", out.String())
	}
	r.logBuildTier(ctx, reading())
	r.logBuildTier(ctx, reading())
	r.logBuildTier(ctx, reading("Stonecutting"))
	r.logBuildTier(ctx, reading("Stonecutting"))
	r.logBuildTier(ctx, reading("Stonecutting", "Electricity"))
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 3 || !strings.Contains(lines[0], "[layout] build tier Camp") || !strings.Contains(lines[1], "[layout] build tier Masonry (Stonecutting)") || !strings.Contains(lines[2], "[layout] build tier Powered (Electricity)") {
		t.Fatalf("log:\n%s", out.String())
	}
}
