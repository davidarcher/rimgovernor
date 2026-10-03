package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Hack is one building's hack state (#1708) as its row carries it; every
// field is unknown when native did not read it. The static facts (defence,
// skill prerequisite, lockout) are the catalog's (bridge.OdysseyCatalog).
type Hack struct {
	// ProgressPercent is the hack progress, 0..1.
	ProgressPercent domain.Fact[float64]
	Hacked          domain.Fact[bool]
	LockedOut       domain.Fact[bool]
	Autohack        domain.Fact[bool]
}

// Portal is one map portal's state: whether its pocket map exists and its
// map id, and an ancient hatch's stockpile type and layout.
type Portal struct {
	PocketMapExists domain.Fact[bool]
	PocketMapID     domain.Fact[int]
	StockpileType   domain.Fact[string]
	Layout          domain.Fact[string]
}
