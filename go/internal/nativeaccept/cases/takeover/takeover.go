// Package takeover supplies the authority boundary and native receipt audit for
// takeover/draft beside the draft subsystem's native assertions.
package takeover

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// Manual establishes and reads back Manual before any simulated player edit.
func Manual(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "takeover-initial-auto", s.Identity())
	if err != nil {
		return err
	}
	if _, err = na.RevokeManual(ctx, h.WireFunc(), "takeover-manual", s.Identity(), grant); err != nil {
		return err
	}
	status, _, err := na.AuthorityStatus(ctx, h.WireFunc(), "takeover-manual-readback", s.Identity())
	if err != nil {
		return err
	}
	if _, err = na.RequireInactive(status, "REVOCATION_REASON_MANUAL"); err != nil {
		return err
	}
	s.Report()["manual_before_edit"] = status
	return nil
}

// noProvenanceHold is a fence: every reason listed is a retired
// "the player owns this, hands off" hold, and none is produced any more (the
// last, clearance's foreign_designation, went with the wall removal ledger's
// ownership flags). A journal naming one means the concept crept back.
func noProvenanceHold(value any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	for _, reason := range []string{"foreign_designation", "player_building", "player_override", "player_owned", "foreign_bill", "unmanaged_bill", "player_schedule", "player_excluded"} {
		if strings.Contains(string(data), `"`+reason+`"`) {
			return fmt.Errorf("auto retained provenance hold %s", reason)
		}
	}
	return nil
}

// Receipt audits the native attempt journal for bridge-driven takeover cases.
// A successful receipt must be applied and recoverable, never a provenance refusal.
func Receipt(ctx context.Context, h *na.Harness, request, receipt map[string]any, report na.Report) error {
	if _, ok := na.AsMap(receipt["applied"]); !ok {
		return fmt.Errorf("takeover was not applied: %v", receipt)
	}
	reply, err := h.Wire(ctx, "takeover-journal-lookup", "receipts_lookup", request)
	if err != nil {
		return err
	}
	_, stored, err := na.Outcome(reply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(receipt, stored) {
		return fmt.Errorf("native takeover journal differs from applied receipt")
	}
	report["takeover_native_journal"] = stored
	return noProvenanceHold(stored)
}
