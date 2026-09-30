package startersite

import (
	"compress/gzip"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

// Args is ArgsFor(9), the 9x9 fixture hut's site.
func Args(ctx context.Context, h *na.Harness) (map[string]any, error) {
	return ArgsFor(9)(ctx, h)
}

// ArgsFor is a Fixture's ArgsFrom for a size x size fixture hut (#700,
// #709): the south-west corner and door plannedSite puts on the layout
// plan derived from the loaded game's map survey (#1250), as the
// siteX/siteZ/doorX/doorZ arguments FixtureHut takes; an error when the
// plan holds no storeroom.
func ArgsFor(size int32) func(context.Context, *na.Harness) (map[string]any, error) {
	return func(ctx context.Context, h *na.Harness) (map[string]any, error) {
		return args(ctx, h, size)
	}
}

func args(ctx context.Context, h *na.Harness, size int32) (map[string]any, error) {
	reply, _, err := h.Client.Identity(ctx)
	if err != nil {
		return nil, err
	}
	expected, err := observation.DecodeIdentity(reply)
	if err != nil {
		return nil, err
	}
	reading, err := observation.ObserveColony(observation.WithPlanningWindow(ctx, window{h.Client}), h.Client, wallClock{}, expected, time.Minute, true)
	if err != nil {
		return nil, fmt.Errorf("colony facts: %w", err)
	}
	facts := reading.Projection
	survey, _, err := h.Client.ReadMapSurvey(ctx, reply.GetLoaded().GetContext().GetIdentity(), facts.Bounds)
	if err != nil {
		return nil, fmt.Errorf("map survey: %w", err)
	}
	if path := os.Getenv("RIMGOVERNOR_SURVEY_DUMP"); path != "" {
		if err := dumpSurvey(path, survey); err != nil {
			return nil, fmt.Errorf("dump map survey: %w", err)
		}
	}
	pawns, _ := facts.Facts.Colonists.Value()
	tier, _ := facts.BuildTier.Value()
	plan, known := policy.DeriveLayoutPlan(survey, int(pawns), tier, nil).Value()
	if !known {
		return nil, fmt.Errorf("no layout plan on this map")
	}
	site, door, ok := plannedSite(plan, facts.Bounds, size)
	if !ok {
		return nil, fmt.Errorf("no planned storeroom for a %dx%d fixture hut on this map", size, size)
	}
	return map[string]any{"siteX": site.X, "siteZ": site.Z, "doorX": door.X, "doorZ": door.Z}, nil
}

// dumpSurvey writes survey as gzipped JSON, the capture behind the policy
// layout benchmarks' fixture (#1280).
func dumpSurvey(path string, survey policy.MapSurvey) error {
	f, err := os.Create(path)
	if err != nil {
		return err
	}
	defer f.Close()
	z := gzip.NewWriter(f)
	if err := json.NewEncoder(z).Encode(survey); err != nil {
		return err
	}
	return z.Close()
}
