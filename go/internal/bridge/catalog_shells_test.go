package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

func mortarShellSlice(t *testing.T) *recordedrows.Slice {
	return recordedrows.Take(t, func(row *d.ThingDef) bool { return row.GetProjectileWhenLoaded() != "" }, "damage_defs")
}

// TestMortarShellsFromDamageRows: the eight shell defs of Core,
// Biotech and Anomaly (every def with a projectileWhenLoaded) classify by
// their projectile's damage def; smoke, firefoam, tox, deadlife and the
// antigrain warhead (a 14.9 blast, past the safe radius) are skipped.
func TestMortarShellsFromDamageRows(t *testing.T) {
	got, err := sharedRecordedCatalog(t).MortarShells(policy.MortarSafeRadius)
	if err != nil {
		t.Fatal(err)
	}
	want := policy.MortarShells{HE: "Shell_HighExplosive", Incendiary: "Shell_Incendiary", EMP: "Shell_EMP"}
	if got != want {
		t.Fatalf("got %+v, want %+v", got, want)
	}
	// A second bomb-class shell inside the radius makes HE ambiguous: unset.
	slice := mortarShellSlice(t)
	slice.CopyThing("Shell_HighExplosive", "Shell_Modded")
	if got, err = catalogOf(slice).MortarShells(policy.MortarSafeRadius); err != nil || got.HE != "" || got.EMP != "Shell_EMP" {
		t.Fatalf("ambiguous HE: got %+v, %v", got, err)
	}
	// A shell whose damage def has no row is a contract error.
	broken := mortarShellSlice(t)
	broken.Thing(broken.Thing("Shell_HighExplosive").ProjectileWhenLoaded).Projectile.DamageDef = "Nope"
	if _, err = catalogOf(broken).MortarShells(policy.MortarSafeRadius); err == nil {
		t.Fatal("a shell with no damage def row was accepted")
	}
	var none *DefinitionCatalog
	if _, err = none.MortarShells(policy.MortarSafeRadius); err == nil {
		t.Fatal("no catalog was accepted")
	}
}
