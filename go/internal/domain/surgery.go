package domain

import "errors"

const SurgeryAction ActionKind = "surgery"

// Surgery is explicit intent to queue one exact native medical operation
// (contracts/proto/operations.proto QueueSurgery) on one already-observed
// living patient. It reuses the same native HealthCardUtility.CreateSurgeryBill
// path the legacy JSON home/medical_operations tool (MedicalOperationsTool.cs)
// drives, so an order here is exactly the bill a player's operations tab would
// queue. Unlike Tend, there is no doctor role: native work selection assigns a
// practitioner from the queued bill. Native recipe/body-part eligibility,
// ingredient and practitioner availability, and current health/care policy are
// established at inspection, not here. Part is -1 for a whole-body recipe.
type Surgery struct {
	patient PawnID
	recipe  string
	part    int32
}

func NewSurgery(patient PawnID, recipe string, part int32) (Surgery, error) {
	if !validID(string(patient)) || !validID(recipe) || part < -1 {
		return Surgery{}, errors.New("surgery requires a valid patient, recipe and body part index")
	}
	return Surgery{patient: patient, recipe: recipe, part: part}, nil
}

func (s Surgery) Patient() PawnID { return s.patient }
func (s Surgery) Recipe() string  { return s.recipe }
func (s Surgery) Part() int32     { return s.part }

func NewSurgeryAction(id ActionID, surgery Surgery) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewSurgery(surgery.patient, surgery.recipe, surgery.part); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: SurgeryAction, surgery: surgery}, nil
}

func (a Action) Surgery() (Surgery, bool) { return a.surgery, a.kind == SurgeryAction }

// MedicalCare names the native patient medical care policy buckets exposed by
// contracts/proto/operations.proto's MedicalCare enum. It mirrors
// RecoveryMethod: domain cannot import policy, so this is the type facts,
// admissions and the bridge share.
type MedicalCare string

const (
	MedicalCareNoCare         MedicalCare = "no_care"
	MedicalCareNoMedicine     MedicalCare = "no_medicine"
	MedicalCareHerbalOrWorse  MedicalCare = "herbal_or_worse"
	MedicalCareNormalOrWorse  MedicalCare = "normal_or_worse"
	MedicalCareBest           MedicalCare = "best"
)

func ValidMedicalCare(care MedicalCare) bool {
	switch care {
	case MedicalCareNoCare, MedicalCareNoMedicine, MedicalCareHerbalOrWorse, MedicalCareNormalOrWorse, MedicalCareBest:
		return true
	default:
		return false
	}
}
