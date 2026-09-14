package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type CaptureJournal interface {
	Journal
	PrepareCapture(context.Context, domain.PlanID, domain.ActionID, store.CaptureAdmission) (domain.Progress, error)
}

type CaptureInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.CaptureFacts
}

type CaptureDispatch struct {
	Attempt   Placement
	Admission store.CaptureAdmission
}

type CaptureEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Capturer, Patient     domain.PawnID
}

// CaptureBoundary is optionally composed, like RescueBoundary: neither
// capturer nor patient is drafted, so this family attaches without a hard
// NewWithCapture ctor.
type CaptureBoundary interface {
	InspectCapture(context.Context, Target) (CaptureInspection, error)
	CapturePatient(context.Context, CaptureDispatch) (Receipt, error)
	ObserveCapture(context.Context, CaptureDispatch, domain.GenerationSnapshot) (CaptureEvidence, error)
}

// EnableCapture activates the capture capability; see EnableRescue for why
// capabilities are wired this way instead of inferred from a composed
// Boundary.
func (e *Executor) EnableCapture(capture CaptureBoundary) error {
	if capture == nil {
		return errors.New("capture boundary required")
	}
	j, ok := e.journal.(CaptureJournal)
	if !ok {
		return errors.New("capture boundary requires typed journal")
	}
	e.capture, e.captureJournal = capture, j
	return nil
}
