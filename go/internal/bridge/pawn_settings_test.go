package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A hostility setting builds one PawnSettingsIntent with the
// hostility_response arm (#1299).
func TestPawnSettingsBuildsHostilityIntent(t *testing.T) {
	value, err := domain.NewHostilitySetting("Human1", domain.HostilityFlee)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewPawnSettingsAction("a1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("pawn settings is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	s := wire.GetPawnSettings()
	if s.GetPawnId() != "Human1" || s.GetHostilityResponse() != "Flee" {
		t.Fatalf("%v", wire)
	}
}

// A self-tend setting builds the self_tend arm (#1305).
func TestPawnSettingsBuildsSelfTendIntent(t *testing.T) {
	value, err := domain.NewSelfTendSetting("Human1", true)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewPawnSettingsAction("a1", value)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if s := wire.GetPawnSettings(); s.GetPawnId() != "Human1" || !s.GetSelfTend() || s.GetHostilityResponse() != "" {
		t.Fatalf("%v", wire)
	}
}
