package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

type RangedPawnFacts struct {
	Pawn                              domain.PawnID
	SnapshotToken                     string
	Dead, Downed, Bleeding, NeedsTend domain.Fact[bool]
	HealthFraction                    domain.Fact[float64]
	FreeColonist, Drafted             domain.Fact[bool]
	// ViolenceCapable mirrors MeleePawnFacts. RangedWeaponEquipped requires the
	// pawn's primary equipped item to be a native ranged (non-explosive-only)
	// weapon; explosive launchers stay outside this contract regardless of how
	// native classifies them, so this fact alone never admits one.
	ViolenceCapable, RangedWeaponEquipped domain.Fact[bool]
	Owner                                 domain.Fact[MeleeDraftOwner]
}

type RangedTargetFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Hostile domain.Fact[bool]
}

type RangedDefenseFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  RangedPawnFacts
	Target                RangedTargetFacts
	NativeCanTry          domain.Fact[bool]
	Emergency             EmergencySnapshot
}

type RangedDefenseRequest struct {
	Action                  domain.Action
	Progress, DraftProgress domain.Progress
	Current                 domain.GenerationSnapshot
	MinimumTick             domain.Tick
	Facts                   RangedDefenseFacts
}
