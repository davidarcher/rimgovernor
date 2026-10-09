package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

var medicalCareWire = map[domain.MedicalCare]o.MedicalCare{
	domain.CareNone:   o.MedicalCare_MEDICAL_CARE_NO_CARE,
	domain.CareNoMeds: o.MedicalCare_MEDICAL_CARE_NO_MEDICINE,
	domain.CareHerbal: o.MedicalCare_MEDICAL_CARE_HERBAL_OR_WORSE,
	domain.CareNormal: o.MedicalCare_MEDICAL_CARE_NORMAL_OR_WORSE,
	domain.CareBest:   o.MedicalCare_MEDICAL_CARE_BEST,
}

// pawnSettingsAction is the PawnSettingsIntent of one pawn and one setting.
// Native re-checks the pawn and the setting live; a setting that
// already holds applies again (NativePawnSettings.cs).
func pawnSettingsAction(action domain.Action) (*o.Action, error) {
	v, ok := action.PawnSettings()
	if !ok {
		return nil, contract("not a pawn settings action")
	}
	intent := &o.PawnSettingsIntent{PawnId: proto.String(string(v.Pawn()))}
	switch v.Kind() {
	case domain.SettingHostility:
		if _, err := domain.NewHostilitySetting(v.Pawn(), v.Hostility()); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_HostilityResponse{HostilityResponse: hostilityWire[v.Hostility()]}
	case domain.SettingSelfTend:
		intent.Setting = &o.PawnSettingsIntent_SelfTend{SelfTend: v.SelfTend()}
	case domain.SettingMedicineCarry:
		count, _ := v.MedicineCarry()
		intent.Setting = &o.PawnSettingsIntent_MedicineCarry{MedicineCarry: int32(count)}
	case domain.SettingNickname:
		if _, err := domain.NewNicknameSetting(v.Pawn(), v.LeaveName()); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_Nickname{Nickname: v.LeaveName()}
	case domain.SettingMedicalCare:
		care, ok := medicalCareWire[v.MedicalCare()]
		if !ok {
			return nil, contract("unknown medical care %q", v.MedicalCare())
		}
		intent.Setting = &o.PawnSettingsIntent_MedicalCare{MedicalCare: care}
	case domain.SettingReadingPolicy:
		name, _ := v.ReadingPolicy()
		if _, err := domain.NewReadingPolicySetting(v.Pawn(), name); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_ReadingPolicy{ReadingPolicy: name}
	case domain.SettingDrugPolicy:
		name, _ := v.DrugPolicy()
		if _, err := domain.NewDrugPolicySetting(v.Pawn(), name); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_DrugPolicy{DrugPolicy: name}
	case domain.SettingFoodPolicy:
		name, _ := v.FoodPolicy()
		if _, err := domain.NewFoodPolicySetting(v.Pawn(), name); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_FoodPolicy{FoodPolicy: name}
	case domain.SettingMechWorkMode:
		mode, _ := v.MechWorkMode()
		if _, err := domain.NewMechWorkModeSetting(v.Pawn(), mode); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_MechWorkMode{MechWorkMode: mode}
	case domain.SettingMechControlGroup:
		group, _ := v.MechControlGroup()
		if _, err := domain.NewMechControlGroupSetting(v.Pawn(), group); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_MechControlGroup{MechControlGroup: int32(group)}
	case domain.SettingChoosePermit:
		faction, permit, _ := v.ChoosePermit()
		if _, err := domain.NewChoosePermitSetting(v.Pawn(), faction, permit); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_ChoosePermit{ChoosePermit: &o.PermitChoice{FactionDef: proto.String(faction), Permit: proto.String(permit)}}
	case domain.SettingExtractBioferrite:
		on, _ := v.ExtractBioferrite()
		intent.Setting = &o.PawnSettingsIntent_ExtractBioferrite{ExtractBioferrite: on}
	default:
		return nil, contract("unknown pawn setting")
	}
	return &o.Action{Intent: &o.Action_PawnSettings{PawnSettings: intent}}, nil
}
