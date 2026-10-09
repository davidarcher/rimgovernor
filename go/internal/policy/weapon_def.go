package policy

// WeaponDef is what a weapon's def rows say about how it fights:
// derived once per load from the ThingDef, its verbs and projectile, the
// projectile's DamageDef and the melee tools' maneuvers. The zero value is
// no weapon (an unarmed pawn).
type WeaponDef struct {
	// Ranged is a weapon with a non-melee verb; Melee one with only tools.
	Ranged, Melee bool
	// Range is the ranged verb's range in cells, 0 for a melee weapon.
	Range float64
	// Reach is how far the wielder strikes: Range for a ranged weapon, the
	// longest melee verb range of its tools for a melee weapon, 0 for no
	// weapon.
	Reach float64
	// ByTrade is membership in the Weapons thing category (what a stockpile
	// calls a weapon): a wood log or a beer is a melee weapon by Melee but
	// not by trade.
	ByTrade bool
	// Explosive is a ranged verb that scatters (forcedMissRadius) a
	// projectile that explodes: a grenade, a rocket, a launcher shell.
	Explosive bool
	// Launcher is an Explosive weapon fired by a shoot verb (a launcher)
	// rather than thrown (a grenade).
	Launcher bool
	// OneUse is a weapon spent by its shot (a rocket launcher).
	OneUse bool
	// Blast is the explosion radius in cells of a weapon a carrier can aim
	// at the ground to hurt a crowd: Explosive, reusable, and its damage
	// hurts or stuns. 0 for every other weapon.
	Blast float64
	// EMP is a projectile whose damage stuns and applies to mechanoids.
	EMP bool
	// Incendiary is a projectile that sets fires.
	Incendiary bool
	// Blunt is a melee weapon every tool of which deals blunt-armored damage.
	Blunt bool
	// DPS is the weapon's nominal damage per second and AP its armor
	// penetration, read from the verb, tool, projectile and damage rows
	// (bridge.DefinitionCatalog.WeaponOf says how). A planning estimate, not
	// a prediction of native combat damage; 0 for a def that states none.
	DPS, AP float64
	// Precision is an accuracy curve that peaks at medium range or beyond.
	Precision bool
	// ForcedMiss is a verb that scatters its shots (forcedMissRadius > 0):
	// area fire a lone fighter alone may carry.
	ForcedMiss bool
}

// Hunts is the vanilla hunting rule on the def's rows: a ranged weapon that
// neither scatters an exploding projectile nor sets fires. It is the one
// predicate behind the hunt channel's gunner count and the formation's
// gunners.
func (w WeaponDef) Hunts() bool { return w.Ranged && !w.Explosive && !w.Incendiary }

// Burner is an incendiary weapon a carrier aims at the ground: the burn-out's
// flame thrower (a molotov), which belongs to the burn-out alone.
func (w WeaponDef) Burner() bool { return w.Incendiary && w.Blast > 0 }

// Armament is an ordinary arm the armory may craft for a fighter: a weapon by
// trade that strikes or shoots, and is neither a scattering explosive, an
// incendiary nor spent by its shot.
func (w WeaponDef) Armament() bool {
	return w.ByTrade && (w.Ranged || w.Melee) && !w.Explosive && !w.Incendiary && !w.OneUse && !w.ForcedMiss
}
