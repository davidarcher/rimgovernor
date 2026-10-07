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

// upkeep/greedy-bedroom (#814): a Greedy colonist owns a bare 5x4 bedroom.
// The room quality gap closer furnishes it from the bedroom template
// (end table, dresser, lamp) and MaintainFlooring floors it until the
// native room reads slightly impressive (50) and the Greedy thought clears.
func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{
		Name:   "upkeep/greedy-bedroom",
		Scope:  "Room quality gap closer (#814): a Greedy colonist's bare bedroom is furnished and floored until it reads slightly impressive and the Greedy thought clears.",
		Start:  cases.LabStart(),
		Keep:   sleeping.keep,
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Sleeping, routinefamily.Flooring, routinefamily.Work}, Extra: sleeping.extra, Prefix: prefix},
		Budget: 30 * time.Minute,
		Reason: "Three furniture builds and a floor, then the native room stat.",
		Run:    runGreedyBedroom,
	})
}

func runGreedyBedroom(ctx context.Context, s cases.Session) error {
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
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	journal, err := serveStage(ctx, service, report)
	if err == nil {
		recoverCtx, cancel := context.WithTimeout(ctx, 25*time.Minute)
		_, werr := waitNeed(recoverCtx, journal, policy.MaintainHousing, domain.FindingMet)
		cancel()
		err = werr
	}
	service.Stop()
	if err != nil {
		return err
	}
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	status, err := callFixture(ctx, h, identity, "test/sleeping_greedy_status", map[string]any{"pawn": pawn})
	if err != nil {
		return err
	}
	report["status"] = status
	impressiveness, _ := status["impressiveness"].(float64)
	catalog, err := cases.Catalog(ctx, h.Client, identity)
	if err != nil {
		return err
	}
	levels, err := catalog.ImpressivenessLevels()
	if err != nil {
		return err
	}
	if impressiveness < levels.SlightlyImpressive {
		return fmt.Errorf("greedy bedroom impressiveness %.1f, want >= %.0f", impressiveness, levels.SlightlyImpressive)
	}
	if active, _ := status["greedyThought"].(bool); active {
		return fmt.Errorf("greedy thought still active at impressiveness %.1f", impressiveness)
	}
	return nil
}
