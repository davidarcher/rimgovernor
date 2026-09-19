// Package httpapi exposes bounded local views and explicitly configured player controls.
package httpapi

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
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

// AttentionAcknowledger clears one blocking GABS attention item so a
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
	AssetsDir                    string
	ReadTimeout, ShutdownTimeout time.Duration
	MaxResponseBytes             int
	Presentation                 PresentationReader
	PresentationMedia            PresentationMediaWriter
	Notifications                NotificationReader
	ClockReview                  ClockReview
	Routines                     RoutineProvider
	WorldEvaluation              WorldEvaluation
	ColonyStatus                 ColonyStatus
	Lifecycle                    LifecycleWriter
	// Attention, when set, lets a lifecycle mutation clear one blocking GABS
	// attention item (raised for a game-side log line GABS treats as
	// noteworthy, e.g. an error-level message) and retry once rather than
	// failing outright. A nil Attention preserves prior behavior.
	Attention AttentionAcknowledger
	// VideoStreamPollInterval sets how often the video-stream WebSocket relay
	// polls for a new frame (ReadFrame, or the shared-memory buffer when
	// VideoFrames opened it). Zero uses a sane default (~24 Hz).
	VideoStreamPollInterval time.Duration
	// VideoFrames, when set, opens the native shared-memory frame buffer named
	// by the lease's source ID so the relay reads pixels directly instead of
	// through the base64 ReadFrame round trip. An open failure (controller on
	// another host, buffer already released) falls back to ReadFrame. Nil
	// always uses ReadFrame.
	VideoFrames func(sourceID string) (videoshm.Reader, error)
	// Pprof mounts net/http/pprof under /debug/pprof/ (see pprof.go); off,
	// the routes answer 404.
	Pprof bool
	// FlightRecorder is the absolute path of the flight-recorder ring the
	// /api/telemetry routes read (see telemetry.go); empty, the routes
	// answer 404.
	FlightRecorder string
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

// PopulationPolicy is the wire shape for the colony population capacity
// policy: a maximum colonist count and a minimum stored-food reserve in
// days. It is a configuration value rather than a plan action, so it
// carries no entity identity and no before-token.
type PopulationPolicy struct {
	Maximum  int32   `json:"maximum"`
	FoodDays float64 `json:"foodDays"`
}

// PopulationDecision is the wire shape for one player-sourced per-pawn
// population direction: rescue, capture, recruit or ignore for one exact
// observed pawn. Like PopulationPolicy it is a recorded direction rather than
// a plan action, so it carries no before-token.
type PopulationDecision struct {
	Pawn     string `json:"pawn"`
	Decision string `json:"decision"`
}

// ResourcePolicy is the wire shape for one resource's whole player-declared
// production policy: the protected reserve and the spending restriction, the
// pair recorded per resource. Unlike the
// two colony policies a change to it does dispatch natively, but the directive
// itself is still a recorded configuration value rather than an action, so it
// carries no before-token of its own; the CAS token the native write needs is
// read fresh at dispatch inspection.
type ResourcePolicy struct {
	Resource string `json:"resource"`
	Reserve  int64  `json:"reserve"`
	Spending string `json:"spending"`
}

// ExpeditionPolicy is the wire shape for a whole set of expedition risk
// limits, as read back from the server. Like PopulationPolicy it is
// configuration rather than a plan action, so it carries no entity identity
// and no before-token.
type ExpeditionPolicy struct {
	MinimumHomeColonists          int32   `json:"minimumHomeColonists"`
	MinimumHomeFoodDays           float64 `json:"minimumHomeFoodDays"`
	TravelFoodMarginDays          float64 `json:"travelFoodMarginDays"`
	MaximumTravelDays             float64 `json:"maximumTravelDays"`
	MaximumCaravans               int32   `json:"maximumCaravans"`
	MinimumGoodwill               int32   `json:"minimumGoodwill"`
	MinimumDestinationTemperature float64 `json:"minimumDestinationTemperature"`
	MaximumDestinationTemperature float64 `json:"maximumDestinationTemperature"`
	KeepHomeDoctor                bool    `json:"keepHomeDoctor"`
	RequireReturnStorage          bool    `json:"requireReturnStorage"`
}

// ExpeditionPolicyPatch is the wire shape a player sends: only the limits
// being changed. Every field is a pointer and omitted when unset, because an
// absent field means "keep the established value" rather than any particular
// number, and an explicit false is a real requested value.
type ExpeditionPolicyPatch struct {
	MinimumHomeColonists          *int32   `json:"minimumHomeColonists,omitempty"`
	MinimumHomeFoodDays           *float64 `json:"minimumHomeFoodDays,omitempty"`
	TravelFoodMarginDays          *float64 `json:"travelFoodMarginDays,omitempty"`
	MaximumTravelDays             *float64 `json:"maximumTravelDays,omitempty"`
	MaximumCaravans               *int32   `json:"maximumCaravans,omitempty"`
	MinimumGoodwill               *int32   `json:"minimumGoodwill,omitempty"`
	MinimumDestinationTemperature *float64 `json:"minimumDestinationTemperature,omitempty"`
	MaximumDestinationTemperature *float64 `json:"maximumDestinationTemperature,omitempty"`
	KeepHomeDoctor                *bool    `json:"keepHomeDoctor,omitempty"`
	RequireReturnStorage          *bool    `json:"requireReturnStorage,omitempty"`
}
type DraftCleanup struct {
	Stage domain.DraftCleanupStage `json:"stage"`
}
type Building struct {
	DefName  string          `json:"defName"`
	X        int32           `json:"x"`
	Z        int32           `json:"z"`
	Rotation domain.Rotation `json:"rotation"`
	Stuff    string          `json:"stuff"`
}
type Progress struct {
	DraftCleanup       *DraftCleanup              `json:"draftCleanup,omitempty"`
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
