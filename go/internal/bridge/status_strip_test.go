package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

func TestStatusStripRowsCarrySeverityTargetAndDetail(t *testing.T) {
	rows := statusStripRows([]policy.StatusRow{
		{Key: "food", Text: "food 1.0 days", Severity: policy.StatusCritical},
		{Key: "refusal", Text: "refused", Severity: policy.StatusWarning, Target: domain.Known(domain.Cell{X: 7, Z: 8})},
		{Key: "goal.a", Text: "a", Severity: policy.StatusInfo, Detail: true},
	})
	if rows[0].GetSeverity() != p.StatusSeverity_STATUS_SEVERITY_CRITICAL || rows[0].Target != nil || rows[0].Detail != nil || rows[0].GetText() != "food 1.0 days" {
		t.Fatalf("%v", rows[0])
	}
	if rows[1].GetSeverity() != p.StatusSeverity_STATUS_SEVERITY_WARNING || rows[1].Target.GetX() != 7 || rows[1].Target.GetZ() != 8 {
		t.Fatalf("%v", rows[1])
	}
	if rows[2].GetSeverity() != p.StatusSeverity_STATUS_SEVERITY_INFO || !rows[2].GetDetail() || rows[2].GetKey() != "goal.a" {
		t.Fatalf("%v", rows[2])
	}
}
