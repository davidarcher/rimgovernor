package domain

import (
	"errors"
	"strings"
)

// PawnSettingsAction changes one pawn setting through Actions/Apply. The selected arm
// determines whether the target is a colonist, mechanoid or held entity. Native validates
// eligibility and treats an already-matching setting as applied.
const PawnSettingsAction ActionKind = "pawn_settings"

// HostilityResponse is a vanilla HostilityResponseMode name.
type HostilityResponse string

const (
	HostilityIgnore HostilityResponse = "Ignore"
	HostilityAttack HostilityResponse = "Attack"
	HostilityFlee   HostilityResponse = "Flee"
)

// Valid reports whether h names a HostilityResponseMode.
func (h HostilityResponse) Valid() bool {
	return h == HostilityIgnore || h == HostilityAttack || h == HostilityFlee
}

// SettingKind names the PawnSettingsIntent arm a PawnSettings carries.
type SettingKind string

const (
	SettingHostility SettingKind = "hostility"
	SettingSelfTend  SettingKind = "self_tend"
	// SettingMedicineCarry is the Medicine inventory-stock count.
	SettingMedicineCarry SettingKind = "medicine_carry"
	// SettingNickname renames an owned pawn away from a short name an older
	// owned pawn holds; native draws the new name.
	SettingNickname SettingKind = "nickname"
	// SettingMedicalCare caps the medicine a doctor may use.
	SettingMedicalCare SettingKind = "medical_care"
	// SettingReadingPolicy assigns the reading policy labelled with the
	// pawn's short name.
	SettingReadingPolicy SettingKind = "reading_policy"
	// SettingDrugPolicy assigns the drug policy labelled with the pawn's
	// short name.
	SettingDrugPolicy SettingKind = "drug_policy"
	// SettingFoodPolicy assigns the food policy labelled with the pawn's
	// short name.
	SettingFoodPolicy SettingKind = "food_policy"
	// SettingMechWorkMode sets the MechWorkModeDef a mech's control group
	// runs.
	SettingMechWorkMode SettingKind = "mech_work_mode"
	// SettingMechControlGroup moves a mech into one of its overseer's
	// control groups.
	SettingMechControlGroup SettingKind = "mech_control_group"
	// SettingChoosePermit spends permit points on one permit of a faction.
	SettingChoosePermit SettingKind = "choose_permit"
	// SettingExtractBioferrite sets a held entity's CompHoldingPlatformTarget
	// extractBioferrite flag, which the game's own Doctor work giver
	// (WorkGiver_ExtractBioferrite) reads.
	SettingExtractBioferrite SettingKind = "extract_bioferrite"
)

// MedicalCare is a vanilla MedicalCareCategory name: the best medicine a
// doctor may use on the pawn.
type MedicalCare string

const (
	CareNone   MedicalCare = "NoCare"
	CareNoMeds MedicalCare = "NoMeds"
	CareHerbal MedicalCare = "HerbalOrWorse"
	CareNormal MedicalCare = "NormalOrWorse"
	CareBest   MedicalCare = "Best"
)

// MedicalCares lists every tier, lowest first.
var MedicalCares = []MedicalCare{CareNone, CareNoMeds, CareHerbal, CareNormal, CareBest}

// Rank is the tier's place in MedicalCares; -1 for an unknown name.
func (c MedicalCare) Rank() int {
	for i, v := range MedicalCares {
		if v == c {
			return i
		}
	}
	return -1
}

// Valid reports whether c names a MedicalCareCategory.
func (c MedicalCare) Valid() bool { return c.Rank() >= 0 }

// Raised is the next tier up; Best stays Best.
func (c MedicalCare) Raised() MedicalCare {
	if r := c.Rank(); r >= 0 && r+1 < len(MedicalCares) {
		return MedicalCares[r+1]
	}
	return c
}

// MaxMedicineCarry is the Medicine inventory-stock group's max (vanilla
// InventoryStockGroupDef Medicine: 0-3).
const MaxMedicineCarry = 3

// PawnSettings is an immutable, comparable value: the pawn and the one
// setting it should hold.
type PawnSettings struct {
	pawn      PawnID
	kind      SettingKind
	hostility HostilityResponse
	selfTend  bool
	carry     int
	leaveName string
	care      MedicalCare
	reading   string
	drug      string
	food      string
	mechMode  string
	mechGroup int
	faction   string
	permit    string
	extract   bool
}

// NewExtractBioferriteSetting sets the extract-bioferrite flag of the held
// entity pawn. Native refuses a pawn that is no entity on a holding
// platform.
func NewExtractBioferriteSetting(pawn PawnID, on bool) (PawnSettings, error) {
	if !validID(string(pawn)) {
		return PawnSettings{}, errors.New("an extract bioferrite setting requires a held entity")
	}
	return PawnSettings{pawn: pawn, kind: SettingExtractBioferrite, extract: on}, nil
}

// ExtractBioferrite is the flag value, and whether this is the extract bioferrite arm.
func (s PawnSettings) ExtractBioferrite() (on, ok bool) {
	return s.extract, s.kind == SettingExtractBioferrite
}

// NewChoosePermitSetting has the colonist take permit (a RoyalTitlePermitDef
// name) with faction (a FactionDef name) through the game's own permit
// checks. The faction def carries no ':' (the store joins the two).
func NewChoosePermitSetting(pawn PawnID, faction, permit string) (PawnSettings, error) {
	if !validID(string(pawn)) || !validID(faction) || strings.Contains(faction, ":") || !validID(permit) {
		return PawnSettings{}, errors.New("a choose permit setting requires a colonist, a faction def and a permit def")
	}
	return PawnSettings{pawn: pawn, kind: SettingChoosePermit, faction: faction, permit: permit}, nil
}

// ChoosePermit is the faction and permit defs, and whether this is the permit arm.
func (s PawnSettings) ChoosePermit() (faction, permit string, ok bool) {
	return s.faction, s.permit, s.kind == SettingChoosePermit
}

// NewMechWorkModeSetting sets the work mode (a MechWorkModeDef name) of the
// control group the mech pawn belongs to. Native refuses a pawn that
// is no mech, has no colonist overseer or names no such mode.
func NewMechWorkModeSetting(pawn PawnID, mode string) (PawnSettings, error) {
	if !validID(string(pawn)) || !validID(mode) {
		return PawnSettings{}, errors.New("a mech work mode setting requires a mech and a work mode def")
	}
	return PawnSettings{pawn: pawn, kind: SettingMechWorkMode, mechMode: mode}, nil
}

// MechWorkMode is the work mode def, and whether this is the mech work mode arm.
func (s PawnSettings) MechWorkMode() (string, bool) {
	return s.mechMode, s.kind == SettingMechWorkMode
}

// NewMechControlGroupSetting moves the mech pawn into its overseer's control
// group with the given index. Native refuses an index the overseer
// has no group for.
func NewMechControlGroupSetting(pawn PawnID, group int) (PawnSettings, error) {
	if !validID(string(pawn)) || group < 0 {
		return PawnSettings{}, errors.New("a mech control group setting requires a mech and a non-negative group index")
	}
	return PawnSettings{pawn: pawn, kind: SettingMechControlGroup, mechGroup: group}, nil
}

// MechControlGroup is the group index, and whether this is the control group arm.
func (s PawnSettings) MechControlGroup() (int, bool) {
	return s.mechGroup, s.kind == SettingMechControlGroup
}

// NewDrugPolicySetting assigns the drug policy labelled name.
func NewDrugPolicySetting(pawn PawnID, name string) (PawnSettings, error) {
	if !validID(string(pawn)) || !validID(name) || len(name) > 80 {
		return PawnSettings{}, errors.New("a drug policy setting requires a pawn and a policy name")
	}
	return PawnSettings{pawn: pawn, kind: SettingDrugPolicy, drug: name}, nil
}

// DrugPolicy is the policy label, and whether this is the drug arm.
func (s PawnSettings) DrugPolicy() (string, bool) { return s.drug, s.kind == SettingDrugPolicy }

// NewReadingPolicySetting assigns the reading policy labelled name.
func NewReadingPolicySetting(pawn PawnID, name string) (PawnSettings, error) {
	if !validID(string(pawn)) || !validID(name) || len(name) > 80 {
		return PawnSettings{}, errors.New("a reading policy setting requires a pawn and a policy name")
	}
	return PawnSettings{pawn: pawn, kind: SettingReadingPolicy, reading: name}, nil
}

// ReadingPolicy is the policy label, and whether this is the reading arm.
func (s PawnSettings) ReadingPolicy() (string, bool) {
	return s.reading, s.kind == SettingReadingPolicy
}

// NewFoodPolicySetting assigns the food policy labelled name.
func NewFoodPolicySetting(pawn PawnID, name string) (PawnSettings, error) {
	if !validID(string(pawn)) || !validID(name) || len(name) > 80 {
		return PawnSettings{}, errors.New("a food policy setting requires a pawn and a policy name")
	}
	return PawnSettings{pawn: pawn, kind: SettingFoodPolicy, food: name}, nil
}

// FoodPolicy is the policy label, and whether this is the food arm.
func (s PawnSettings) FoodPolicy() (string, bool) { return s.food, s.kind == SettingFoodPolicy }

// NewMedicineCarrySetting is the pawn's Medicine inventory-stock count,
// 0 to MaxMedicineCarry.
func NewMedicineCarrySetting(pawn PawnID, count int) (PawnSettings, error) {
	if !validID(string(pawn)) || count < 0 || count > MaxMedicineCarry {
		return PawnSettings{}, errors.New("a medicine carry setting requires a pawn and a count of 0-3")
	}
	return PawnSettings{pawn: pawn, kind: SettingMedicineCarry, carry: count}, nil
}

// MedicineCarry is the carry count, and whether this is the carry arm.
func (s PawnSettings) MedicineCarry() (int, bool) { return s.carry, s.kind == SettingMedicineCarry }

func NewHostilitySetting(pawn PawnID, mode HostilityResponse) (PawnSettings, error) {
	if !validID(string(pawn)) || !mode.Valid() {
		return PawnSettings{}, errors.New("a hostility setting requires a pawn and Ignore, Attack or Flee")
	}
	return PawnSettings{pawn: pawn, kind: SettingHostility, hostility: mode}, nil
}

// NewSelfTendSetting sets playerSettings.selfTend.
func NewSelfTendSetting(pawn PawnID, on bool) (PawnSettings, error) {
	if !validID(string(pawn)) {
		return PawnSettings{}, errors.New("a self-tend setting requires a pawn")
	}
	return PawnSettings{pawn: pawn, kind: SettingSelfTend, selfTend: on}, nil
}

// NewNicknameSetting renames an owned pawn away from leave, its colliding
// short name.
func NewNicknameSetting(pawn PawnID, leave string) (PawnSettings, error) {
	if !validID(string(pawn)) || strings.TrimSpace(leave) == "" || len(leave) > 256 {
		return PawnSettings{}, errors.New("a nickname setting requires a pawn and the short name it leaves")
	}
	return PawnSettings{pawn: pawn, kind: SettingNickname, leaveName: leave}, nil
}

// NewMedicalCareSetting caps the pawn's medical care.
func NewMedicalCareSetting(pawn PawnID, care MedicalCare) (PawnSettings, error) {
	if !validID(string(pawn)) || !care.Valid() {
		return PawnSettings{}, errors.New("a medical care setting requires a pawn and a MedicalCareCategory")
	}
	return PawnSettings{pawn: pawn, kind: SettingMedicalCare, care: care}, nil
}

func (s PawnSettings) Pawn() PawnID                 { return s.pawn }
func (s PawnSettings) Kind() SettingKind            { return s.kind }
func (s PawnSettings) Hostility() HostilityResponse { return s.hostility }
func (s PawnSettings) SelfTend() bool               { return s.selfTend }
func (s PawnSettings) MedicalCare() MedicalCare     { return s.care }

// LeaveName is the nickname arm's colliding short name.
func (s PawnSettings) LeaveName() string { return s.leaveName }

func canonicalPawnSettings(s PawnSettings) (PawnSettings, error) {
	switch s.kind {
	case SettingHostility:
		return NewHostilitySetting(s.pawn, s.hostility)
	case SettingSelfTend:
		return NewSelfTendSetting(s.pawn, s.selfTend)
	case SettingMedicineCarry:
		return NewMedicineCarrySetting(s.pawn, s.carry)
	case SettingNickname:
		return NewNicknameSetting(s.pawn, s.leaveName)
	case SettingMedicalCare:
		return NewMedicalCareSetting(s.pawn, s.care)
	case SettingReadingPolicy:
		return NewReadingPolicySetting(s.pawn, s.reading)
	case SettingDrugPolicy:
		return NewDrugPolicySetting(s.pawn, s.drug)
	case SettingFoodPolicy:
		return NewFoodPolicySetting(s.pawn, s.food)
	case SettingMechWorkMode:
		return NewMechWorkModeSetting(s.pawn, s.mechMode)
	case SettingMechControlGroup:
		return NewMechControlGroupSetting(s.pawn, s.mechGroup)
	case SettingChoosePermit:
		return NewChoosePermitSetting(s.pawn, s.faction, s.permit)
	case SettingExtractBioferrite:
		return NewExtractBioferriteSetting(s.pawn, s.extract)
	}
	return PawnSettings{}, errors.New("unknown pawn setting")
}

func NewPawnSettingsAction(id ActionID, s PawnSettings) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := canonicalPawnSettings(s)
	if err != nil || canonical != s {
		return Action{}, errors.New("invalid pawn settings")
	}
	return Action{id: id, kind: PawnSettingsAction, pawnSettings: s}, nil
}

func (a Action) PawnSettings() (PawnSettings, bool) {
	return a.pawnSettings, a.kind == PawnSettingsAction
}
