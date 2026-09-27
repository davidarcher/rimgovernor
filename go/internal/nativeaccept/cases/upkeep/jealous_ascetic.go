package upkeep

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// upkeep/jealous-ascetic (#839, epic #799 items 3-4): a Jealous colonist
// owns a bare bedroom beside an Ascetic colonist's room that already holds
// an end table. The jealous room is raised until BedroomJealous clears; the
// ascetic room is capped below decent and must never rise.
func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{
		Name:   "upkeep/jealous-ascetic",
		Scope:  "Room quality targets (#813/#826): a Jealous colonist's bedroom is raised until the jealous thought clears, while an Ascetic colonist's room is never upgraded.",
		Start:  cases.LabStart(),
		Keep:   sleeping.keep,
		Serve:  &cases.ServeSpec{Families: []string{"sleeping", "flooring", "work"}, Extra: sleeping.extra, Prefix: prefix},
		Budget: 30 * time.Minute,
		Reason: "Furniture and floor builds in the jealous room, then native room stats and thoughts.",
		Run:    runJealousAscetic,
	})
}

func runJealousAscetic(ctx context.Context, s cases.Session) error {
	report, identity := s.Report(), s.Identity()
	h := s.Harness()
	prepared, err := callFixture(ctx, h, identity, "test/sleeping_setup", map[string]any{"jealous_ascetic": true})
	if err != nil {
		return err
	}
	report["prepared"] = prepared
	jealous, ascetic := na.AsString(prepared["pawn"]), na.AsString(prepared["ascetic"])
	if jealous == "" || ascetic == "" || na.AsString(prepared["jealousBed"]) == "" || na.AsString(prepared["asceticBed"]) == "" {
		return fmt.Errorf("fixture returned no jealous/ascetic bedrooms: %#v", prepared)
	}
	before, err := callFixture(ctx, h, identity, "test/sleeping_greedy_status", map[string]any{"pawn": ascetic, "thought": "Ascetic"})
	if err != nil {
		return err
	}
	report["ascetic_before"] = before
	start, _ := before["impressiveness"].(float64)
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	journal, err := serveStage(ctx, service, report)
	if err == nil {
		recoverCtx, cancel := context.WithTimeout(ctx, 25*time.Minute)
		g, werr := waitNeed(recoverCtx, journal, policy.MaintainHousing, domain.NeedRecovered)
		cancel()
		err = werr
		if werr == nil {
			report["sleeping_recovered_tick"] = int64(g.Goal.Tick)
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
	jstatus, err := callFixture(ctx, h, identity, "test/sleeping_greedy_status", map[string]any{"pawn": jealous, "thought": "BedroomJealous"})
	if err != nil {
		return err
	}
	report["jealous_status"] = jstatus
	astatus, err := callFixture(ctx, h, identity, "test/sleeping_greedy_status", map[string]any{"pawn": ascetic, "thought": "Ascetic"})
	if err != nil {
		return err
	}
	report["ascetic_status"] = astatus
	if active, _ := jstatus["thoughtActive"].(bool); active {
		return fmt.Errorf("jealous thought still active: jealous room %v, ascetic room %v", jstatus["impressiveness"], astatus["impressiveness"])
	}
	// Room stats drift slightly with dirt and wear; a controller upgrade
	// (furniture or floor) moves impressiveness by whole points.
	if end, _ := astatus["impressiveness"].(float64); end > start+0.5 {
		return fmt.Errorf("ascetic room impressiveness rose %.1f -> %.1f", start, end)
	}
	return nil
}
