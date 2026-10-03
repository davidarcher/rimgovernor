package bridge

import (
	"testing"

	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"

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
	if s.GetPawnId() != "Human1" || s.GetHostilityResponse() != op.HostilityResponse_HOSTILITY_RESPONSE_FLEE {
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
	if s := wire.GetPawnSettings(); s.GetPawnId() != "Human1" || !s.GetSelfTend() || s.GetHostilityResponse() != op.HostilityResponse_HOSTILITY_RESPONSE_UNSPECIFIED {
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
	if s := wire.GetPawnSettings(); s.GetPawnId() != "Human1" || s.GetMedicineCarry() != 0 || s.GetSetting() == nil || s.GetHostilityResponse() != op.HostilityResponse_HOSTILITY_RESPONSE_UNSPECIFIED {
		t.Fatalf("%v", wire)
	}
}

// The mech settings build the mech_work_mode and mech_control_group arms (#1685).
func TestPawnSettingsBuildsMechIntents(t *testing.T) {
	mode, err := domain.NewMechWorkModeSetting("Mech1", "Work")
	if err != nil {
		t.Fatal(err)
	}
	group, err := domain.NewMechControlGroupSetting("Mech1", 2)
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		v    domain.PawnSettings
		want func(*op.PawnSettingsIntent) bool
	}{
		{mode, func(s *op.PawnSettingsIntent) bool { return s.GetMechWorkMode() == "Work" }},
		{group, func(s *op.PawnSettingsIntent) bool { return s.GetMechControlGroup() == 2 && s.GetMechWorkMode() == "" }},
	} {
		action, err := domain.NewPawnSettingsAction("a1", c.v)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		if s := wire.GetPawnSettings(); s.GetPawnId() != "Mech1" || !c.want(s) {
			t.Fatalf("%v", wire)
		}
	}
}
