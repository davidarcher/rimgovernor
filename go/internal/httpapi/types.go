// Package httpapi exposes bounded local views and explicitly configured player controls.
package httpapi

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"time"
)

// Snapshot providers return owned snapshots and honor request cancellation.
// Reading a cached snapshot does not trigger inference or native refreshes.
type SnapshotProvider interface {
	Snapshot(context.Context) (Snapshot, error)
}
type PlanReader interface {
	LoadPlan(context.Context, domain.PlanID) (store.PlanState, error)
}

// AttentionAcknowledger clears one blocking attention item so a
// subsequently retried native call is no longer refused on its account.
type AttentionAcknowledger interface {
	AckAttention(ctx context.Context, attentionID string) error
}
type Snapshot struct {
	SessionID    string
	Connected    bool
	Mode         string
	Status       string
	Generation   domain.Fact[domain.GenerationSnapshot]
	Identity     domain.Fact[observation.Identity]
	Tick         domain.Fact[domain.Tick]
	Paused       domain.Fact[bool]
	ObservedAt   domain.Fact[time.Time]
	Stale        bool
	ActivePlanID domain.Fact[domain.PlanID]
}
type Config struct {
	ReadTimeout, ShutdownTimeout time.Duration
	MaxResponseBytes             int
	Presentation                 PresentationReader
	PresentationMedia            PresentationMediaWriter
	Notifications                NotificationReader
	ClockReview                  ClockReview
	Rounds                       RoundsProvider
	ColonyStatus                 ColonyStatus
	Lifecycle                    LifecycleWriter
	// Attention, when set, lets a lifecycle mutation clear one blocking
	// attention item (raised by the GABP host for a game-side log line it treats as
	// noteworthy, e.g. an error-level message) and retry once rather than
	// failing outright. A nil Attention preserves prior behavior.
	Attention AttentionAcknowledger
	// Pprof mounts net/http/pprof under /debug/pprof/ (see pprof.go); off,
	// the routes answer 404.
	Pprof bool
	// FlightRecorder is the absolute path of the flight-recorder ring the
	// spectator route reads; empty, the route projects no rows.
	FlightRecorder string
	// Access, when set, receives one http_access row per request (access.go);
	// nil records nothing.
	Access telemetry.Recorder
}
type State struct {
	SessionID    string         `json:"sessionId"`
	Connected    bool           `json:"connected"`
	Mode         string         `json:"mode"`
	Status       Status         `json:"status"`
	Game         Game           `json:"game"`
	Generation   *Generation    `json:"generation"`
	Identity     *Identity      `json:"identity"`
	ActivePlanID *domain.PlanID `json:"activePlanId"`
}
type Identity struct {
	ColonyID  string `json:"colonyId"`
	MapID     int32  `json:"mapId"`
	LoadToken string `json:"loadToken"`
}
type Status struct {
	Label string `json:"label"`
}
type Game struct {
	Tick       *domain.Tick `json:"tick"`
	Paused     *bool        `json:"paused"`
	ObservedAt *time.Time   `json:"observedAt"`
	Stale      bool         `json:"stale"`
}
type Generation struct {
	Colony   domain.ColonyID         `json:"colony"`
	Map      domain.MapID            `json:"map"`
	Load     domain.LoadID           `json:"load"`
	Plan     domain.PlanID           `json:"plan"`
	Revision domain.PlanRevision     `json:"revision,string"`
	Native   domain.NativeGeneration `json:"native,string"`
}
type Plan struct {
	ID       domain.PlanID       `json:"id"`
	Revision domain.PlanRevision `json:"revision,string"`
	Actions  []Action            `json:"actions"`
}
type Action struct {
	ID       domain.ActionID   `json:"id"`
	Kind     domain.ActionKind `json:"kind"`
	Building *Building         `json:"building,omitempty"`
	Draft    *Draft            `json:"draft,omitempty"`
	Progress Progress          `json:"progress"`
}
type Draft struct {
	PawnID domain.PawnID `json:"pawnId"`
}
type ResearchSelect struct {
	Project string `json:"project"`
}

type Building struct {
	DefName  string          `json:"defName"`
	X        int32           `json:"x"`
	Z        int32           `json:"z"`
	Rotation domain.Rotation `json:"rotation"`
	Stuff    string          `json:"stuff"`
}
type Progress struct {
	Stage              domain.Stage               `json:"stage"`
	Attempt            domain.AttemptID           `json:"attempt,string"`
	Tick               domain.Tick                `json:"tick"`
	Unresolved         bool                       `json:"unresolved"`
	Receipt            *domain.Receipt            `json:"receipt"`
	Effect             *domain.Effect             `json:"effect"`
	UnsuccessfulReason *domain.UnsuccessfulReason `json:"unsuccessfulReason"`
	HeldReasons        []domain.HeldReason        `json:"heldReasons,omitempty"`
}
type Failure struct {
	Code   string `json:"code"`
	Detail string `json:"detail"`
}

func value[T any](fact domain.Fact[T]) *T {
	v, known := fact.Value()
	if !known {
		return nil
	}
	return &v
}
func generation(s domain.GenerationSnapshot) *Generation {
	return &Generation{s.Colony, s.Map, s.Load, s.Plan, s.Revision, s.Native}
}
