package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ShrineGuardKind classifies what a breached shrine released.
type ShrineGuardKind = string

const (
	ShrineGuardMechanoid  ShrineGuardKind = "mechanoid"
	ShrineGuardInsectoid  ShrineGuardKind = "insectoid"
	ShrineGuardFleshbeast ShrineGuardKind = "fleshbeast"
	ShrineGuardHuman      ShrineGuardKind = "human"
	ShrineGuardHive       ShrineGuardKind = "hive"
	ShrineGuardOther      ShrineGuardKind = "other"
)

// ShrineCasket is one ancient cryptosleep casket. HasContents is the native
// fact; who is inside stays unknown until the casket opens. A casket under
// 20% hit points explodes, so HitPoints is a safety reading, not trivia.
type ShrineCasket struct {
	EntityID                string
	Cell                    domain.Cell
	HitPoints, MaxHitPoints uint32
	HasContents             bool
	PlayerClaimed           bool
}

type ShrineGuard struct {
	EntityID     string
	Kind         ShrineGuardKind
	Downed, Dead bool
}

// ShrineBreachWall is a perimeter wall the player may deconstruct without a
// roof-support blocker; Outside is the adjacent cell beyond the room and
// DefName the wall's definition (empty from a native before it was read).
type ShrineBreachWall struct {
	EntityID, DefName string
	Cell, Outside     domain.Cell
}

// AncientShrine is one ancient-danger room as observed. Guards is complete
// only when GuardsKnown; a sealed shrine never knows its guards. Nothing here
// admits a breach: readiness (#457) and the breach goal (#458) decide.
type AncientShrine struct {
	ID               string
	Minimum, Maximum domain.Cell
	Sealed, InHome   bool
	GuardsKnown      bool
	Caskets          []ShrineCasket
	Guards           []ShrineGuard
	BreachWalls      []ShrineBreachWall
}

// GuardsAlive reports whether any observed guard still stands or lies downed.
// Unknown guards (a sealed shrine) count as alive: the room has not been
// cleared until it has been seen.
func (s AncientShrine) GuardsAlive() bool {
	if !s.GuardsKnown {
		return true
	}
	for _, guard := range s.Guards {
		if !guard.Dead {
			return true
		}
	}
	return false
}

// FilledCaskets counts the caskets that still hold something.
func (s AncientShrine) FilledCaskets() int {
	n := 0
	for _, casket := range s.Caskets {
		if casket.HasContents {
			n++
		}
	}
	return n
}
