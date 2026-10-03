package policy

// coreWeapons is test data: the def rows' facts (#1723) of the weapons the
// planning tests name, stated as bridge.CoreWeaponFixtures derives them.
var coreWeapons = map[string]WeaponDef{
	"Weapon_GrenadeFrag":         {Ranged: true, Range: 12.9, Explosive: true, Blast: 1.9, DPS: 12.019, AP: .1, ForcedMiss: true},
	"Weapon_GrenadeMolotov":      {Ranged: true, Range: 12.9, Explosive: true, Blast: 1.1, Incendiary: true, DPS: 2.404, ForcedMiss: true},
	"Weapon_GrenadeEMP":          {Ranged: true, Range: 12.9, Explosive: true, Blast: 3.5, EMP: true, DPS: 12.019, ForcedMiss: true},
	"Gun_EmpLauncher":            {Ranged: true, Range: 23.9, Explosive: true, Launcher: true, Blast: 1.1, EMP: true, DPS: 7.143, ForcedMiss: true},
	"Gun_IncendiaryLauncher":     {Ranged: true, Range: 23.9, Explosive: true, Launcher: true, Blast: 1.1, Incendiary: true, DPS: 1.429, ForcedMiss: true},
	"Gun_TripleRocket":           {Ranged: true, Range: 35.9, Explosive: true, Launcher: true, OneUse: true, DPS: 15.517, AP: .1, ForcedMiss: true},
	"Weapon_RocketswarmLauncher": {Ranged: true, Range: 26.9, Explosive: true, Launcher: true, ForcedMiss: true},
	"Gun_AssaultRifle":           {Ranged: true, Range: 30.9, DPS: 10.879, AP: .165},
	"Gun_SniperRifle":            {Ranged: true, Range: 44.9, DPS: 5, AP: .375, Precision: true},
	"Gun_BoltActionRifle":        {Ranged: true, Range: 36.9, DPS: 5.625, AP: .27, Precision: true},
	"Gun_PumpShotgun":            {Ranged: true, Range: 15.9, DPS: 8.372, AP: .14},
	"Gun_Revolver":               {Ranged: true, Range: 25.9, DPS: 6.316, AP: .18},
	"Gun_Minigun":                {Ranged: true, Range: 30.9, DPS: 41.667, AP: .15, Precision: true},
	"Gun_LMG":                    {Ranged: true, Range: 25.9, DPS: 18.075, AP: .18},
	"Gun_ChargeRifle":            {Ranged: true, Range: 27.9, DPS: 14.118, AP: .35},
	"Bow_Short":                  {Ranged: true, Range: 22.9, DPS: 3.667, AP: .165},
	"MeleeWeapon_Club":           {Melee: true, Blunt: true, DPS: 7.487, AP: .188},
	"MeleeWeapon_Mace":           {Melee: true, Blunt: true, DPS: 8.563, AP: .211},
	"MeleeWeapon_Knife":          {Melee: true, DPS: 7.832, AP: .177},
	"MeleeWeapon_Spear":          {Melee: true, DPS: 10.426, AP: .381},
	"MeleeWeapon_LongSword":      {Melee: true, DPS: 11.415, AP: .33},
	"MeleeWeapon_Longsword":      {Melee: true, DPS: 11.415, AP: .33},
	"WoodLog":                    {Melee: true, Blunt: true, DPS: 5.75, AP: .15},
	// Not in bridge.CoreWeaponFixtures: round numbers for the tests that
	// need a close-range shotgun, an SMG, a mid bow and a sling.
	"Gun_ChainShotgun": {Ranged: true, Range: 12.9, DPS: 12, AP: .14},
	"Gun_HeavySMG":     {Ranged: true, Range: 22.9, DPS: 9, AP: .18},
	"Bow_Recurve":      {Ranged: true, Range: 25.9, DPS: 4, AP: .14},
	"Bow_Great":        {Ranged: true, Range: 29.9, DPS: 7.2, AP: .2},
	"Modded_Sling":     {Ranged: true, Range: 20, DPS: 4, AP: .1},
}

func coreFacts(def string) WeaponDef { return coreWeapons[def] }

// withWeapon is the pawn state holding def, with its rows' facts.
func withWeapon(s CombatPawnState, def string) CombatPawnState {
	s.Weapon, s.WeaponFacts = def, coreWeapons[def]
	return s
}
