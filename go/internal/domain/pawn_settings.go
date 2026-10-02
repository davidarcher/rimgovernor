package domain

import (
	"errors"
	"strings"
)

// PawnSettingsAction sets one per-pawn Assign-tab setting on one colony pawn
// (#1299, epic #1292): a PawnSettingsIntent on Actions/Apply, one setting
// per intent: hostility response (#1299), self-tend (#1305), nickname
// (#1310), medicine carry (#1307), the medical care cap (#1301) and the
// reading policy (#1306) and the drug policy (#1537). Native treats a setting that
// already holds as applied.
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
	// SettingMedicineCarry is the Medicine inventory-stock count (#1307).
	SettingMedicineCarry SettingKind = "medicine_carry"
	// SettingNickname renames an owned pawn away from a short name an older
	// owned pawn holds (#1310); native draws the new name.
	SettingNickname SettingKind = "nickname"
	// SettingMedicalCare caps the medicine a doctor may use (#1301).
	SettingMedicalCare SettingKind = "medical_care"
	// SettingReadingPolicy assigns the reading policy labelled with the
	// pawn's short name (#1306).
	SettingReadingPolicy SettingKind = "reading_policy"
	// SettingDrugPolicy assigns the drug policy labelled with the pawn's
	// short name (#1537).
	SettingDrugPolicy SettingKind = "drug_policy"
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
}

// NewDrugPolicySetting assigns the drug policy labelled name (#1537).
func NewDrugPolicySetting(pawn PawnID, name string) (PawnSettings, error) {
	if !validID(string(pawn)) || !validID(name) || len(name) > 80 {
		return PawnSettings{}, errors.New("a drug policy setting requires a pawn and a policy name")
	}
	return PawnSettings{pawn: pawn, kind: SettingDrugPolicy, drug: name}, nil
}

// DrugPolicy is the policy label, and whether this is the drug arm.
func (s PawnSettings) DrugPolicy() (string, bool) { return s.drug, s.kind == SettingDrugPolicy }

// NewReadingPolicySetting assigns the reading policy labelled name (#1306).
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

// NewMedicineCarrySetting is the pawn's Medicine inventory-stock count
// (#1307), 0 to MaxMedicineCarry.
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

// NewSelfTendSetting sets playerSettings.selfTend (#1305).
func NewSelfTendSetting(pawn PawnID, on bool) (PawnSettings, error) {
	if !validID(string(pawn)) {
		return PawnSettings{}, errors.New("a self-tend setting requires a pawn")
	}
	return PawnSettings{pawn: pawn, kind: SettingSelfTend, selfTend: on}, nil
}

// NewNicknameSetting renames an owned pawn away from leave, its colliding
// short name (#1310).
func NewNicknameSetting(pawn PawnID, leave string) (PawnSettings, error) {
	if !validID(string(pawn)) || strings.TrimSpace(leave) == "" || len(leave) > 256 {
		return PawnSettings{}, errors.New("a nickname setting requires a pawn and the short name it leaves")
	}
	return PawnSettings{pawn: pawn, kind: SettingNickname, leaveName: leave}, nil
}

// NewMedicalCareSetting caps the pawn's medical care (#1301).
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
