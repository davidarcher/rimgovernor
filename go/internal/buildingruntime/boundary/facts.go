package boundary

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// FactBool, FactUint, FactPresence, PawnToken, ReceiptJob and IssueField
// decode typed native pawn/receipt evidence into domain facts. They are
// shared across every pawn-order boundary (Draft, Tend, Haul, Rescue, Equip,
// Ranged, Melee), which is why they live in the shared boundary package
// rather than any one family's package.

func FactBool(v *bool) domain.Fact[bool] {
	if v == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*v)
}
func FactUint(v *uint32) domain.Fact[uint32] {
	if v == nil {
		return domain.Unknown[uint32]()
	}
	return domain.Known(*v)
}

// FactPresence handles fields (like MentalState) that native leaves unset,
// rather than reporting a false-ish value, when the fact is genuinely known
// to not apply (e.g. a pawn with no mental state). A nil value is only
// actually unknown when no matching NOT_APPLICABLE issue confirms the
// omission; otherwise treat the field as known-absent.
func FactPresence(v *string, issues []*n.ReadIssue, field string) domain.Fact[bool] {
	if v != nil {
		return domain.Known(true)
	}
	for _, issue := range issues {
		if issue.GetField() == field && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
			return domain.Known(false)
		}
	}
	return domain.Unknown[bool]()
}

func PawnToken(row *n.PawnState, ctx *c.ObservationContext) (string, error) {
	ref := row.Pawn.GetSnapshot()
	if ref == nil || ref.GetEntityId() != row.Pawn.GetId() || !proto.Equal(ref.Context, ctx) || !ValidID(ref.GetToken()) {
		return "", executor.ErrHeld
	}
	return ref.GetToken(), nil
}

func ReceiptJob(receipt *r.Receipt) *r.JobEffect {
	if receipt == nil {
		return nil
	}
	switch v := receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		return v.Applied.GetObserved().GetJob()
	case *r.Receipt_NoChange:
		return v.NoChange.GetObserved().GetJob()
	case *r.Receipt_Uncertain:
		return v.Uncertain.GetLastObserved().GetJob()
	}
	return nil
}

// IssueField reports whether issues names field, the shared "unresolved
// native issue" check used by every pawn-order boundary's job-facts decoding.
func IssueField(issues []*n.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}
