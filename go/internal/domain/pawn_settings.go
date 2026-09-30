package domain

import "errors"

// PawnSettingsAction sets one per-pawn Assign-tab setting on one colony pawn
// (#1299, epic #1292): a PawnSettingsIntent on Actions/Apply, one setting
// per intent. Only the hostility response is carried so far; the other
// intent arms (self-tend, reading policy, medicine carry, nickname) land
// with their epic issues. Native treats a setting that already holds as
// applied.
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

// PawnSettings is an immutable, comparable value: the pawn and the one
// setting it should hold.
type PawnSettings struct {
	pawn      PawnID
	hostility HostilityResponse
}

func NewHostilitySetting(pawn PawnID, mode HostilityResponse) (PawnSettings, error) {
	if !validID(string(pawn)) || !mode.Valid() {
		return PawnSettings{}, errors.New("a hostility setting requires a pawn and Ignore, Attack or Flee")
	}
	return PawnSettings{pawn: pawn, hostility: mode}, nil
}

func (s PawnSettings) Pawn() PawnID                 { return s.pawn }
func (s PawnSettings) Hostility() HostilityResponse { return s.hostility }

func NewPawnSettingsAction(id ActionID, s PawnSettings) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewHostilitySetting(s.pawn, s.hostility)
	if err != nil || canonical != s {
		return Action{}, errors.New("invalid pawn settings")
	}
	return Action{id: id, kind: PawnSettingsAction, pawnSettings: s}, nil
}

func (a Action) PawnSettings() (PawnSettings, bool) {
	return a.pawnSettings, a.kind == PawnSettingsAction
}
