package mood

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// Fixed game time: diagnostic environment overrides cannot shorten this gate.
const recreationTicks uint64 = 10 * 60000

func init() {
	const watch = 2 * time.Hour // Ten days at 100 ticks/s, with a bounded stalled step.
	cases.Register(cases.Case{
		Name:   "mood/recreation",
		Scope:  "Ten quiet baseline days with live needs and all routine families: no colonist ends with a NeedJoy thought below -5; completed Brewing requires the social drug policy assigned.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Quiet:  na.QuietRequired,
		Keep:   []string{string(na.LiveNeeds)},
		Serve:  &cases.ServeSpec{Prefix: "mood-recreation", NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second},
		Budget: watch + 7*time.Minute,
		Reason: "nightly ten-day recreation outcome with live needs and all routine families; exceeds smoke budget",
		Run: func(ctx context.Context, s cases.Session) error {
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{
					Watch: watch, Window: recreationTicks, Poll: 10 * time.Second,
					Goal:     policy.EnsureComfort,
					Extra:    []policy.GoalID{policy.EnsureBasicComfort, policy.EnsureWorkAssignments, policy.EnsureResearch},
					FailFast: sustainedfood.FailFast{Disabled: true},
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					window, ok := report["window"].(*sustainedfood.TickWindow)
					if !ok || !window.Reached {
						return fmt.Errorf("ten-day recreation window not reached: %v", report["window"])
					}
					return auditRecreation(ctx, h, s.Identity(), report)
				},
			})
			return err
		},
	})
}

func auditRecreation(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) error {
	scope := map[string]any{"expectedIdentity": identity}
	research, err := h.Wire(ctx, "recreation-brewing", "observations_read_research", map[string]any{
		"scope": scope, "includeLocked": true, "includeFinished": true, "nameContains": "Brewing",
	})
	if err != nil {
		return err
	}
	report["recreation_research"] = research
	raw, err := json.Marshal(research)
	if err != nil {
		return err
	}
	r := &o.ResearchReply{}
	if err := protojson.Unmarshal(raw, r); err != nil {
		return err
	}
	brewing, err := recreationBrewing(r.GetObserved())
	if err != nil {
		return err
	}
	report["brewing_finished"] = brewing
	listed, err := h.Wire(ctx, "recreation-colonists", "observations_list_pawns", map[string]any{
		"scope": scope, "filter": map[string]any{"colonist": true},
		"details": map[string]any{"social": true, "work": true}, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return err
	}
	report["recreation_colonists"] = listed
	raw, err = json.Marshal(listed)
	if err != nil {
		return err
	}
	p := &o.ListPawnsReply{}
	if err := protojson.Unmarshal(raw, p); err != nil {
		return err
	}
	return checkRecreation(p.GetObserved(), brewing)
}

func recreationComplete(c *o.Completeness, count int) bool {
	return c != nil && c.Matched != nil && c.Returned != nil && c.GetUnreadable() == 0 && c.GetPage().GetComplete() &&
		c.GetMatched() == uint64(count) && c.GetReturned() == uint64(count)
}

func recreationBrewing(r *o.ResearchSnapshot) (bool, error) {
	if r == nil || !recreationComplete(r.Completeness, len(r.Projects)) {
		return false, fmt.Errorf("brewing research census incomplete")
	}
	for _, p := range r.Projects {
		if p.GetProject().GetDefName() == "Brewing" && p.Finished != nil && len(p.Issues) == 0 {
			return p.GetFinished(), nil
		}
	}
	return false, fmt.Errorf("brewing completion unknown")
}

func checkRecreation(snapshot *o.PawnSnapshot, brewing bool) error {
	if snapshot == nil || len(snapshot.Pawns) == 0 || !recreationComplete(snapshot.Completeness, len(snapshot.Pawns)) {
		return fmt.Errorf("recreation colonist census empty or incomplete")
	}
	seen := map[string]bool{}
	for _, pawn := range snapshot.Pawns {
		id := pawn.GetPawn().GetId()
		if id == "" || seen[id] || !pawn.GetColonist() || pawn.Dead == nil || pawn.GetDead() {
			return fmt.Errorf("invalid recreation colonist %q", id)
		}
		seen[id] = true
		social := pawn.GetSocial()
		if social == nil || len(social.Issues) != 0 || len(pawn.Issues) != 0 || social.GetSituationalCacheStale() {
			return fmt.Errorf("colonist %s thoughts unavailable or stale", id)
		}
		for _, rows := range [][]*o.Thought{social.Memories, social.Situational} {
			for _, thought := range rows {
				if thought == nil || thought.DefName == nil || thought.MoodOffsetTotal == nil || math.IsNaN(thought.GetMoodOffsetTotal()) || math.IsInf(thought.GetMoodOffsetTotal(), 0) {
					return fmt.Errorf("colonist %s thought offset unknown", id)
				}
				if thought.GetDefName() == "NeedJoy" && thought.GetMoodOffsetTotal() < -5 {
					return fmt.Errorf("colonist %s NeedJoy offset %g below -5", id, thought.GetMoodOffsetTotal())
				}
			}
		}
		if brewing {
			settings := pawn.GetSettings()
			if settings == nil || len(settings.Issues) != 0 || settings.DrugPolicyName == nil || settings.GetDrugPolicyName() != policy.SocialDrugPolicyName {
				return fmt.Errorf("colonist %s social drug policy not assigned with Brewing complete", id)
			}
		}
	}
	return nil
}
