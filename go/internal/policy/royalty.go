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
}
