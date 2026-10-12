package combatlab

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/combatsim/simimport"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// SimEnv is what a fixture does not state and the sim needs: the lab's own
// terrain, weather, pawn kind and skills, and the two facts no source here
// holds. FriendlyFireChanceFactor and ColonistWorn are left unknown by
// LabSimEnv so that a caller states them; the import fails closed until it
// does.
type SimEnv struct {
	MapSize int
	// Terrain and Weather are the lab's: bare Soil under clear weather
	// (test/lab_start).
	Terrain, Weather string
	// ColonistKind is the kind of a lab colonist (DebugStartFixture.Colonist).
	ColonistKind string
	// ColonistSkill and HostileSkill are the level every skill of a staged
	// colonist and hostile has when the staging is unseeded
	// (DebugStartFixture.SkillLevel, HostileSkill).
	ColonistSkill, HostileSkill int32
	// ColonistWorn is the apparel a staged colonist wears. The stage strips
	// hostiles but not colonists, whose starter clothes the generator picks,
	// so the fixture cannot say.
	ColonistWorn domain.Fact[[]simimport.Wear]
	// ColonistTraits are the traits a staged colonist carries (the metrics
	// cases' superpawn crew has na.SuperTraits).
	ColonistTraits []simimport.Trait
	// HostileLordJob and HostileLordToil are the lord a staged hostile is under:
	// the stage gives a fixture without a siege, sappers or an arrival an assault
	// lord, whose first toil is the walk-in assault.
	HostileLordJob, HostileLordToil domain.Fact[string]
	// FriendlyFireChanceFactor is the difficulty's setting.
	FriendlyFireChanceFactor domain.Fact[float64]
	// DeathOnDowned is the storyteller population intent and the difficulty's
	// death-on-downed settings, which decide how often a downed raider dies.
	DeathOnDowned domain.Fact[simimport.DeathOnDowned]
	// AllowInstantKillChance is the difficulty's setting (1 off Peaceful-style
	// protection), which decides whether a colonist can die to a single hit.
	AllowInstantKillChance domain.Fact[float64]
	// AutoFleeFraction is the fraction of the raid downed or killed at which
	// the lord panics (Lord.SetJob's Trigger_FractionPawnsLost): the game draws
	// it from the faction's 0.4 to 0.7 seeded by the lord's load id.
	AutoFleeFraction    domain.Fact[float64]
	StepTicks, MaxTicks int
}

// Skill levels the lab staging sets, mirrored from
// scripts/fixtures/DebugStartFixture.cs.
const (
	labColonistSkill = 8
	labHostileSkill  = 8
	// simStepTicks is a sim step: one game tick, so the holders and the raiders
	// decide on the tick a walk or a burst ends (#2910).
	simStepTicks = 1
	// simMaxTicks ends an unresolved sim fight at ten game minutes.
	simMaxTicks = 36000
)

// LabSimEnv is the lab's SimEnv with the two unstated facts unknown.
func LabSimEnv() SimEnv {
	return SimEnv{
		MapSize: na.LabMapSize, Terrain: "Soil", Weather: "Clear", ColonistKind: "Colonist",
		ColonistSkill: labColonistSkill, HostileSkill: labHostileSkill,
		HostileLordJob: domain.Known("LordJob_AssaultColony"), HostileLordToil: domain.Known("LordToil_AssaultColony"),
		StepTicks: simStepTicks, MaxTicks: simMaxTicks,
	}
}

// SimSpec converts a fixture built by Build into the importer's Spec. It
// refuses what the sim cannot represent (roofs, sieges, arrivals, sappers,
// prisoners, non-colonist sides, staged injuries, shields, hit-point damage)
// instead of dropping it.
func SimSpec(f Fixture, env SimEnv) (simimport.Spec, error) {
	refuse := func(what string) (simimport.Spec, error) {
		return simimport.Spec{}, fmt.Errorf("combatlab: fixture %s has %s, which the sim does not model", f.Name, what)
	}
	switch {
	case f.Roof != nil:
		return refuse("a roof")
	case f.Siege != nil:
		return refuse("a siege lord")
	case f.Arrival != "":
		return refuse("an arrival mode")
	case f.PrisonBreak:
		return refuse("a prison break")
	case f.Sappers:
		return refuse("sappers")
	}
	s := simimport.Spec{
		Name: f.Name, Width: env.MapSize, Height: env.MapSize, Terrain: env.Terrain, Weather: env.Weather,
		FriendlyFireChanceFactor: env.FriendlyFireChanceFactor, DeathOnDowned: env.DeathOnDowned, AllowInstantKillChance: env.AllowInstantKillChance, StepTicks: env.StepTicks, MaxTicks: env.MaxTicks,
	}
	for _, t := range f.Things {
		if t.Hostile || t.HitPoints != 0 {
			return refuse("a hostile or damaged building " + t.Def)
		}
		s.Things = append(s.Things, simimport.Thing{Def: t.Def, Cell: domain.Cell{X: int32(t.X), Z: int32(t.Z)}})
	}
	hostiles := 0
	for _, p := range f.Pawns {
		if p.Downed || p.Injured || p.StunTicks != 0 || p.Apparel != "" || len(p.Hediffs) > 0 || len(p.Trained) > 0 || len(p.Inventory) > 0 {
			return refuse("a staged condition on a pawn")
		}
		sp := simimport.Pawn{
			Cell: domain.Cell{X: int32(p.X), Z: int32(p.Z)}, Weapon: p.Weapon, WeaponStuff: p.WeaponStuff, Side: p.Side,
		}
		switch p.Side {
		case Colonist:
			sp.ID = domain.PawnID(fmt.Sprintf("c%d", p.Index))
			sp.Kind, sp.Worn = env.ColonistKind, env.ColonistWorn
			sp.Skills = skills(env.ColonistSkill)
			sp.Traits = env.ColonistTraits
		case Hostile:
			sp.ID = domain.PawnID(fmt.Sprintf("h%d", hostiles))
			hostiles++
			sp.Kind, sp.Worn = p.Kind, domain.Known([]simimport.Wear{}) // the stage destroys a hostile's apparel
			sp.Skills = skills(env.HostileSkill)
			sp.LordJobClass, sp.LordToilClass = env.HostileLordJob, env.HostileLordToil
		default:
			return refuse("a " + p.Side + " pawn")
		}
		s.Pawns = append(s.Pawns, sp)
	}
	if hostiles > 0 {
		s.Raid = &simimport.Raid{CanTimeoutOrFlee: true, Exit: raidExit(s), AutoFleeFraction: env.AutoFleeFraction, FirstTargets: stagedFirstTargets(f.Name)}
	}
	if l := f.Layout; l != nil {
		layout := policy.CombatLayout{Toward: domain.Rotation(l.Toward)}
		for _, c := range l.Firing {
			layout.Firing = append(layout.Firing, domain.Cell{X: int32(c.X), Z: int32(c.Z)})
		}
		for _, c := range l.Retreat {
			layout.Retreat = append(layout.Retreat, domain.Cell{X: int32(c.X), Z: int32(c.Z)})
		}
		if l.Choke != nil {
			layout.Choke = domain.Known(domain.Cell{X: int32(l.Choke.X), Z: int32(l.Choke.Z)})
		}
		s.Layout = domain.Known(layout)
	}
	return s, nil
}

// raidExit is the map-edge cell nearest the hostiles' centroid, where fleeing
// raiders leave (A32).
func raidExit(s simimport.Spec) domain.Fact[domain.Cell] {
	var sx, sz, n int
	for _, p := range s.Pawns {
		if p.Side == Hostile {
			sx, sz, n = sx+int(p.Cell.X), sz+int(p.Cell.Z), n+1
		}
	}
	x, z := sx/n, sz/n
	best, exit := x, domain.Cell{X: 0, Z: int32(z)}
	for _, c := range []struct {
		dist int
		cell domain.Cell
	}{
		{s.Width - 1 - x, domain.Cell{X: int32(s.Width - 1), Z: int32(z)}},
		{z, domain.Cell{X: int32(x), Z: 0}},
		{s.Height - 1 - z, domain.Cell{X: int32(x), Z: int32(s.Height - 1)}},
	} {
		if c.dist < best {
			best, exit = c.dist, c.cell
		}
	}
	return domain.Known(exit)
}

// skills sets the skills the sim's pawn stats read to level.
func skills(level int32) map[string]int32 {
	return map[string]int32{"Shooting": level, "Melee": level}
}
