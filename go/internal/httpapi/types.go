// Package httpapi exposes bounded local read views. It has no mutation interface.
package httpapi

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

// Providers return owned snapshots and must honor request cancellation. API reads
// do not trigger inference, native mutations, or implicit observation refreshes.
type SnapshotProvider interface {
	Snapshot(context.Context) (Snapshot, error)
}
type PlanReader interface {
	LoadPlan(context.Context, domain.PlanID) (store.PlanState, error)
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
	Colony    domain.ColonyID         `json:"colony"`
	Map       domain.MapID            `json:"map"`
	Load      domain.LoadID           `json:"load"`
	Direction domain.DirectionID      `json:"direction,string"`
	Plan      domain.PlanID           `json:"plan"`
	Revision  domain.PlanRevision     `json:"revision,string"`
	Native    domain.NativeGeneration `json:"native,string"`
}
type Plan struct {
	ID       domain.PlanID       `json:"id"`
	Revision domain.PlanRevision `json:"revision,string"`
	Actions  []Action            `json:"actions"`
}
type Action struct {
	ID       domain.ActionID   `json:"id"`
	Kind     domain.ActionKind `json:"kind"`
	Building Building          `json:"building"`
	Progress Progress          `json:"progress"`
}
type Building struct {
	DefName  string          `json:"defName"`
	X        int32           `json:"x"`
	Z        int32           `json:"z"`
	Rotation domain.Rotation `json:"rotation"`
	Stuff    string          `json:"stuff"`
}
type Progress struct {
	Stage      domain.Stage     `json:"stage"`
	Attempt    domain.AttemptID `json:"attempt,string"`
	Tick       domain.Tick      `json:"tick"`
	Unresolved bool             `json:"unresolved"`
	Receipt    *domain.Receipt  `json:"receipt"`
	Effect     *domain.Effect   `json:"effect"`
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
	return &Generation{s.Colony, s.Map, s.Load, s.Direction, s.Plan, s.Revision, s.Native}
}
