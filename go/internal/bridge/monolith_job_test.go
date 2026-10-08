package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The monolith's two orders ride the recovery-service action as give-jobs
// named after the game's JobDefs (#2437).
func TestMonolithOrdersAreGiveJobsOnTheMonolith(t *testing.T) {
	for method, job := range map[domain.RecoveryMethod]string{
		domain.RecoveryServiceInvestigateMonolith: "InvestigateMonolith",
		domain.RecoveryServiceActivateMonolith:    "ActivateMonolith",
	} {
		service, err := domain.NewRecoveryService("Thing_Pawn1", "Thing_VoidMonolith1", method)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewRecoveryServiceAction("a-0", service)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := recoverAction(action)
		if err != nil {
			t.Fatal(err)
		}
		intent := wire.GetGiveJob()
		if intent.GetPawn().GetId() != "Thing_Pawn1" || intent.GetJob() != job || len(intent.Targets) != 1 || intent.Targets[0].GetId() != "Thing_VoidMonolith1" {
			t.Fatalf("%s: %v", method, intent)
		}
	}
	if _, err := domain.NewRecoveryService("Thing_Pawn1", "Thing_VoidMonolith1", "awaken"); err == nil {
		t.Fatal("an unknown service method was accepted")
	}
}
