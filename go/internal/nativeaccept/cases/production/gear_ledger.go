package production

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
)

func init() {
	cases.Register(cases.Case{
		Name: "production/gear-ledger",
		Scope: "MaintainEquipment and the armory declare their gear batches to the work ledger (#2597): the lab colony's " +
			"missing apparel or weapons become a gear_batch bill the ledger planner places under a ledger-* method, and the " +
			"journal records it completed. Nightly tier: the bill finishing and an undeclared bill being " +
			"removed after policy.OrphanGraceRounds are pinned by the buildingruntime ledger tests.",
		Start:  cases.LabStart(),
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Gear, routinefamily.Armory, routinefamily.Bill}},
		Budget: 6 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: gearLedger,
	})
}

func gearLedger(ctx context.Context, s cases.Session) error {
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err = service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer journal.Close()
	return na.WaitProgress(ctx, na.Wait{Ceiling: 5 * time.Minute, Stall: 2 * time.Minute, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.PlanHistoryWithMethods(ctx, 256, "ledger-*")
		if err != nil {
			return "", false, err
		}
		for _, p := range plans {
			for _, progress := range p.Progress {
				if bill, ok := progress.Action().ProductionBill(); ok && bill.Mode() == domain.GearBatch && progress.View().Stage == domain.Completed {
					return "gear batch placed by the ledger", true, nil
				}
			}
		}
		return "waiting for the ledger to place a gear batch", false, nil
	})
}
