package domain

import (
	"errors"
	"strings"
)

// PawnSettingsAction sets one per-pawn Assign-tab setting on one colony pawn
// (#1299, epic #1292): a PawnSettingsIntent on Actions/Apply, one setting
// per intent. The hostility response (#1299), self-tend (#1305) and
// medicine carry (#1307) are carried so far; the other intent arms
// (reading policy, nickname) land with their epic issues. Native treats a setting that
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
)

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

func (s PawnSettings) Pawn() PawnID                 { return s.pawn }
func (s PawnSettings) Kind() SettingKind            { return s.kind }
func (s PawnSettings) Hostility() HostilityResponse { return s.hostility }
func (s PawnSettings) SelfTend() bool               { return s.selfTend }

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
