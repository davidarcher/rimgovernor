package executor

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// DraftJournal retains draft evidence in the same journal as other action kinds.
// Admission is durable evidence; it cannot restore live write permission.
type DraftJournal interface {
	Journal
	PrepareDraft(context.Context, domain.PlanID, domain.ActionID, store.DraftAdmission) (domain.Progress, error)
	RecordDraftReceipt(context.Context, domain.PlanID, domain.ActionID, domain.AttemptID, domain.Receipt, domain.Fact[domain.DraftClaim]) (domain.Progress, error)
	ObserveDraft(context.Context, domain.PlanID, domain.Observation, domain.GenerationSnapshot, domain.Fact[domain.DraftClaim]) (domain.Progress, error)
	BeginDraftCleanup(context.Context, domain.PlanID, domain.ActionID, domain.DraftReleaseRequest) (domain.Progress, error)
	RecordDraftCleanup(context.Context, domain.PlanID, domain.ActionID, domain.DraftRelease, domain.DraftCleanupOutcome) (domain.Progress, error)
	ObserveDraftCleanup(context.Context, domain.PlanID, domain.ActionID, domain.DraftCleanupObservation) (domain.Progress, error)
	ObserveDraftScopeSupersession(context.Context, domain.PlanID, domain.DraftScopeSupersession) (domain.Progress, error)
}

var _ DraftJournal = (*store.Store)(nil)

type DraftInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Pawn                  policy.DraftPawnFacts
	PawnSnapshotToken     string
	Emergency             policy.EmergencySnapshot
}

type DraftDispatch struct {
	Attempt           Placement
	PawnSnapshotToken string
}

type DraftReceipt struct {
	Receipt Receipt
	Claim   domain.Fact[domain.DraftClaim]
}

// Claim must correlate the original native attempt with fresh full-owner pawn
// evidence. A matching pawn, session or drafted flag alone is insufficient.
type DraftEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Drafted               domain.Fact[bool]
	Claim                 domain.Fact[domain.DraftClaim]
}

// Exactly one arm describes fresh native evidence. Missing evidence returns a
// hold, never an inferred release or supersession. Reconcile can bind an unknown
// claim after an ordinary action has become terminal.
type DraftCleanupInspection struct {
	StartedAt, ObservedAt time.Time
	Reconcile             *DraftEvidence
	ScopeSupersession     *domain.DraftScopeSupersession
	Supersession          *domain.DraftCleanupObservation
	Request               *domain.DraftReleaseRequest
}

type DraftCleanupReceipt struct {
	Release domain.DraftRelease
	Outcome domain.DraftCleanupOutcome
}

// DraftBoundary separates live drafting from original-claim cleanup. Cleanup
// reads actual world identity without acquiring permission or entering a runtime
// control gate; shutdown may hold that gate while joining this writer.
type DraftBoundary interface {
	InspectDraft(context.Context, Target) (DraftInspection, error)
	Draft(context.Context, DraftDispatch) (DraftReceipt, error)
	ObserveDraft(context.Context, Placement, domain.GenerationSnapshot) (DraftEvidence, error)
	InspectDraftCleanup(context.Context, Placement, domain.DraftCleanup) (DraftCleanupInspection, error)
	// ReleaseDraft validates native reply identity, generation and exact original
	// claim against the persisted request. It never acquires a lease or retries.
	ReleaseDraft(context.Context, domain.DraftRelease) (DraftCleanupReceipt, error)
}
