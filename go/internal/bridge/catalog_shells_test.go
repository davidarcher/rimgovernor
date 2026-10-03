package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestMortarShellsFromDamageRows (#1723): the eight shell defs of Core,
// Biotech and Anomaly (every def with a projectileWhenLoaded) classify by
// their projectile's damage def; smoke, firefoam, tox, deadlife and the
// antigrain warhead (a 14.9 blast, past the safe radius) are skipped.
func TestMortarShellsFromDamageRows(t *testing.T) {
	catalog := FixtureCatalog("load", CoreWeaponFixtures()...)
	got, err := catalog.MortarShells(policy.MortarSafeRadius)
	if err != nil {
		t.Fatal(err)
	}
	want := policy.MortarShells{HE: "Shell_HighExplosive", Incendiary: "Shell_Incendiary", EMP: "Shell_EMP"}
	if got != want {
		t.Fatalf("got %+v, want %+v", got, want)
	}
	// A second bomb-class shell inside the radius makes HE ambiguous: unset.
	both := FixtureCatalog("load", append(CoreWeaponFixtures(), FixtureDef{Name: "Shell_Modded", Shell: &FixtureShell{DamageDef: "Bomb", ExplosionRadius: 3}})...)
	if got, err = both.MortarShells(policy.MortarSafeRadius); err != nil || got.HE != "" || got.EMP != "Shell_EMP" {
		t.Fatalf("ambiguous HE: got %+v, %v", got, err)
	}
	// A shell whose damage def has no row is a contract error.
	broken := FixtureCatalog("load", FixtureDef{Name: "Shell_Broken", Shell: &FixtureShell{DamageDef: "Nope"}})
	if _, err = broken.MortarShells(policy.MortarSafeRadius); err == nil {
		t.Fatal("a shell with no damage def row was accepted")
	}
	var none *DefinitionCatalog
	if _, err = none.MortarShells(policy.MortarSafeRadius); err == nil {
		t.Fatal("no catalog was accepted")
	}
}
