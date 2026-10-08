package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestGravEngineInspectionUsesExactGiveJobTargets(t *testing.T) {
	service, err := domain.NewRecoveryService("pawn", "engine", domain.RecoveryServiceInspectGravEngine)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRecoveryServiceAction("inspect", service)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := recoverAction(action)
	if err != nil {
		t.Fatal(err)
	}
	job := wire.GetGiveJob()
	if job.GetPawn().GetId() != "pawn" || job.GetJob() != "InspectGravEngine" || len(job.Targets) != 1 || job.Targets[0].GetId() != "engine" {
		t.Fatal(job)
	}
}
