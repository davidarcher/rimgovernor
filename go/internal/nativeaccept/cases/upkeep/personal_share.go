package upkeep

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// upkeep/personal-share-rich and upkeep/personal-share-poor (#1847, epic
// #1829): the same bare greedy bedroom and the same gear staging run twice,
// once in a colony stocked with gold and food and once in one stripped of
// every loose item but wood and food (a poor colony; the equipment goal needs Stable, so it is not Foothold). Each case asserts its own
// absolute outcome against the shared thresholds below, so the pair diverges
// only through the colonist's personal wealth share. Each game runs once.
//
// Staged on the first colonist (the greedy bedroom's owner): a shirt, pants
// and a flak vest worn, an excellent plate armor on the ground, and a bare
// bedroom. The second colonist is stripped of all apparel with a basic shirt
// and pants on the ground (a necessity, never charged).
func init() {
	for _, c := range []struct {
		name, wealth, scope, reason string
	}{
		{"upkeep/personal-share-rich", "rich",
			"Personal wealth shares (#1829): in a colony rich in items the greedy colonist's bedroom reaches the shared impressiveness stage and the plate armor replaces the flak vest, while the bare colonist is dressed.",
			"Bedroom furniture and floor builds, a gear swap, then native room stats and worn apparel."},
		{"upkeep/personal-share-poor", "poor",
			"Personal wealth shares (#1829): in a poor colony the same bedroom stays under the shared stage and the flak vest is kept, yet the bed stays owned and the bare colonist is dressed (necessities are never charged).",
			"Same staging as the rich case, with a settle wait after the standards recover to show the gates hold."},
	} {
		wealth := c.wealth
		sleeping := scenarios()["sleeping"]
		cases.Register(cases.Case{
			Name:        c.name,
			Scope:       c.scope,
			Start:       cases.LabStart(),
			RequiredOps: []string{"test/sleeping_setup", "test/sleeping_greedy_status", "test/gear_fixture"},
			Keep:        sleeping.keep,
			Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Sleeping, routinefamily.Flooring, routinefamily.Gear, routinefamily.Work}, Extra: sleeping.extra, Prefix: prefix},
			Budget:      30 * time.Minute,
			Reason:      c.reason,
			Run:         func(ctx context.Context, s cases.Session) error { return runPersonalShare(ctx, s, wealth) },
		})
	}
}

// Shared thresholds: the rich colony must reach both, the poor one neither.
const (
	// shareArmorRich is the armor rung the rich colonist wears: plate armor
	// over a flak vest (rung 1).
	shareArmorRich = 2
	// sharePoorSettle is how long the poor colony runs after its goals
	// recover, so a gate that leaked would have spent by then.
	sharePoorSettle = 3 * time.Minute
)

// shareArmorRung ranks the torso armor the fixture offers.
var shareArmorRung = map[string]int{"Apparel_FlakVest": 1, "Apparel_PlateArmor": shareArmorRich}

func runPersonalShare(ctx context.Context, s cases.Session, wealth string) error {
	report, identity := s.Report(), s.Identity()
	h := s.Harness()
	prepared, err := callFixture(ctx, h, identity, "test/sleeping_setup", map[string]any{"greedy": true})
	if err != nil {
		return err
	}
	report["prepared"] = prepared
	pawn := na.AsString(prepared["pawn"])
	if pawn == "" || na.AsString(prepared["greedyBed"]) == "" {
		return fmt.Errorf("fixture returned no greedy bedroom: %#v", prepared)
	}
	staged, err := callFixture(ctx, h, identity, "test/gear_fixture", map[string]any{"mode": "share_setup", "pawn": pawn, "wealth": wealth})
	if err != nil {
		return err
	}
	report["staged"] = staged
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	journal, err := serveStage(ctx, service, report)
	if err == nil {
		recoverCtx, cancel := context.WithTimeout(ctx, 25*time.Minute)
		for _, need := range []policy.ConcernID{policy.MaintainHousing, policy.MaintainEquipment} {
			_, werr := waitNeed(recoverCtx, journal, need, domain.FindingMet)
			if werr != nil {
				err = werr
				break
			}
		}
		cancel()
	}
	if err == nil && wealth == "poor" {
		select {
		case <-time.After(sharePoorSettle):
		case <-ctx.Done():
			err = ctx.Err()
		}
	}
	service.Stop()
	if err != nil {
		return err
	}
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	read, err := callFixture(ctx, h, identity, "test/gear_fixture", map[string]any{"mode": "share_read"})
	if err != nil {
		return err
	}
	report["read"] = read
	status, err := callFixture(ctx, h, identity, "test/sleeping_greedy_status", map[string]any{"pawn": pawn})
	if err != nil {
		return err
	}
	report["status"] = status
	catalog, err := cases.Catalog(ctx, h.Client, identity)
	if err != nil {
		return err
	}
	levels, err := catalog.ImpressivenessLevels()
	if err != nil {
		return err
	}
	impressiveness, _ := status["impressiveness"].(float64)
	rung, quality := 0, ""
	for _, row := range na.AsSlice(read["worn"]) {
		m, _ := na.AsMap(row)
		if r := shareArmorRung[na.AsString(m["defName"])]; r > rung {
			rung, quality = r, na.AsString(m["quality"])
		}
	}
	report["armor_rung"], report["armor_quality"] = rung, quality
	// Necessities are never charged, rich or poor: the bare colonist wears
	// the basic outfit and the greedy colonist still sleeps in an owned bed.
	if len(na.AsSlice(read["bareWorn"])) == 0 {
		return fmt.Errorf("the bare colonist was left undressed: %v", read["bareWorn"])
	}
	if na.AsString(status["room"]) == "" {
		return fmt.Errorf("the greedy colonist's bed sits in no room: %v", status)
	}
	if rung == 0 {
		return fmt.Errorf("the greedy colonist wears no torso armor at all: %v", read["worn"])
	}
	switch wealth {
	case "rich":
		if impressiveness < levels.SlightlyImpressive {
			return fmt.Errorf("rich colony bedroom impressiveness %.1f, want >= %.0f", impressiveness, levels.SlightlyImpressive)
		}
		if rung < shareArmorRich {
			return fmt.Errorf("rich colony armor rung %d (%s), want >= %d", rung, quality, shareArmorRich)
		}
	default:
		if impressiveness >= levels.SlightlyImpressive {
			return fmt.Errorf("poor colony bedroom impressiveness %.1f reached the rich stage %.0f", impressiveness, levels.SlightlyImpressive)
		}
		if rung >= shareArmorRich {
			return fmt.Errorf("poor colony wears the plate armor (rung %d, %s)", rung, quality)
		}
	}
	return nil
}
