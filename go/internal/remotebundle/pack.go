package remotebundle

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

// PackOptions deliberately names dependencies, never a whole profile/worktree.
type PackOptions struct {
	Repo, GameDir, HarmonyDLL, BridgeDir, GABS, Output      string
	GameVersion, HarmonyVersion, BridgeVersion, GABSVersion string
	Origin                                                  Origin
	Recipient, KeyID                                        string
	PartBytes                                               int64
	Tools                                                   Tools
	StartsDir                                               string
	HarmonyMod                                              string
}

// CorePaths is the conservative inclusion list. No Core/Unity media is removed.
// Expansions, user profiles, workshop trees and unrelated mods are excluded.
var CorePaths = []string{"RimWorldWin64.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "WinPixEventRuntime.dll", "Version.txt", "EULA.txt", "Licenses.txt", "Data/Core", "RimWorldWin64_Data", "MonoBleedingEdge"}

type Starts struct {
	SchemaVersion      int    `json:"schema_version"`
	GameVersion        string `json:"game_version"`
	NativeSourceSHA256 string `json:"native_source_sha256"`
	DependenciesSHA256 string `json:"dependencies_sha256"`
	CommittedFixtures  []File `json:"committed_fixtures"`
	// Root-local generated starts are deliberately not imported without provenance.
	Generated []File `json:"generated"`
}

func Pack(ctx context.Context, o PackOptions) (Manifest, error) {
	m := Manifest{SchemaVersion: 1, Game: Game{o.GameVersion, "windows-x64", true}, Origin: o.Origin, Encryption: Encryption{"age-v1", o.KeyID}, Inventory: Reference{Path: "inventory.json"}}
	if o.Recipient == "" || o.KeyID == "" || o.GameVersion == "" || o.HarmonyVersion == "" || o.BridgeVersion == "" || o.GABSVersion == "" {
		return m, fmt.Errorf("recipient, key ID and exact dependency versions are required")
	}
	if !repositoryPattern.MatchString(o.Origin.Repository) || o.Origin.ReleaseID <= 0 {
		return m, fmt.Errorf("pinned origin repository and release ID are required")
	}
	if err := os.Mkdir(o.Output, 0700); err != nil {
		return m, fmt.Errorf("output must be new: %w", err)
	}
	tree := filepath.Join(o.Output, "staging")
	if err := os.Mkdir(tree, 0700); err != nil {
		return m, err
	}
	defer os.RemoveAll(tree)
	for _, p := range CorePaths {
		src := filepath.Join(o.GameDir, filepath.FromSlash(p))
		if _, err := os.Stat(src); os.IsNotExist(err) && (p == "UnityCrashHandler64.exe" || p == "WinPixEventRuntime.dll") {
			continue
		}
		if err := Materialize(src, filepath.Join(tree, "game", filepath.FromSlash(p))); err != nil {
			return m, fmt.Errorf("game input %s: %w", p, err)
		}
	}
	version, err := os.ReadFile(filepath.Join(tree, "game", "Version.txt"))
	if err != nil {
		return m, err
	}
	if strings.TrimSpace(string(version)) != o.GameVersion {
		return m, fmt.Errorf("game version does not match Version.txt")
	}
	if o.HarmonyMod == "" {
		return m, fmt.Errorf("-harmony-mod must name the complete Harmony runtime package")
	}
	for _, required := range []string{"About/About.xml", "LoadFolders.xml", "Current/Assemblies/0Harmony.dll", "Current/Assemblies/HarmonyMod.dll"} {
		if _, err := os.Stat(filepath.Join(o.HarmonyMod, filepath.FromSlash(required))); err != nil {
			return m, fmt.Errorf("harmony runtime input %s: %w", required, err)
		}
	}
	want, _, err := HashFile(o.HarmonyDLL)
	if err != nil {
		return m, err
	}
	got, _, err := HashFile(filepath.Join(o.HarmonyMod, "Current", "Assemblies", "0Harmony.dll"))
	if err != nil {
		return m, err
	}
	if want != got {
		return m, fmt.Errorf("build Harmony DLL differs from the runtime package")
	}
	for _, pair := range [][2]string{{o.HarmonyMod, "harmony"}, {o.BridgeDir, "bridge"}, {o.GABS, "gabs/gabs.exe"}} {
		if err := Materialize(pair[0], filepath.Join(tree, filepath.FromSlash(pair[1]))); err != nil {
			return m, err
		}
	}
	if _, err := os.Stat(filepath.Join(tree, "bridge", "1.6", "Assemblies", "RimBridgeServer.Sdk.dll")); err != nil {
		return m, fmt.Errorf("bridge SDK: %w", err)
	}
	// Committed fixtures are inventoried, not copied from any player's profile.
	fixtures, err := InventoryTree(filepath.Join(o.Repo, "scripts", "fixtures", "saves"))
	if err != nil {
		return m, err
	}
	native, err := inputs.SourceTreeHash(o.Repo)
	if err != nil {
		return m, err
	}
	dependencyHash, err := dependencyIdentity(tree)
	if err != nil {
		return m, err
	}
	starts := Starts{SchemaVersion: 1, GameVersion: o.GameVersion, NativeSourceSHA256: native, DependenciesSHA256: dependencyHash, CommittedFixtures: fixtures.Files, Generated: []File{}}
	if err := os.Mkdir(filepath.Join(tree, "starts"), 0700); err != nil {
		return m, err
	}
	if o.StartsDir != "" {
		b, err := os.ReadFile(filepath.Join(o.StartsDir, "compatibility.json"))
		if err != nil {
			return m, err
		}
		var supplied Starts
		if err := Decode(b, &supplied); err != nil {
			return m, err
		}
		if supplied.SchemaVersion != 1 || supplied.GameVersion != starts.GameVersion || supplied.NativeSourceSHA256 != native || supplied.DependenciesSHA256 != dependencyHash || !slices.Equal(supplied.CommittedFixtures, fixtures.Files) {
			return m, fmt.Errorf("cached starts incompatible with dependencies/native source/fixtures; regenerate before packaging")
		}
		if err := checkGenerated(o.StartsDir, supplied.Generated); err != nil {
			return m, err
		}
		for _, f := range supplied.Generated {
			if err := Materialize(filepath.Join(o.StartsDir, f.Path), filepath.Join(tree, "starts", f.Path)); err != nil {
				return m, err
			}
		}
		starts.Generated = supplied.Generated
	}
	if err := WriteJSON(filepath.Join(tree, "starts", "compatibility.json"), starts); err != nil {
		return m, err
	}
	m.Components = []Component{{Name: "game", Version: o.GameVersion, PathPrefix: "game/"}, {Name: "harmony", Version: o.HarmonyVersion, PathPrefix: "harmony/"}, {Name: "bridge", Version: o.BridgeVersion, PathPrefix: "bridge/"}, {Name: "gabs", Version: o.GABSVersion, PathPrefix: "gabs/"}, {Name: "starts", Version: native, PathPrefix: "starts/"}}
	inv, err := InventoryTree(tree)
	if err != nil {
		return m, err
	}
	BindInventory(&m, inv)
	if err := ValidateInventory(inv, m); err != nil {
		return m, err
	}
	if err := WriteJSON(filepath.Join(o.Output, "inventory.json"), inv); err != nil {
		return m, err
	}
	m.Inventory.SHA256, _, err = HashFile(filepath.Join(o.Output, "inventory.json"))
	if err != nil {
		return m, err
	}
	m.Parts, err = EncryptArchive(ctx, o.Tools, tree, o.Output, o.Recipient, o.PartBytes)
	if err != nil {
		return m, err
	}
	if err := m.Validate(false); err != nil {
		return m, err
	}
	return m, WriteJSON(filepath.Join(o.Output, "bundle.draft.json"), m)
}

// ValidateStarts refuses unproven generated saves. Fresh starts regenerate through
// the ordinary harness, which reports its cache miss. Committed saves come from
// the tested checkout; a bundle never overwrites them.
func ValidateStarts(tree, repo string, m Manifest) (string, error) {
	b, err := os.ReadFile(filepath.Join(tree, "starts", "compatibility.json"))
	if err != nil {
		return "", err
	}
	var s Starts
	if err := Decode(b, &s); err != nil {
		return "", err
	}
	if s.SchemaVersion != 1 || s.GameVersion != m.Game.Version || !digestPattern.MatchString(s.NativeSourceSHA256) {
		return "", fmt.Errorf("invalid start compatibility metadata")
	}
	dependencyHash, err := dependencyIdentity(tree)
	if err != nil {
		return "", err
	}
	if s.DependenciesSHA256 != dependencyHash {
		return "", fmt.Errorf("cached-start dependency identity mismatch")
	}
	if err := checkGenerated(filepath.Join(tree, "starts"), s.Generated); err != nil {
		return "", err
	}
	native, err := inputs.SourceTreeHash(repo)
	if err != nil {
		return "", err
	}
	if native != s.NativeSourceSHA256 {
		return "native inputs changed; generated starts will regenerate", nil
	}
	current, err := InventoryTree(filepath.Join(repo, "scripts", "fixtures", "saves"))
	if err != nil {
		return "", err
	}
	if !slices.Equal(s.CommittedFixtures, current.Files) {
		return "committed fixtures changed; use tested checkout and regenerate starts", nil
	}
	if len(s.Generated) > 0 {
		return "compatible generated starts ready to stage", nil
	}
	return "compatible committed fixtures; generated starts will regenerate on first use", nil
}
