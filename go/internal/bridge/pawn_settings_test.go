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

// Every medical care tier builds the medical_care arm (#1301).
func TestPawnSettingsBuildsMedicalCareIntent(t *testing.T) {
	for _, care := range domain.MedicalCares {
		value, err := domain.NewMedicalCareSetting("Human1", care)
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
		s := wire.GetPawnSettings()
		if s.GetPawnId() != "Human1" || s.GetMedicalCare() != medicalCareWire[care] || s.GetMedicalCare() == 0 {
			t.Fatalf("%s: %v", care, wire)
		}
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

// A medicine carry setting builds the medicine_carry arm (#1307), zero included.
func TestPawnSettingsBuildsMedicineCarryIntent(t *testing.T) {
	value, err := domain.NewMedicineCarrySetting("Human1", 0)
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
	if s := wire.GetPawnSettings(); s.GetPawnId() != "Human1" || s.GetMedicineCarry() != 0 || s.GetSetting() == nil || s.GetHostilityResponse() != "" {
		t.Fatalf("%v", wire)
	}
}
