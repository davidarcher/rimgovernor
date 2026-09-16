package domain

import (
	"errors"
	"math"
)

// Expedition policy bounds, including maximumTravelDays' exclusive lower bound
// and the two asymmetric destination-temperature windows.
const (
	minExpeditionHomeColonists    int32 = 1
	maxExpeditionHomeColonists    int32 = 100
	maxExpeditionHomeFoodDays           = 60.0
	maxExpeditionTravelMarginDays       = 30.0
	maxExpeditionTravelDays             = 60.0
	minExpeditionCaravans         int32 = 1
	maxExpeditionCaravans         int32 = 20
	minExpeditionGoodwill         int32 = -100
	maxExpeditionGoodwill         int32 = 100
	minExpeditionTemperatureLow         = -100.0
	minExpeditionTemperatureHigh        = 50.0
	maxExpeditionTemperatureLow         = -50.0
	maxExpeditionTemperatureHigh        = 100.0
)

// Optional carries a value that a partial player request may or may not have
// supplied. It is deliberately a comparable value rather than a pointer: a
// request carrying one must stay comparable with ==, which both the store's
// request-ID replay check and the player gate's idempotency check depend on,
// and pointer fields would compare identity rather than intent.
type Optional[T comparable] struct {
	value   T
	present bool
}

// Some marks a field as explicitly supplied by the player.
func Some[T comparable](value T) Optional[T] { return Optional[T]{value, true} }

// Get returns the supplied value and whether it was supplied at all.
func (o Optional[T]) Get() (T, bool) { return o.value, o.present }

// Present reports whether the player supplied this field.
func (o Optional[T]) Present() bool { return o.present }

// ExpeditionPolicyFields is the full, plainly readable set of expedition risk
// limits. It exists so ExpeditionPolicy can stay a closed value with a single
// validating constructor without that constructor taking ten positional
// arguments, and so a merge can be expressed as ordinary field assignment.
type ExpeditionPolicyFields struct {
	// MinimumHomeColonists is the colonist count that must remain at home.
	MinimumHomeColonists int32
	// MinimumHomeFoodDays is the food runway the remaining home colony must
	// still hold after a caravan departs, in days.
	MinimumHomeFoodDays float64
	// TravelFoodMarginDays is the reserve a party must carry past its own
	// route estimate, in days.
	TravelFoodMarginDays float64
	// MaximumTravelDays is the player's travel time budget, in days.
	MaximumTravelDays float64
	// MaximumCaravans is the number of concurrent expeditions permitted.
	MaximumCaravans int32
	// MinimumGoodwill is the faction goodwill a destination must hold.
	MinimumGoodwill int32
	// MinimumDestinationTemperature and MaximumDestinationTemperature bound
	// the destination temperature window, in degrees Celsius.
	MinimumDestinationTemperature float64
	MaximumDestinationTemperature float64
	// KeepHomeDoctor requires a doctor to remain at the home map.
	KeepHomeDoctor bool
	// RequireReturnStorage requires storage for a returning party's haul.
	RequireReturnStorage bool
}

// ExpeditionPolicy is an immutable, comparable colony configuration value:
// the player's expedition risk limits.
//
// Like PopulationPolicy it deliberately has no ActionKind, no action
// constructor and no executor/bridge boundary. Setting expedition limits
// issues no native RimWorld call, so there is no CAS token to hold, no
// receipt to verify and no completing Observation to require. See store.SubmitExpeditionPolicy for the
// persistence side and interpreter.Guidance.ExpeditionPolicy for the chat
// nudge that feeds it.
//
// This is a distinct concept from policy.CaravanDeparturePolicy and
// policy.WorldEvaluationPolicy. Those two are hardcoded, read-only, narrower
// subsets of the same contract, each consumed by one internal
// admission or evaluation function. This value is the writable player-facing
// whole. Making those two read from this store is a separate refactor and is
// deliberately not attempted here.
type ExpeditionPolicy struct {
	fields ExpeditionPolicyFields
}

// DefaultExpeditionPolicyFields are the defaults in force when the player has
// never set a policy for the plan at all.
func DefaultExpeditionPolicyFields() ExpeditionPolicyFields {
	return ExpeditionPolicyFields{
		MinimumHomeColonists:          1,
		MinimumHomeFoodDays:           0.5,
		TravelFoodMarginDays:          0.5,
		MaximumTravelDays:             3,
		MaximumCaravans:               2,
		MinimumGoodwill:               -50,
		MinimumDestinationTemperature: -10,
		MaximumDestinationTemperature: 40,
		KeepHomeDoctor:                true,
		RequireReturnStorage:          true,
	}
}

// DefaultExpeditionPolicy is the policy a world has before the player has
// ever submitted one. It is the base a first partial patch merges onto.
func DefaultExpeditionPolicy() ExpeditionPolicy {
	return ExpeditionPolicy{DefaultExpeditionPolicyFields()}
}

// NewExpeditionPolicy bounds every field the way the player command contract
// does and enforces the one cross-field rule: the destination temperature
// window may not be reversed.
func NewExpeditionPolicy(fields ExpeditionPolicyFields) (ExpeditionPolicy, error) {
	if err := boundedInt32(fields.MinimumHomeColonists, minExpeditionHomeColonists, maxExpeditionHomeColonists, "minimum home colonists"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedFloat(fields.MinimumHomeFoodDays, 0, maxExpeditionHomeFoodDays, false, "minimum home food days"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedFloat(fields.TravelFoodMarginDays, 0, maxExpeditionTravelMarginDays, false, "travel food margin days"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedFloat(fields.MaximumTravelDays, 0, maxExpeditionTravelDays, true, "maximum travel days"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedInt32(fields.MaximumCaravans, minExpeditionCaravans, maxExpeditionCaravans, "maximum caravans"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedInt32(fields.MinimumGoodwill, minExpeditionGoodwill, maxExpeditionGoodwill, "minimum goodwill"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedFloat(fields.MinimumDestinationTemperature, minExpeditionTemperatureLow, minExpeditionTemperatureHigh, false, "minimum destination temperature"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if err := boundedFloat(fields.MaximumDestinationTemperature, maxExpeditionTemperatureLow, maxExpeditionTemperatureHigh, false, "maximum destination temperature"); err != nil {
		return ExpeditionPolicy{}, err
	}
	if fields.MinimumDestinationTemperature > fields.MaximumDestinationTemperature {
		return ExpeditionPolicy{}, errors.New("destination temperature limits are reversed")
	}
	return ExpeditionPolicy{fields}, nil
}

func boundedInt32(value, low, high int32, name string) error {
	if value < low || value > high {
		return errors.New(name + " out of range")
	}
	return nil
}

func boundedFloat(value, low, high float64, exclusiveLow bool, name string) error {
	if math.IsNaN(value) || math.IsInf(value, 0) || value > high || value < low || (exclusiveLow && value == low) {
		return errors.New(name + " out of range")
	}
	return nil
}

// Fields returns a copy of the policy's values.
func (p ExpeditionPolicy) Fields() ExpeditionPolicyFields { return p.fields }

// Set reports whether this is a real policy. The zero value is not
// constructible through NewExpeditionPolicy (MinimumHomeColonists 0 is out of
// range), so it unambiguously means "no policy".
func (p ExpeditionPolicy) Set() bool { return p != ExpeditionPolicy{} }

func (p ExpeditionPolicy) MinimumHomeColonists() int32 { return p.fields.MinimumHomeColonists }
func (p ExpeditionPolicy) MinimumHomeFoodDays() float64 {
	return p.fields.MinimumHomeFoodDays
}
func (p ExpeditionPolicy) TravelFoodMarginDays() float64 { return p.fields.TravelFoodMarginDays }
func (p ExpeditionPolicy) MaximumTravelDays() float64    { return p.fields.MaximumTravelDays }
func (p ExpeditionPolicy) MaximumCaravans() int32        { return p.fields.MaximumCaravans }
func (p ExpeditionPolicy) MinimumGoodwill() int32        { return p.fields.MinimumGoodwill }
func (p ExpeditionPolicy) MinimumDestinationTemperature() float64 {
	return p.fields.MinimumDestinationTemperature
}
func (p ExpeditionPolicy) MaximumDestinationTemperature() float64 {
	return p.fields.MaximumDestinationTemperature
}
func (p ExpeditionPolicy) KeepHomeDoctor() bool       { return p.fields.KeepHomeDoctor }
func (p ExpeditionPolicy) RequireReturnStorage() bool { return p.fields.RequireReturnStorage }

// ExpeditionPolicyPatch is one explicit player request to change some of the
// expedition limits. Unlike PopulationPolicy's full replacement, the patch
// is merged over the
// policy in force, so a request that names only maximumTravelDays must leave
// every other limit exactly as it stands. Absence is therefore meaningful and
// is carried here by Optional rather than by a sentinel value.
type ExpeditionPolicyPatch struct {
	MinimumHomeColonists          Optional[int32]
	MinimumHomeFoodDays           Optional[float64]
	TravelFoodMarginDays          Optional[float64]
	MaximumTravelDays             Optional[float64]
	MaximumCaravans               Optional[int32]
	MinimumGoodwill               Optional[int32]
	MinimumDestinationTemperature Optional[float64]
	MaximumDestinationTemperature Optional[float64]
	KeepHomeDoctor                Optional[bool]
	RequireReturnStorage          Optional[bool]
}

// Empty reports whether the request would change nothing.
func (q ExpeditionPolicyPatch) Empty() bool { return q == ExpeditionPolicyPatch{} }

// Validate range-checks only the fields the request actually supplies, and
// applies the destination temperature cross-check only when the request
// supplies both ends. A request naming one end alone is checked against the
// other end at merge time, in Apply, because only then is the value it will
// be compared against known.
func (q ExpeditionPolicyPatch) Validate() error {
	if q.Empty() {
		return errors.New("expedition policy request changes nothing")
	}
	if err := optionalInt32(q.MinimumHomeColonists, minExpeditionHomeColonists, maxExpeditionHomeColonists, "minimum home colonists"); err != nil {
		return err
	}
	if err := optionalFloat(q.MinimumHomeFoodDays, 0, maxExpeditionHomeFoodDays, false, "minimum home food days"); err != nil {
		return err
	}
	if err := optionalFloat(q.TravelFoodMarginDays, 0, maxExpeditionTravelMarginDays, false, "travel food margin days"); err != nil {
		return err
	}
	if err := optionalFloat(q.MaximumTravelDays, 0, maxExpeditionTravelDays, true, "maximum travel days"); err != nil {
		return err
	}
	if err := optionalInt32(q.MaximumCaravans, minExpeditionCaravans, maxExpeditionCaravans, "maximum caravans"); err != nil {
		return err
	}
	if err := optionalInt32(q.MinimumGoodwill, minExpeditionGoodwill, maxExpeditionGoodwill, "minimum goodwill"); err != nil {
		return err
	}
	if err := optionalFloat(q.MinimumDestinationTemperature, minExpeditionTemperatureLow, minExpeditionTemperatureHigh, false, "minimum destination temperature"); err != nil {
		return err
	}
	if err := optionalFloat(q.MaximumDestinationTemperature, maxExpeditionTemperatureLow, maxExpeditionTemperatureHigh, false, "maximum destination temperature"); err != nil {
		return err
	}
	low, hasLow := q.MinimumDestinationTemperature.Get()
	high, hasHigh := q.MaximumDestinationTemperature.Get()
	if hasLow && hasHigh && low > high {
		return errors.New("destination temperature limits are reversed")
	}
	return nil
}

func optionalInt32(o Optional[int32], low, high int32, name string) error {
	if value, ok := o.Get(); ok {
		return boundedInt32(value, low, high, name)
	}
	return nil
}

func optionalFloat(o Optional[float64], low, high float64, exclusiveLow bool, name string) error {
	if value, ok := o.Get(); ok {
		return boundedFloat(value, low, high, exclusiveLow, name)
	}
	return nil
}

// Apply merges the request over the policy in force and validates the whole
// result, which is what makes an unset field keep its established value. An
// unset base (a world whose player has never submitted a policy) merges over
// DefaultExpeditionPolicy.
func (q ExpeditionPolicyPatch) Apply(base ExpeditionPolicy) (ExpeditionPolicy, error) {
	if err := q.Validate(); err != nil {
		return ExpeditionPolicy{}, err
	}
	if !base.Set() {
		base = DefaultExpeditionPolicy()
	}
	fields := base.fields
	if value, ok := q.MinimumHomeColonists.Get(); ok {
		fields.MinimumHomeColonists = value
	}
	if value, ok := q.MinimumHomeFoodDays.Get(); ok {
		fields.MinimumHomeFoodDays = value
	}
	if value, ok := q.TravelFoodMarginDays.Get(); ok {
		fields.TravelFoodMarginDays = value
	}
	if value, ok := q.MaximumTravelDays.Get(); ok {
		fields.MaximumTravelDays = value
	}
	if value, ok := q.MaximumCaravans.Get(); ok {
		fields.MaximumCaravans = value
	}
	if value, ok := q.MinimumGoodwill.Get(); ok {
		fields.MinimumGoodwill = value
	}
	if value, ok := q.MinimumDestinationTemperature.Get(); ok {
		fields.MinimumDestinationTemperature = value
	}
	if value, ok := q.MaximumDestinationTemperature.Get(); ok {
		fields.MaximumDestinationTemperature = value
	}
	if value, ok := q.KeepHomeDoctor.Get(); ok {
		fields.KeepHomeDoctor = value
	}
	if value, ok := q.RequireReturnStorage.Get(); ok {
		fields.RequireReturnStorage = value
	}
	return NewExpeditionPolicy(fields)
}
