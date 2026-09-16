package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

type MeleeDraftOwner struct {
	Claim   domain.DraftClaimID
	Session domain.ControllerSessionID
}

type MeleePawnFacts struct {
	Pawn                              domain.PawnID
	SnapshotToken                     string
	Dead, Downed, Bleeding, NeedsTend domain.Fact[bool]
	HealthFraction                    domain.Fact[float64]
	FreeColonist, Drafted             domain.Fact[bool]
	ViolenceCapable, EquipmentKnown   domain.Fact[bool]
	Owner                             domain.Fact[MeleeDraftOwner]
}

type MeleeTargetFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Hostile domain.Fact[bool]
}

type MeleeDefenseFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  MeleePawnFacts
	Target                MeleeTargetFacts
	NativeCanTry          domain.Fact[bool]
	Emergency             EmergencySnapshot
}

type MeleeDefenseRequest struct {
	Action                  domain.Action
	Progress, DraftProgress domain.Progress
	Current                 domain.GenerationSnapshot
	MinimumTick             domain.Tick
	Facts                   MeleeDefenseFacts
}
