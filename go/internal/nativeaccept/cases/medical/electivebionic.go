package medical

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const electivePrefix = "elective-bionic"

func init() {
	cases.Register(cases.Case{
		Name: "medical/elective-bionic",
		Scope: "Elective role-based upgrade (#1167): with a ranged shooter, a master doctor, one bionic eye and a medical " +
			"bed, MaintainSurgery admits an InstallBionicEye SurgeryIntent on the shooter and on nobody else.",
		Start:   cases.Fixture{Op: "test/elective_bionic_prepare", On: cases.LabStart()},
		Service: true,
		Budget:  5 * time.Minute,
		Run:     electiveBionic,
	})
}

func electiveBionic(ctx context.Context, s cases.Session) error {
	report, identity, prepared := s.Report(), s.Identity(), s.Prepared()
	shooter := na.AsString(prepared["shooterId"])
	if shooter == "" {
		return fmt.Errorf("prepare: missing shooter: %#v", prepared)
	}
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"medical", "work"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	if _, err = service.WaitAttached(identity, 90*time.Second); err != nil {
		return err
	}
	if _, err = service.Resume(electivePrefix, identity, token, report); err != nil {
		return err
	}
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: electivePrefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	defer func() { report["authority_reacquisitions"] = stopKeepAlive() }()
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	waitCtx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	return na.WaitProgress(waitCtx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.LoadPlans(ctx, 256)
		if err != nil {
			return "", false, err
		}
		for _, plan := range plans {
			for _, progress := range plan.Progress {
				surgery, ok := progress.Action().Surgery()
				if !ok || surgery.Recipe() != "InstallBionicEye" {
					continue
				}
				if surgery.Pawn() != domain.PawnID(shooter) {
					return "", false, fmt.Errorf("bionic eye elective went to %s, not the shooter %s", surgery.Pawn(), shooter)
				}
				report["elective"] = map[string]any{"plan": string(plan.Spec.ID()), "pawn": shooter, "part": surgery.Part()}
				return "", true, nil
			}
		}
		return na.Signature(len(plans)), false, nil
	})
}
