package medical

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

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
	shooter := na.AsString(s.Prepared()["shooterId"])
	if shooter == "" {
		return fmt.Errorf("prepare: missing shooter: %#v", s.Prepared())
	}
	run, err := startSurgeryRun(ctx, s, "elective-bionic")
	if err != nil {
		return err
	}
	defer run.stop()
	return run.until(ctx, 3*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		for _, surgery := range surgeries {
			if surgery.Recipe() != "InstallBionicEye" {
				continue
			}
			if string(surgery.Pawn()) != shooter {
				var all []string
				for _, x := range surgeries {
					all = append(all, string(x.Pawn())+"/"+x.Recipe())
				}
				return "", false, fmt.Errorf("bionic eye elective went to %s, not the shooter %s (all %v)", surgery.Pawn(), shooter, all)
			}
			s.Report()["elective"] = map[string]any{"pawn": shooter, "part": surgery.Part()}
			return "", true, nil
		}
		return na.Signature(len(surgeries)), false, nil
	})
}
