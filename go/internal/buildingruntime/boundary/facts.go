package boundary

import (
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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

// PawnStateToken is the row-level pawn-state CAS ref
// (NativePawnObservationTools.PawnSnapshotToken: dead/downed/drafted/in
// bed/hostility/mental state/faction), the token the native AssignBed
// operation compares its expected pawn token against. PawnToken above is the
// draft-control token on the EntityRef, which pawn-order operations use.
func PawnStateToken(row *n.PawnState, ctx *c.ObservationContext) (string, error) {
	ref := row.GetSnapshot()
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

// JobEvidenceExpectedJob decodes one pawn's JobEvidence into the ExpectedJob
// fencing value EnsureMood-* relief dispatch (bridge.MoodReliefWriter) must
// supply. JobEvidence.LoadId is the exact decimal-string form of the native
// Job.loadID int -- see NativeObservationTools.cs's JobRow
// (integrations/rimgovernor-native/src/Bridge/Protocol/NativeObservationTools.cs,
// "row.LoadId = job.loadID.ToString(...)") -- the same int
// NativeMoodReliefOperations.cs fences on ("jobId = job.loadID") and the same
// int the legacy JSON tool exposed as jobLoadId. A pawn with no current job
// carries JobRow's own "current_job" Not-Applicable issue instead of a
// LoadId, which decodes as explicitly Idle rather than an unknown job.
//
// job itself being nil (the job tracker/queue unavailable, PawnState's own
// "job" issue) is a different, coarser unavailability the caller must check
// first via IssueField(row.Issues, "job") -- this function only decodes an
// already-present JobEvidence.
func JobEvidenceExpectedJob(job *n.JobEvidence) (bridge.MoodReliefExpectedJob, bool) {
	if job == nil {
		return bridge.MoodReliefExpectedJob{}, false
	}
	if IssueField(job.Issues, "current_job") {
		if job.LoadId != nil {
			return bridge.MoodReliefExpectedJob{}, false
		}
		return bridge.MoodReliefExpectedJob{Idle: true}, true
	}
	if job.LoadId == nil {
		return bridge.MoodReliefExpectedJob{}, false
	}
	id, err := strconv.ParseInt(job.GetLoadId(), 10, 32)
	if err != nil {
		return bridge.MoodReliefExpectedJob{}, false
	}
	v := int32(id)
	return bridge.MoodReliefExpectedJob{JobID: &v}, true
}
