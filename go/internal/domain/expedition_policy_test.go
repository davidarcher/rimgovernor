package domain

import (
	"math"
	"testing"
)

func TestExpeditionPolicyDefaultsAndBounds(t *testing.T) {
	t.Parallel()
	base := DefaultExpeditionPolicy()
	if !base.Set() || base.MinimumHomeColonists() != 1 || base.MinimumHomeFoodDays() != 0.5 ||
		base.TravelFoodMarginDays() != 0.5 || base.MaximumTravelDays() != 3 || base.MaximumCaravans() != 2 ||
		base.MinimumGoodwill() != -50 || base.MinimumDestinationTemperature() != -10 ||
		base.MaximumDestinationTemperature() != 40 || !base.KeepHomeDoctor() || !base.RequireReturnStorage() {
		t.Fatal("defaults must mirror the Python contract", base)
	}
	if (ExpeditionPolicy{}).Set() {
		t.Fatal("the zero value must report no policy")
	}
	if _, err := NewExpeditionPolicy(ExpeditionPolicyFields{}); err == nil {
		t.Fatal("the zero value must not be constructible")
	}
	for name, change := range map[string]func(*ExpeditionPolicyFields){
		"colonists low":  func(f *ExpeditionPolicyFields) { f.MinimumHomeColonists = 0 },
		"colonists high": func(f *ExpeditionPolicyFields) { f.MinimumHomeColonists = 101 },
		"home food low":  func(f *ExpeditionPolicyFields) { f.MinimumHomeFoodDays = -0.01 },
		"home food high": func(f *ExpeditionPolicyFields) { f.MinimumHomeFoodDays = 60.01 },
		"home food nan":  func(f *ExpeditionPolicyFields) { f.MinimumHomeFoodDays = math.NaN() },
		"margin low":     func(f *ExpeditionPolicyFields) { f.TravelFoodMarginDays = -1 },
		"margin high":    func(f *ExpeditionPolicyFields) { f.TravelFoodMarginDays = 30.01 },
		"margin inf":     func(f *ExpeditionPolicyFields) { f.TravelFoodMarginDays = math.Inf(1) },
		"travel zero":    func(f *ExpeditionPolicyFields) { f.MaximumTravelDays = 0 },
		"travel high":    func(f *ExpeditionPolicyFields) { f.MaximumTravelDays = 60.01 },
		"caravans low":   func(f *ExpeditionPolicyFields) { f.MaximumCaravans = 0 },
		"caravans high":  func(f *ExpeditionPolicyFields) { f.MaximumCaravans = 21 },
		"goodwill low":   func(f *ExpeditionPolicyFields) { f.MinimumGoodwill = -101 },
		"goodwill high":  func(f *ExpeditionPolicyFields) { f.MinimumGoodwill = 101 },
		"min temp low":   func(f *ExpeditionPolicyFields) { f.MinimumDestinationTemperature = -100.01 },
		"min temp high":  func(f *ExpeditionPolicyFields) { f.MinimumDestinationTemperature = 50.01 },
		"max temp low":   func(f *ExpeditionPolicyFields) { f.MaximumDestinationTemperature = -50.01 },
		"max temp high":  func(f *ExpeditionPolicyFields) { f.MaximumDestinationTemperature = 100.01 },
		"reversed window": func(f *ExpeditionPolicyFields) {
			f.MinimumDestinationTemperature, f.MaximumDestinationTemperature = 20, 10
		},
		"reversed at edge": func(f *ExpeditionPolicyFields) {
			f.MinimumDestinationTemperature, f.MaximumDestinationTemperature = 0, -0.01
		},
	} {
		fields := DefaultExpeditionPolicyFields()
		change(&fields)
		if _, err := NewExpeditionPolicy(fields); err == nil {
			t.Fatal("out-of-range expedition policy must be rejected:", name)
		}
	}
	// The exclusive lower bound on travel days and the inclusive zero lower
	// bounds on the two food fields are both load bearing.
	edge := DefaultExpeditionPolicyFields()
	edge.MaximumTravelDays, edge.MinimumHomeFoodDays, edge.TravelFoodMarginDays = 0.001, 0, 0
	edge.MinimumDestinationTemperature, edge.MaximumDestinationTemperature = 20, 20
	if _, err := NewExpeditionPolicy(edge); err != nil {
		t.Fatal(err)
	}
}

// A partial request keeps every limit it does not name.
func TestExpeditionPolicyPatchMergesOverBase(t *testing.T) {
	t.Parallel()
	if !(ExpeditionPolicyPatch{}).Empty() {
		t.Fatal("the zero patch must be empty")
	}
	if err := (ExpeditionPolicyPatch{}).Validate(); err == nil {
		t.Fatal("an empty request must be rejected")
	}
	// Merging over an unset base uses the contract defaults.
	first, err := ExpeditionPolicyPatch{MaximumTravelDays: Some(9.0)}.Apply(ExpeditionPolicy{})
	if err != nil || first.MaximumTravelDays() != 9 || first.MinimumHomeColonists() != 1 || !first.KeepHomeDoctor() {
		t.Fatal(first, err)
	}
	second, err := ExpeditionPolicyPatch{MinimumHomeColonists: Some(int32(7)), KeepHomeDoctor: Some(false)}.Apply(first)
	if err != nil {
		t.Fatal(err)
	}
	if second.MaximumTravelDays() != 9 || second.MinimumHomeColonists() != 7 || second.KeepHomeDoctor() ||
		!second.RequireReturnStorage() || second.MinimumGoodwill() != -50 {
		t.Fatal("unnamed limits must survive the merge", second)
	}
	// One end of the temperature window may be moved alone, and is checked
	// against the established other end rather than against a default.
	wide, err := ExpeditionPolicyPatch{MaximumDestinationTemperature: Some(55.0)}.Apply(second)
	if err != nil || wide.MaximumDestinationTemperature() != 55 || wide.MinimumDestinationTemperature() != -10 {
		t.Fatal(wide, err)
	}
	if _, err = (ExpeditionPolicyPatch{MinimumDestinationTemperature: Some(50.0)}).Apply(second); err == nil {
		t.Fatal("a lone end that reverses the established window must be rejected")
	}
	if _, err = (ExpeditionPolicyPatch{MaximumCaravans: Some(int32(0))}).Apply(second); err == nil {
		t.Fatal("an out-of-range supplied field must be rejected")
	}
}

// Validate checks only what the request supplies, and cross-checks the
// temperature window only when the request supplies both ends.
func TestExpeditionPolicyPatchValidatesSuppliedFieldsOnly(t *testing.T) {
	t.Parallel()
	if err := (ExpeditionPolicyPatch{MinimumDestinationTemperature: Some(45.0)}).Validate(); err != nil {
		t.Fatal("a lone in-range end has nothing to cross-check yet", err)
	}
	if err := (ExpeditionPolicyPatch{
		MinimumDestinationTemperature: Some(45.0),
		MaximumDestinationTemperature: Some(10.0),
	}).Validate(); err == nil {
		t.Fatal("a reversed window supplied in one request must be rejected")
	}
	for name, patch := range map[string]ExpeditionPolicyPatch{
		"colonists": {MinimumHomeColonists: Some(int32(101))},
		"home food": {MinimumHomeFoodDays: Some(61.0)},
		"margin":    {TravelFoodMarginDays: Some(-1.0)},
		"travel":    {MaximumTravelDays: Some(0.0)},
		"caravans":  {MaximumCaravans: Some(int32(21))},
		"goodwill":  {MinimumGoodwill: Some(int32(-101))},
		"min temp":  {MinimumDestinationTemperature: Some(51.0)},
		"max temp":  {MaximumDestinationTemperature: Some(-51.0)},
		"nan":       {MaximumTravelDays: Some(math.NaN())},
	} {
		if err := patch.Validate(); err == nil {
			t.Fatal("out-of-range supplied field must be rejected:", name)
		}
	}
	// Booleans carry no range, and false is a real supplied value rather
	// than an absent one.
	patch := ExpeditionPolicyPatch{RequireReturnStorage: Some(false)}
	if patch.Empty() || patch.Validate() != nil {
		t.Fatal("an explicit false must count as supplied")
	}
}

// An expedition policy is configuration, not a plan action.
func TestExpeditionPolicyIsNotAnActionKind(t *testing.T) {
	t.Parallel()
	for _, kind := range SupportedActionKinds() {
		if string(kind) == "expedition_policy" {
			t.Fatal("expedition policy must not be a dispatchable action kind")
		}
	}
}
