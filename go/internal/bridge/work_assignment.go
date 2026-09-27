package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// workSettingsAction is the WorkSettingsIntent of one pawn's settings write:
// the drug policy alone, the medicine ceiling alone, or any of work
// priorities, allowed area, timetable and food additions together. Native
// checks the pawn and each field live when it applies; settings that
// already hold apply again.
func workSettingsAction(action domain.Action) (*op.Action, error) {
	w, ok := action.WorkAssignment()
	if !ok {
		return nil, contract("not a work assignment action")
	}
	if canonical, err := w.Canonical(); err != nil || canonical != w {
		return nil, contract("invalid work assignment")
	}
	return &op.Action{Intent: &op.Action_WorkSettings{WorkSettings: workSettingsIntent(w)}}, nil
}

func workSettingsIntent(w domain.WorkAssignment) *op.WorkSettingsIntent {
	intent := &op.WorkSettingsIntent{PawnId: proto.String(string(w.Pawn()))}
	if w.DrugPolicy() != "" {
		intent.DrugPolicy = proto.String(w.DrugPolicy())
		return intent
	}
	switch w.MedicalCare() {
	case "NoMeds":
		intent.MedicalCare = op.MedicalCare_MEDICAL_CARE_NO_MEDICINE.Enum()
	case "HerbalOrWorse":
		intent.MedicalCare = op.MedicalCare_MEDICAL_CARE_HERBAL_OR_WORSE.Enum()
	case "NormalOrWorse":
		intent.MedicalCare = op.MedicalCare_MEDICAL_CARE_NORMAL_OR_WORSE.Enum()
	}
	for _, setting := range w.Settings() {
		intent.Work = append(intent.Work, &op.WorkPriority{WorkTypeDef: proto.String(setting.Definition), Priority: proto.Int32(setting.Priority)})
	}
	if w.HasArea() {
		if w.AreaClear() {
			intent.AllowedArea = &op.Assignment{Value: &op.Assignment_Clear{Clear: &op.Clear{}}}
		} else {
			intent.AllowedArea = &op.Assignment{Value: &op.Assignment_EntityId{EntityId: w.Area()}}
		}
	}
	if w.HasSchedule() {
		intent.Schedule = &op.Schedule{AssignmentDefs: append([]string(nil), w.Schedule()...)}
	}
	if defs := w.FoodAllow(); len(defs) > 0 {
		intent.FoodAllow = &op.DefinitionList{Defs: defs}
	}
	return intent
}
