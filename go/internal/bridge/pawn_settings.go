package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// pawnSettingsAction is the PawnSettingsIntent of one pawn and one setting
// (#1299). Native re-checks the pawn and the setting live; a setting that
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
		intent.Setting = &o.PawnSettingsIntent_HostilityResponse{HostilityResponse: string(v.Hostility())}
	case domain.SettingSelfTend:
		intent.Setting = &o.PawnSettingsIntent_SelfTend{SelfTend: v.SelfTend()}
	case domain.SettingNickname:
		if _, err := domain.NewNicknameSetting(v.Pawn(), v.LeaveName()); err != nil {
			return nil, contract("%v", err)
		}
		intent.Setting = &o.PawnSettingsIntent_Nickname{Nickname: v.LeaveName()}
	default:
		return nil, contract("unknown pawn setting")
	}
	return &o.Action{Intent: &o.Action_PawnSettings{PawnSettings: intent}}, nil
}
