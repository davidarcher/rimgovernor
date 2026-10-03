package policy

// coreWeapons is test data: the def rows' facts (#1723) of the weapons the
// planning tests name, stated as bridge.CoreWeaponFixtures derives them.
var coreWeapons = map[string]WeaponDef{
	"Weapon_GrenadeFrag":         {Ranged: true, Range: 12.9, Explosive: true, Blast: 1.9},
	"Weapon_GrenadeMolotov":      {Ranged: true, Range: 12.9, Explosive: true, Blast: 1.1, Incendiary: true},
	"Weapon_GrenadeEMP":          {Ranged: true, Range: 12.9, Explosive: true, Blast: 3.5, EMP: true},
	"Gun_EmpLauncher":            {Ranged: true, Range: 23.9, Explosive: true, Launcher: true, Blast: 1.1, EMP: true},
	"Gun_IncendiaryLauncher":     {Ranged: true, Range: 23.9, Explosive: true, Launcher: true, Blast: 1.1, Incendiary: true},
	"Gun_TripleRocket":           {Ranged: true, Range: 35.9, Explosive: true, Launcher: true, OneUse: true},
	"Weapon_RocketswarmLauncher": {Ranged: true, Range: 26.9, Explosive: true, Launcher: true},
	"Gun_AssaultRifle":           {Ranged: true, Range: 30.9},
	"Gun_SniperRifle":            {Ranged: true, Range: 44.9},
	"Gun_Revolver":               {Ranged: true, Range: 25.9},
	"Gun_Minigun":                {Ranged: true, Range: 30.9},
	"MeleeWeapon_Club":           {Melee: true, Blunt: true},
	"MeleeWeapon_Mace":           {Melee: true, Blunt: true},
	"MeleeWeapon_Knife":          {Melee: true},
	"MeleeWeapon_LongSword":      {Melee: true},
	"MeleeWeapon_Longsword":      {Melee: true},
}

// withWeapon is the pawn state holding def, with its rows' facts.
func withWeapon(s CombatPawnState, def string) CombatPawnState {
	s.Weapon, s.WeaponFacts = def, coreWeapons[def]
	return s
}
