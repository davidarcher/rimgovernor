package setup

import (
	"os"
	"path/filepath"
	"testing"
)

func TestExplicitDiscoveryNeedsEveryInput(t *testing.T) {
	t.Setenv(RimWorldDirEnv, "machine-local-game")
	t.Setenv(HarmonyEnv, "machine-local-harmony")
	t.Setenv(GABSEnv, "peer-gabs")
	if _, err := Discover(Overrides{Explicit: true}); err == nil {
		t.Fatal("explicit discovery fell back to machine inputs")
	}
	root := t.TempDir()
	game := filepath.Join(root, "game")
	bridge := filepath.Join(root, "bridge")
	sdk := filepath.Join(root, "sdk")
	harmonyMod := filepath.Join(root, "harmony")
	for _, p := range []string{"About/About.xml", "Current/Assemblies/HarmonyMod.dll", "Current/Assemblies/0Harmony.dll"} {
		file := filepath.Join(harmonyMod, filepath.FromSlash(p))
		os.MkdirAll(filepath.Dir(file), 0700)
		os.WriteFile(file, []byte("fixture"), 0600)
	}
	for _, p := range []string{filepath.Join(game, "RimWorldWin64.exe"), filepath.Join(game, "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll"), filepath.Join(bridge, "About", "About.xml"), filepath.Join(sdk, "RimBridgeServer.Sdk.dll"), filepath.Join(root, "harmony.dll"), filepath.Join(root, "gabs.exe")} {
		if err := os.MkdirAll(filepath.Dir(p), 0700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte("fixture"), 0600); err != nil {
			t.Fatal(err)
		}
	}
	o := Overrides{Explicit: true, RimWorldDir: game, BridgeDir: bridge, RimBridgeSDK: sdk, HarmonyMod: harmonyMod, Harmony: filepath.Join(harmonyMod, "Current", "Assemblies", "0Harmony.dll"), GABS: filepath.Join(root, "gabs.exe")}
	in, err := Discover(o)
	if err != nil {
		t.Fatal(err)
	}
	if !in.CleanProfile || in.RimBridgeSDK != absClean(sdk) || in.BridgeDir != bridge {
		t.Fatalf("explicit input mismatch: %+v", in)
	}
	os.Remove(filepath.Join(harmonyMod, "Current", "Assemblies", "HarmonyMod.dll"))
	if _, err := Discover(o); err == nil {
		t.Fatal("build DLL without Harmony runtime accepted")
	}
}
