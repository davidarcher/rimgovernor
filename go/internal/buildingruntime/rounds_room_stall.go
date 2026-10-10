package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// roomBuildStallTicks is how long a room's blueprints or frames may stand
// unfinished before the planner reports the stall, and the spacing of the
// repeats while it lasts.
const roomBuildStallTicks domain.Tick = 30000

// roomStall remembers since when a room has had unfinished sites and when the
// stall was last reported. Derived, in memory only.
type roomStall struct{ since, reported domain.Tick }

// roomSites is the room's blueprints and frames still standing on its ground.
func roomSites(sites []policy.ConstructionSite, ground policy.Rectangle) []policy.ConstructionSite {
	var out []policy.ConstructionSite
	for _, s := range sites {
		c := s.Building.Cell()
		if c.X >= ground.X && c.X < ground.X+ground.Width && c.Z >= ground.Z && c.Z < ground.Z+ground.Height {
			out = append(out, s)
		}
	}
	return out
}

// siteReason is the first reason the census gives for a site not finishing.
func siteReason(s policy.ConstructionSite) string {
	if s.FinishingBlocker != "" {
		return s.FinishingBlocker
	}
	if done, known := s.ResourcesComplete.Value(); known && !done {
		return "awaiting_materials"
	}
	if n, known := s.EligibleFinishers.Value(); known && n == 0 {
		return "no_eligible_builder"
	}
	return "unreported"
}

// siteCounts counts the sites by stage and by reason.
func siteCounts(sites []policy.ConstructionSite) (blueprints, frames int, reasons map[string]int) {
	reasons = map[string]int{}
	for _, s := range sites {
		if s.Stage == "frame" {
			frames++
		} else {
			blueprints++
		}
		reasons[siteReason(s)]++
	}
	return blueprints, frames, reasons
}

// dominantReason is the most common reason, ties broken by name.
func dominantReason(reasons map[string]int) string {
	best := ""
	for r, n := range reasons {
		if best == "" || n > reasons[best] || n == reasons[best] && r < best {
			best = r
		}
	}
	return best
}

// awaitingKey names what a room's wait is on: the stage counts and the
// dominant reason; empty when no site stands.
func awaitingKey(sites []policy.ConstructionSite) string {
	if len(sites) == 0 {
		return ""
	}
	blueprints, frames, reasons := siteCounts(sites)
	var parts []string
	if blueprints > 0 {
		parts = append(parts, fmt.Sprintf("%d_blueprints", blueprints))
	}
	if frames > 0 {
		parts = append(parts, fmt.Sprintf("%d_frames", frames))
	}
	return "awaiting_build:" + strings.Join(parts, ",") + ":" + dominantReason(reasons)
}

// noteStall tracks the room's unfinished sites and reports a stall once they
// have stood roomBuildStallTicks, again every window after.
func (r *Rounder) noteStall(ctx context.Context, name string, now domain.Tick, sites []policy.ConstructionSite, obstructions []string) {
	if len(sites) == 0 && len(obstructions) == 0 {
		delete(r.roomStalls, name)
		return
	}
	if r.roomStalls == nil {
		r.roomStalls = map[string]roomStall{}
	}
	st, tracked := r.roomStalls[name]
	if !tracked {
		r.roomStalls[name] = roomStall{since: now}
		return
	}
	if now-st.since < roomBuildStallTicks || st.reported != 0 && now-st.reported < roomBuildStallTicks {
		return
	}
	st.reported = now
	r.roomStalls[name] = st
	blueprints, frames, reasons := siteCounts(sites)
	tiers := map[string]int{}
	eligible := 0
	skill := 0
	for _, s := range sites {
		if tier, known := s.Tier.Value(); known {
			tiers[fmt.Sprint(int(tier))]++
		}
		if n, known := s.EligibleFinishers.Value(); known {
			eligible += n
		}
		if m, known := s.MinimumFinishingSkill.Value(); known && m > skill {
			skill = m
		}
	}
	names := make([]string, 0, len(sites))
	for _, s := range sites {
		names = append(names, fmt.Sprintf("%s@%d,%d", s.Building.Definition(), s.Building.Cell().X, s.Building.Cell().Z))
	}
	sort.Strings(names)
	if len(names) > 8 {
		names = names[:8]
	}
	telemetry.Decide(ctx, telemetry.Decision{Kind: "build_stalled", Component: "building-planner", Level: slog.LevelWarn, Verdict: "waiting", Reason: stallReason(reasons, obstructions), Target: name,
		Attrs: map[string]any{"blueprints": blueprints, "frames": frames, "reasons": reasons, "tiers": tiers, "eligible_finishers": eligible, "max_min_skill": skill, "stalled_ticks": int64(now - st.since), "sites": names, "obstructions": obstructions}})
}

// stallReason is the most common site reason, or "obstructed" when only
// foreign things on the room's cells hold it.
func stallReason(reasons map[string]int, obstructions []string) string {
	if len(reasons) == 0 && len(obstructions) > 0 {
		return "obstructed"
	}
	return dominantReason(reasons)
}

// roomObstructions names the foreign things the room's pending claim, cut and
// haul operations stand on, e.g. Steel(40)@10,10: what a door or wall cell is
// waiting to have cleared.
func roomObstructions(ops []policy.Operation) []string {
	var out []string
	for _, op := range ops {
		if op.Kind != policy.OpClaim && op.Kind != policy.OpCut && op.Kind != policy.OpHaulOut {
			continue
		}
		for _, t := range op.Targets {
			count := ""
			if t.Count > 0 {
				count = fmt.Sprintf("(%d)", t.Count)
			}
			out = append(out, fmt.Sprintf("%s:%s%s@%d,%d", op.Kind, t.DefName, count, t.Minimum.X, t.Minimum.Z))
		}
	}
	return out
}
