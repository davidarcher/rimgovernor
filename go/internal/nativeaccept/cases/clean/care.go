package clean

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "clean/care",
		Scope: "Opportunistic cleaning around a patient's bed (#2521): with eight old filth beside a bed, the patched native " +
			"WorkGiver_DoBill hands a doctor a Clean job capped at five filth before a surgery bill, and a finished tend hands " +
			"the doctor the same job; a doctor with Cleaning at priority 0 gets the surgery bill and no follow-up job. " +
			"A Go snapshot cannot prove the Harmony patches on vanilla job selection and the tend driver's finalizer.",
		Start: cases.LabStart(), RequiredOps: []string{"test/care_cleaning"},
		Budget: 3 * time.Minute, Crew: cases.Crew{Size: 3},
		Run: runCare,
	})
}

func runCare(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "care-cleaning-authority", s.Identity()); err != nil {
		return err
	}
	row, err := h.Call(ctx, "care-cleaning", "test/care_cleaning", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["probe"] = row
	if ok, _ := na.AsBool(row["success"]); !ok {
		return fmt.Errorf("care_cleaning refused: %#v", row)
	}
	if active, _ := na.AsBool(row["supervisorActive"]); !active {
		return fmt.Errorf("supervisor not active: %#v", row)
	}
	if row["surgery"] != "Clean" || na.AsNumber(row["surgeryTargets"]) != 5 {
		return fmt.Errorf("a doctor must clean the five-filth cap before the surgery bill: %#v", row)
	}
	if row["tend"] != "Clean" || na.AsNumber(row["tendTargets"]) != 5 {
		return fmt.Errorf("a finished tend must hand the doctor the five-filth clean job: %#v", row)
	}
	if row["surgeryDisabled"] != "DoBill" || na.AsNumber(row["surgeryDisabledTargets"]) != 0 {
		return fmt.Errorf("the Cleaning-disabled doctor must go straight to the surgery bill: %#v", row)
	}
	if row["tendDisabled"] != "none" || na.AsNumber(row["tendDisabledTargets"]) != 0 {
		return fmt.Errorf("the Cleaning-disabled doctor must not clean after tending: %#v", row)
	}
	return nil
}
