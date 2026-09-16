package docker

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
)

// BuildNativeMods builds a fresh copy of the unified RimGovernor native mod
// from source's own tree (scripts/build_native_mod.ps1, against
// integrations/rimgovernor-native) and layers it into a private mods
// directory at <root>/mods, alongside Harmony and RimBridgeServer copied
// verbatim from sharedMods -- mirroring the build-and-layer pattern every
// other documented acceptance workflow follows (see the recovered
// docs/developers/testing/local-acceptance-inputs.md), which treats
// sharedMods/RimGovernor as a snapshot that must never be trusted directly:
// a worker started straight against it can silently run a stale binary that
// predates source changes (see StartWorker's own doc comment). Only
// RimGovernor itself is ever rebuilt; Harmony and RimBridgeServer are
// third-party inputs, copied as-is. game must be the platform's game build
// (its *_Data/Managed directory supplies RimWorldManagedDir); sharedMods
// must be the platform's shared Mods directory (its
// Harmony/*/Assemblies/0Harmony.dll and RimBridgeServer/*/Assemblies supply
// HarmonyAssembly/RimBridgeSdkDir). Returns the private mods directory to
// mount instead of sharedMods.
func BuildNativeMods(ctx context.Context, source, game, sharedMods, root, logPath string) (string, error) {
	managed, err := globOne(filepath.Join(game, "*_Data", "Managed"))
	if err != nil {
		return "", fmt.Errorf("locate RimWorld Managed dir under %s: %w", game, err)
	}
	// RimWorld mods that ship per-version subfolders (Harmony's 1.4, 1.5, ...)
	// always pair them with a "Current" alias resolved to the active game
	// version at runtime (see Harmony/LoadFolders.xml in the shared store) --
	// that alias, not a version glob, is the one actually loaded.
	harmonyDLL := filepath.Join(sharedMods, "Harmony", "Current", "Assemblies", "0Harmony.dll")
	if _, err := os.Stat(harmonyDLL); err != nil {
		return "", fmt.Errorf("locate Harmony assembly at %s: %w", harmonyDLL, err)
	}
	sdkDir, err := globOne(filepath.Join(sharedMods, "RimBridgeServer", "*", "Assemblies"))
	if err != nil {
		return "", fmt.Errorf("locate RimBridgeServer SDK dir under %s: %w", sharedMods, err)
	}

	script := filepath.Join(source, "scripts", "build_native_mod.ps1")
	if _, err := os.Stat(script); err != nil {
		return "", fmt.Errorf("native build script: %w", err)
	}
	shell, err := powershellBinary()
	if err != nil {
		return "", err
	}
	buildOut := filepath.Join(root, "native-build")
	if _, err := os.Stat(buildOut); err == nil {
		return "", fmt.Errorf("native build output must be fresh: %s", buildOut)
	}

	cmd := exec.CommandContext(ctx, shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
		"-File", script,
		"-RimWorldManagedDir", managed,
		"-HarmonyAssembly", harmonyDLL,
		"-RimBridgeSdkDir", sdkDir,
		"-OutputRoot", buildOut,
	)
	out, runErr := cmd.CombinedOutput()
	if logPath != "" {
		_ = os.WriteFile(logPath, out, 0644)
	}
	if runErr != nil {
		return "", fmt.Errorf("build native mod: %w: %s", runErr, out)
	}

	mods := filepath.Join(root, "mods")
	for _, name := range []string{"Harmony", "RimBridgeServer"} {
		if err := copyTree(filepath.Join(sharedMods, name), filepath.Join(mods, name)); err != nil {
			return "", fmt.Errorf("copy %s: %w", name, err)
		}
	}
	if err := copyTree(filepath.Join(buildOut, "RimGovernor"), filepath.Join(mods, "RimGovernor")); err != nil {
		return "", fmt.Errorf("copy freshly built RimGovernor package: %w", err)
	}
	return mods, nil
}

// globOne resolves pattern to exactly one match, so a shared input store
// whose versioned subdirectory (e.g. Harmony/Current or RimBridgeServer/1.6)
// changes name doesn't silently go stale or silently pick the wrong one.
func globOne(pattern string) (string, error) {
	matches, err := filepath.Glob(pattern)
	if err != nil {
		return "", err
	}
	if len(matches) != 1 {
		return "", fmt.Errorf("expected exactly one match for %s, found %d", pattern, len(matches))
	}
	return matches[0], nil
}

// powershellBinary finds a PowerShell interpreter capable of running
// scripts/build_native_mod.ps1: PowerShell 7+ (pwsh) if present, else Windows
// PowerShell.
func powershellBinary() (string, error) {
	for _, name := range []string{"pwsh", "pwsh.exe", "powershell", "powershell.exe"} {
		if path, err := exec.LookPath(name); err == nil {
			return path, nil
		}
	}
	return "", fmt.Errorf("no PowerShell interpreter (pwsh or powershell) found on PATH")
}

// copyTree recursively copies source's contents to destination, creating
// destination directories as needed.
func copyTree(source, destination string) error {
	return filepath.WalkDir(source, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		relative, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		target := filepath.Join(destination, relative)
		if d.IsDir() {
			return os.MkdirAll(target, 0755)
		}
		return copyFile(path, target)
	})
}
