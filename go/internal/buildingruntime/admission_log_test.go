package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRefusalSummaryNamesEachReason(t *testing.T) {
	got := refusalSummary([]policy.Refusal{{Reason: policy.AlreadyReserved}, {Action: "p-0", Reason: "insufficient_stock", Resource: "Steel"}})
	if got != "["+string(policy.AlreadyReserved)+" insufficient_stock/Steel@p-0]" {
		t.Fatal(got)
	}
	if refusalSummary(nil) != "none" {
		t.Fatal(refusalSummary(nil))
	}
}
