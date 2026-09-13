package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// HomeCoverageTarget refreshes one exact Home coverage row (shape token,
// global revision and missing/excluded counts) immediately before dispatch.
// Unlike beds or repairable structures, Home coverage has no exact-ID lookup
// RPC and no per-target CAS token (see bridge/home_coverage.go): native
// recomputes the census-wide revision and each target's shape hash from
// current geometry, so this reuses the general ReadColonyFacts upkeep
// projection the routine review pass already consumes
// (observation.colonyHomeCoverage), matching by target ID within the same
// fresh census the general upkeep facts deliberately keep unscoped.
type HomeCoverageTarget struct {
	Context  *c.ObservationContext
	Target   string
	Shape    string
	Revision int64
	Missing  int64
	Excluded int64
}

// ReadHomeCoverageTarget observes the full colony upkeep census and matches
// the requested target by ID, refreshing its shape token, the global
// revision and its missing/excluded counts.
func (client *Client) ReadHomeCoverageTarget(ctx context.Context, identity *c.Identity, target string) (HomeCoverageTarget, Result, error) {
	if validID(target) != nil {
		return HomeCoverageTarget{}, Result{}, contract("invalid home coverage target identity")
	}
	reply, raw, err := client.ReadColonyFacts(ctx, identity, false, nil)
	if err != nil {
		return HomeCoverageTarget{}, raw, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return HomeCoverageTarget{}, raw, contract("home coverage target census missing")
	}
	h := observed.GetUpkeep().GetObserved().GetHomeCoverage().GetObserved()
	if h == nil {
		return HomeCoverageTarget{}, raw, contract("home coverage census unavailable")
	}
	if h.Revision == nil || h.GetRevision() < 0 {
		return HomeCoverageTarget{}, raw, contract("invalid home coverage revision")
	}
	var found *HomeCoverageTarget
	for _, row := range h.Targets {
		if row == nil || row.GetId() != target {
			continue
		}
		if found != nil {
			return HomeCoverageTarget{}, raw, contract("ambiguous home coverage target")
		}
		if row.ShapeToken == nil || row.MissingCells == nil || row.ExcludedCells == nil {
			return HomeCoverageTarget{}, raw, contract("home coverage target facts incomplete")
		}
		found = &HomeCoverageTarget{Context: observed.Context, Target: target, Shape: row.GetShapeToken(), Revision: h.GetRevision(), Missing: int64(row.GetMissingCells()), Excluded: int64(row.GetExcludedCells())}
	}
	if found == nil {
		return HomeCoverageTarget{}, raw, contract("home coverage target missing")
	}
	return *found, raw, nil
}
