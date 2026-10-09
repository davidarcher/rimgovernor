package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "wall/tier-gate",
		Scope: "Native material delivery by construction tier (#2523): with five steel, a tier-0 steel wall is handed the delivery job and a tier-5 steel wall is not; a tier-5 wood wall and an untiered steel wall are unaffected; " +
			"set-tier to 0 lets the tier-5 site compete; an unreachable tier-0 site does not block; with the supervisor inactive nothing is gated. A Go snapshot cannot prove the Harmony patch on the vanilla delivery work-giver.",
		Start: cases.LabStart(), RequiredOps: []string{"test/construction_tier_gate"},
		Budget: 3 * time.Minute, Crew: cases.Crew{Size: 3},
		Run: runTierGate,
	})
}

func runTierGate(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	call := func(label, action string) (map[string]any, error) {
		row, err := h.Call(ctx, label, "test/construction_tier_gate", map[string]any{"action": action})
		if err != nil {
			return nil, err
		}
		if ok, _ := na.AsBool(row["success"]); !ok {
			return nil, fmt.Errorf("construction_tier_gate %s refused: %#v", action, row)
		}
		s.Report()[label] = row
		return row, nil
	}
	// Each site reports the job the delivery work-giver hands the one builder, or none.
	hasJob := func(row map[string]any, site string) bool {
		m, _ := na.AsMap(row[site])
		return m["job"] == "HaulToContainer"
	}
	expect := func(label string, row map[string]any, want [4]bool) error {
		for i, site := range []string{"a", "b", "c", "d"} {
			if hasJob(row, site) != want[i] {
				return fmt.Errorf("%s: site %s delivery job = %v, want %v: %#v", label, site, hasJob(row, site), want[i], row)
			}
		}
		return nil
	}
	prepared, err := call("tier-prepare", "prepare")
	if err != nil {
		return err
	}
	sites, _ := prepared["sites"].([]any)
	if len(sites) != 4 {
		return fmt.Errorf("expected four staged sites: %#v", prepared)
	}
	bSite, _ := na.AsMap(sites[1])

	inactive, err := call("tier-inactive", "query")
	if err != nil {
		return err
	}
	if active, _ := na.AsBool(inactive["supervisorActive"]); active {
		return fmt.Errorf("supervisor must be inactive before authority is granted: %#v", inactive)
	}
	if err = expect("inactive supervisor gates nothing", inactive, [4]bool{true, true, true, true}); err != nil {
		return err
	}

	id := s.Identity()
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "tier-gate-authority", id); err != nil {
		return err
	}
	gated, err := call("tier-gated", "query")
	if err != nil {
		return err
	}
	if active, _ := na.AsBool(gated["supervisorActive"]); !active {
		return fmt.Errorf("supervisor not active: %#v", gated)
	}
	// Tier 0 steel first; tier 5 steel waits; wood and untiered are free.
	if err = expect("tier 0 first", gated, [4]bool{true, false, true, true}); err != nil {
		return err
	}

	setTier := func(label string, tier int) error {
		placement := map[string]any{"defName": "Wall", "stuff": "Steel", "x": bSite["x"], "z": bSite["z"], "rotation": "ROTATION_NORTH"}
		out, e := na.ApplyOne(ctx, h, label, id, label, map[string]any{"building": map[string]any{"placement": placement, "tier": tier, "existingTargetId": bSite["target"]}})
		if e != nil {
			return e
		}
		if _, ok := na.AsMap(out["applied"]); !ok {
			return fmt.Errorf("%s not applied: %#v", label, out)
		}
		return nil
	}
	if err = setTier("tier-raise", 0); err != nil {
		return err
	}
	equal, err := call("tier-equal", "query")
	if err != nil {
		return err
	}
	if err = expect("equal tiers compete", equal, [4]bool{true, true, true, true}); err != nil {
		return err
	}

	if err = setTier("tier-lower-again", 5); err != nil {
		return err
	}
	enclosed, err := call("tier-enclosed", "enclose")
	if err != nil {
		return err
	}
	a, _ := na.AsMap(enclosed["a"])
	if reachable, _ := na.AsBool(a["reachable"]); reachable {
		return fmt.Errorf("enclosed tier-0 site must be unreachable: %#v", enclosed)
	}
	return expect("an unreachable tier-0 site does not block", enclosed, [4]bool{false, true, true, true})
}
