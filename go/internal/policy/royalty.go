package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// RoyaltyFacts are the Empire title ladder, the permit catalog and the
// colonists' holdings (#1599), decoded from the native royalty read. Every
// scalar the native read left absent is unknown.
type RoyaltyFacts struct {
	// Ladder lists every title by ascending seniority.
	Ladder  []RoyalRung
	Permits map[string]RoyalPermit
	// Holders maps a colonist to its holdings, one per faction.
	Holders map[PawnID][]RoyalHolding
	// Psycasts maps a psycaster to its known psycasts (#1600).
	Psycasts map[PawnID][]Psycast
	// Neuroformers is the colony's neuroformer stock by def (#1600).
	Neuroformers map[string]Neuroformer
	// Ceremonies are the pending bestowing ceremonies (#1602), by pawn.
	Ceremonies []BestowingCeremony
}

// PsycastTarget is what a psycast aims at.
type PsycastTarget string

const (
	PsycastTargetSelf  PsycastTarget = "self"
	PsycastTargetPawn  PsycastTarget = "pawn"
	PsycastTargetThing PsycastTarget = "thing"
	PsycastTargetCell  PsycastTarget = "cell"
)

// Psycast is one known psycast: Level is the psylink level that unlocks it,
// PsyfocusCost the 0-1 Psyfocus it spends, Entropy the neural heat it adds and
// Target is empty when native did not classify it.
type Psycast struct {
	Def           string
	Level         domain.Fact[int]
	PsyfocusCost  domain.Fact[float64]
	Entropy       domain.Fact[float64]
	Target        PsycastTarget
	CooldownTicks domain.Fact[int]
}

// Neuroformer is the stock of one neuroformer def: the psylink neuroformer or
// a psycast neurotrainer (TeachesPsycast names its ability). Held counts
// unforbidden stacks on the home maps; Craftable is an available recipe;
// Tradeable is a trader selling it.
type Neuroformer struct {
	Def            string
	TeachesPsycast string
	Held           domain.Fact[int]
	Craftable      domain.Fact[bool]
	Tradeable      domain.Fact[bool]
}

// RoyalRung is one title on the ladder; FavorNeeded is the favor that earns it.
//
// The Throne fields are the title's throne-room requirement: minimum
// impressiveness and area (unknown when the read left them absent), the
// throne definitions it accepts (empty when it needs no throne) and whether
// the throne must be assigned to the holder.
type RoyalRung struct {
	Title                   string
	Seniority               domain.Fact[int]
	FavorNeeded             domain.Fact[int]
	ThroneMinImpressiveness domain.Fact[int]
	ThroneMinArea           domain.Fact[int]
	ThroneThings            []string
	ThroneAssigned          domain.Fact[bool]
	// Bedroom* are the title's bedroom requirements: an absent area or
	// impressiveness is none, and no BedroomThings needs no furniture.
	BedroomMinArea           domain.Fact[int]
	BedroomMinImpressiveness domain.Fact[int]
	BedroomFloored           domain.Fact[bool]
	BedroomThings            []BedroomThing
}

// RoyalPermit is one permit definition. Acts marks a permit the holder calls
// (rather than a passive one); FavorCost is then the favor a call spends and
// unknown for a passive permit.
type RoyalPermit struct {
	Name         string
	MinTitle     domain.Fact[string]
	PermitPoints domain.Fact[int]
	Acts         domain.Fact[bool]
	FavorCost    domain.Fact[int]
	CooldownDays domain.Fact[float64]
}

// RoyalHolding is a colonist's standing with one faction. Title is empty for
// favor or permits without a current title.
type RoyalHolding struct {
	FactionDef   string
	Title        string
	Favor        domain.Fact[int]
	PermitPoints domain.Fact[int]
	Permits      []string
	// Cooldowns is the native cooldown of each held permit, by permit def
	// (#1607).
	Cooldowns map[string]PermitCooldown
}

// PermitCooldown is one held permit's native cooldown at the read's tick.
// LastUsedTick is unknown when the permit was never used; RemainingTicks is 0
// when it can be used.
type PermitCooldown struct {
	LastUsedTick   domain.Fact[int]
	RemainingTicks domain.Fact[int]
}

// PermitUsedSince reports whether the pawn's permit for the faction was used
// at or after tick since, from a royalty read taken after the attempt: the
// replay rule's way to resolve an uncertain ability receipt (an effective use
// is never re-sent). An unknown cooldown is false and the caller re-reads.
func (f *RoyaltyFacts) PermitUsedSince(pawn PawnID, faction, permit string, since int) bool {
	if f == nil {
		return false
	}
	for _, h := range f.Holders[pawn] {
		if h.FactionDef != faction {
			continue
		}
		if last, ok := h.Cooldowns[permit].LastUsedTick.Value(); ok && last >= since {
			return true
		}
	}
	return false
}
