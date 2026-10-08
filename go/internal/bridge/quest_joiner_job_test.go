package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestQuestJoinerHelpUsesExactExistingGiveJobShape(t *testing.T) {
	service, err := domain.NewRecoveryService("helper", "joiner", domain.RecoveryServiceOfferHelp)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRecoveryServiceAction("help", service)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := recoverAction(action)
	if err != nil {
		t.Fatal(err)
	}
	job := wire.GetGiveJob()
	if job.GetJob() != "OfferHelp" || job.GetPawn().GetId() != "helper" || len(job.Targets) != 1 || job.Targets[0].GetId() != "joiner" {
		t.Fatal(job)
	}
}
