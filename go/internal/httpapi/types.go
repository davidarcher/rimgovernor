// Package httpapi exposes bounded local views and explicitly configured player controls.
package httpapi

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
	TradeEconomy                 TradeEconomy
	// VideoStreamPollInterval sets how often the video-stream WebSocket relay
	// polls ReadFrame for a new frame. Zero uses a sane default (~24 Hz);
	// this bounds correctness-proving throughput, not maximum achievable FPS.
	VideoStreamPollInterval time.Duration
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
	Building *Building         `json:"building,omitempty"`
	Draft    *Draft            `json:"draft,omitempty"`
	Progress Progress          `json:"progress"`
}
type Draft struct {
	PawnID domain.PawnID `json:"pawnId"`
}
type CargoItem struct {
	Definition string `json:"definition"`
	Count      uint64 `json:"count,string"`
}
type CaravanDeparture struct {
	Crew            []domain.PawnID `json:"crew"`
	Cargo           []CargoItem     `json:"cargo"`
	DestinationTile int32           `json:"destinationTile"`
}
type QuestAccept struct {
	Quest        domain.QuestID `json:"quest"`
	AccepterPawn domain.PawnID  `json:"accepterPawn"`
	RewardChoice int32          `json:"rewardChoice"`
}
type ResearchSelect struct {
	Project string `json:"project"`
}
type TravelCaravan struct {
	Caravan         domain.CaravanID `json:"caravan"`
	Kind            string           `json:"kind"`
	DestinationTile int32            `json:"destinationTile"`
}
type Tend struct {
	Doctor  domain.PawnID `json:"doctor"`
	Patient domain.PawnID `json:"patient"`
}
type Rescue struct {
	Rescuer domain.PawnID `json:"rescuer"`
	Patient domain.PawnID `json:"patient"`
}
type Husbandry struct {
	Animal       domain.PawnID `json:"animal"`
	Method       string        `json:"method"`
	TrainableDef string        `json:"trainableDef"`
}
type RecoveryService struct {
	Pawn   domain.PawnID `json:"pawn"`
	Thing  string        `json:"thing"`
	Method string        `json:"method"`
}
type BedAssign struct {
	Pawn        domain.PawnID `json:"pawn"`
	Bed         string        `json:"bed"`
	PreviousBed string        `json:"previousBed"`
}
type BuildingTemperature struct {
	Thing   string  `json:"thing"`
	Celsius float64 `json:"celsius"`
	Before  string  `json:"before"`
}
type Surgery struct {
	Patient domain.PawnID `json:"patient"`
	Recipe  string        `json:"recipe"`
	Part    int32         `json:"part"`
}
type Movement struct {
	Pawn domain.PawnID `json:"pawn"`
	X    int32         `json:"x"`
	Z    int32         `json:"z"`
}
type SettlementGift struct {
	Caravan    domain.CaravanID    `json:"caravan"`
	Settlement domain.SettlementID `json:"settlement"`
	Faction    domain.FactionID    `json:"faction"`
	CrewIDs    []domain.PawnID     `json:"crewIds"`
	Silver     int32               `json:"silver"`
}
type QuestFulfill struct {
	Quest   domain.QuestID   `json:"quest"`
	Caravan domain.CaravanID `json:"caravan"`
	CrewIDs []domain.PawnID  `json:"crewIds"`
}
type TradeLine struct {
	LineID        string `json:"lineId"`
	AbsoluteCount int32  `json:"absoluteCount"`
}
type TradeEconomicFloor struct {
	DefName string `json:"defName"`
	Count   int32  `json:"count"`
}

// Trade is the wire shape for all four trade sub-operations; only the
// fields the selected Kind carries are populated (the same discipline
// domain.Trade itself uses), all others are omitted/zero.
type Trade struct {
	Kind                  domain.TradeOperationKind `json:"kind"`
	Trader                domain.SettlementID       `json:"trader,omitempty"`
	Negotiator            domain.PawnID             `json:"negotiator,omitempty"`
	GiftMode              bool                      `json:"giftMode,omitempty"`
	Lines                 []TradeLine               `json:"lines,omitempty"`
	AllowPawns            bool                      `json:"allowPawns,omitempty"`
	ExpectedDealSignature string                    `json:"expectedDealSignature,omitempty"`
	EconomicFloors        []TradeEconomicFloor      `json:"economicFloors,omitempty"`
	AllowEmpty            bool                      `json:"allowEmpty,omitempty"`
	EndKind               domain.TradeEndKind       `json:"endKind,omitempty"`
	ReceiveQuest          bool                      `json:"receiveQuest,omitempty"`
}
type ZoneCell struct {
	X int32 `json:"x"`
	Z int32 `json:"z"`
}
type ZoneCreate struct {
	Kind     domain.ZoneKind          `json:"kind"`
	Crop     string                   `json:"crop,omitempty"`
	Preset   domain.StockpilePreset   `json:"preset,omitempty"`
	Priority domain.StockpilePriority `json:"priority,omitempty"`
	Cells    []ZoneCell               `json:"cells"`
	Allow    []string                 `json:"allow,omitempty"`
}
// ZoneEdit is the wire shape for the zone-edit command; only the fields the
// selected Op carries are populated (cells for add/remove, none for
// delete), mirroring Trade's discipline for its own closed sub-operations.
type ZoneEdit struct {
	ZoneID string             `json:"zoneId"`
	Before string             `json:"before"`
	Op     domain.ZoneEditOp  `json:"op"`
	Cells  []ZoneCell         `json:"cells,omitempty"`
}
// PopulationPolicy is the wire shape for the colony population capacity
// policy: a maximum colonist count and a minimum stored-food reserve in
// days. It is a configuration value rather than a plan action, so unlike
// ZoneEdit it carries no entity identity and no before-token.
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
// pair Python keeps in plan.control['resource_policy'][resource]. Unlike the
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
	return &Generation{s.Colony, s.Map, s.Load, s.Direction, s.Plan, s.Revision, s.Native}
}
