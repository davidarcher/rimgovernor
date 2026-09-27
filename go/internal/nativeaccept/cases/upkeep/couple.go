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

// upkeep/couple (#812): two bedless lovers beside one vacant double bed.
// MaintainSleeping must assign both to it through BedAssignIntents and recovers
// only once each is observed sleeping there, so neither sleeps alone.
func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{
		Name:   "upkeep/couple",
		Scope:  "Couples share a bedroom (#812): two willing lovers are both assigned one vacant double bed via BedAssignIntents and MaintainSleeping recovers on their observed shared sleep.",
		Start:  cases.LabStart(),
		Keep:   sleeping.keep,
		Serve:  &cases.ServeSpec{Families: []string{"sleeping", "work"}, Extra: sleeping.extra, Prefix: prefix},
		Budget: 20 * time.Minute,
		Reason: "Two assignments, then a night of observed shared sleep.",
		Run:    runCouple,
	})
}

func runCouple(ctx context.Context, s cases.Session) error {
	report, identity := s.Report(), s.Identity()
	if !na.Contains(s.Names(), "test/sleeping_setup") {
		return fmt.Errorf("missing test/sleeping_setup")
	}
	h := s.Harness()
	prepared, err := callFixture(ctx, h, identity, "test/sleeping_setup", map[string]any{"couple": true})
	if err != nil {
		return err
	}
	report["prepared"] = prepared
	pawn, partner, double := na.AsString(prepared["pawn"]), na.AsString(prepared["partner"]), na.AsString(prepared["doubleBed"])
	if pawn == "" || partner == "" || double == "" {
		return fmt.Errorf("fixture returned no couple: %#v", prepared)
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	journal, err := serveStage(ctx, service, report)
	if err == nil {
		recoverCtx, cancel := context.WithTimeout(ctx, 15*time.Minute)
		g, werr := waitNeed(recoverCtx, journal, policy.MaintainSleeping, domain.NeedRecovered)
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
	observed, err := readColonyFacts(ctx, h, identity, "beds-after")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	for _, raw := range na.AsSlice(upkeep["beds"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["bed"])
		if na.AsString(ref["id"]) != double {
			continue
		}
		owners := map[string]bool{}
		for _, o := range na.AsSlice(row["owners"]) {
			owners[na.AsString(o)] = true
		}
		report["double_bed_owners"] = row["owners"]
		if !owners[pawn] || !owners[partner] || len(owners) != 2 {
			return fmt.Errorf("double bed %s owned by %v, want the couple %s and %s", double, row["owners"], pawn, partner)
		}
		return nil
	}
	return fmt.Errorf("double bed %s missing after recovery", double)
}
