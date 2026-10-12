// Package simimport builds a combatsim.Fight from a plain description of a
// fight (a Spec) and the native DefinitionCatalog. It derives what combatsim
// deliberately holds no literals for: per-pawn injury thresholds, the
// flattened armor list, effective weapon values, shooter stats, body size, move
// speed, and per-cell thing and terrain defs. Every derived value is traced to
// the catalog def (or game constant) it was read from in Result.Trace. A value
// the catalog cannot give, or that the Spec does not state, fails the import
// with combatsim.ErrUnsupported; nothing is guessed. The derivations and the
// departures from the game they carry are approximations A24-A31 in
// ../approximations.md.
//
// The Spec is an in-memory description, not a persisted or wire format. The
// package sits beside combatsim so the pure core imports no catalog code;
// fixtures are converted to a Spec on the nativeaccept side (combatlab).
package simimport

import (
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Spec describes one fight.
type Spec struct {
	// Name labels the Metrics row.
	Name          string
	Width, Height int
	// Terrain is the TerrainDef name of every cell.
	Terrain string
	// Weather is the WeatherDef name in force; its move speed and accuracy
	// multipliers and range cap come from the def.
	Weather string
	// FriendlyFireChanceFactor is the difficulty's friendlyFireChanceFactor,
	// which no catalog def holds. Unknown fails the import.
	FriendlyFireChanceFactor domain.Fact[float64]
	// DeathOnDowned states what the death-on-downed chance of a hostile
	// depends on besides the catalog (A33). Unknown fails the import of a Spec
	// with a hostile.
	DeathOnDowned domain.Fact[DeathOnDowned]
	Things        []Thing
	Pawns         []Pawn
	// Layout is the stored defense layout the policy reads, unknown without one.
	Layout domain.Fact[policy.CombatLayout]
	// Raid is the hostiles' lord; nil leaves Fight.EnemyAI unset.
	Raid *Raid
	// StepTicks is the game ticks per Step; MaxTicks ends an unresolved fight.
	StepTicks, MaxTicks int
}

// DeathOnDowned is the live state Pawn_HealthTracker.CheckForStateChange reads
// for a non-colony humanlike: the storyteller's population intent and two
// difficulty settings. No catalog def holds them.
type DeathOnDowned struct {
	// PopulationIntent is StorytellerUtilityPopulation.PopulationIntent.
	PopulationIntent float64
	// EnemyDeathOnDownedChanceFactor is the difficulty's setting.
	EnemyDeathOnDownedChanceFactor float64
	// UnwaveringPrisoners is the difficulty's setting; false selects the
	// WaveringPrisoners curve.
	UnwaveringPrisoners bool
}

// Thing is one structure or item standing on a cell. Stuff does not change
// any value the sim reads (fill percent, path cost, passability), so it is not
// carried.
type Thing struct {
	Def  string
	Cell domain.Cell
}

// Wear is one worn apparel: its def and stuff ("" for none).
type Wear struct{ Def, Stuff string }

// Pawn is one combatant. Only healthy adult pawns of a humanlike race with no
// traits are described: their health, capacities and needs are the catalog
// baseline, and a lab-staged pawn is exactly that.
type Pawn struct {
	ID   domain.PawnID
	Side string
	// Kind is the PawnKindDef name; its race def gives body size, health scale
	// and natural armor.
	Kind string
	Cell domain.Cell
	// Weapon and WeaponStuff are the wielded weapon def ("" for none). A
	// weapon that is not an ordinary ranged weapon leaves the pawn unarmed and
	// is listed in Result.Unmodeled.
	Weapon, WeaponStuff string
	// Worn lists the worn apparel outermost first. Unknown fails the import:
	// the sim must not assume a pawn is unarmored.
	Worn domain.Fact[[]Wear]
	// Skills are the SkillRecord levels by SkillDef name. Nil fails the import.
	Skills map[string]int32
	// LordJobClass and LordToilClass are the hostile's lord evidence; unknown
	// reaches the policy as unknown.
	LordJobClass, LordToilClass domain.Fact[string]
}

// Raid is the hostiles' LordJob_AssaultColony as the sim needs it. Importing a
// Spec with a Raid sets Fight.EnemyAI, which drives the hostile ranged pawns;
// without one the caller drives them through Fight.Enemy.
type Raid struct {
	// CanTimeoutOrFlee is the lord's canTimeoutOrFlee: true for an ordinary
	// raid, which gives up after AssaultTimeBeforeGiveUp or on enough colony
	// damage. It needs Exit.
	CanTimeoutOrFlee bool
	// Exit is the cell fleeing raiders walk to and leave by. No catalog def or
	// fixture holds it (the game picks a map-edge spot), so the caller states it.
	Exit domain.Fact[domain.Cell]
}

// Trace records where one derived value came from.
type Trace struct {
	// Subject is a pawn id, or "map".
	Subject string
	// Field is the combatsim field or fact that was derived.
	Field string
	// Source names the catalog def and member, stat or game constant read.
	Source string
}

// Result is an imported fight.
type Result struct {
	// Fight is ready for combatsim.RunFight; a Spec without a Raid leaves the
	// hostiles to the caller's Fight.Enemy.
	Fight combatsim.Fight
	// Trace lists every derived value with its source.
	Trace []Trace
	// Unmodeled lists what the Spec holds that the sim leaves out (a melee
	// weapon, say). The fight runs without it: read it before trusting a
	// result.
	Unmodeled []string
}
