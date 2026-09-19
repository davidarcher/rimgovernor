// Package setup builds a worktree's private native acceptance environment
// (`acceptance setup`): the game copy under .rimgovernor/native-rimworld,
// the bridge root under .rimgovernor/bridge, the fixture mod build
// installed into the copy, and the controller binaries under
// .rimgovernor/bin. It is what the agent runbook's "private to the
// worktree" section used to ask each agent to do by hand.
package setup

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// Environment variables that override discovery, for a machine whose Steam
// layout the registry does not describe (or no Steam at all).
const (
	// RimWorldDirEnv names the RimWorld install (the directory holding
	// RimWorldWin64.exe).
	RimWorldDirEnv = "RIMGOVERNOR_RIMWORLD_DIR"
	// HarmonyEnv names 0Harmony.dll.
	HarmonyEnv = "RIMGOVERNOR_HARMONY_DLL"
	// GABSEnv names the gabs.exe to copy into the bridge root.
	GABSEnv = "RIMGOVERNOR_GABS_EXE"
)

// RimWorldAppID is RimWorld's Steam app id: its workshop content lives
// under steamapps/workshop/content/<RimWorldAppID>.
const RimWorldAppID = "294100"

// HarmonyWorkshopID is the Harmony mod's workshop item id.
const HarmonyWorkshopID = "2009463077"

// Inputs are the machine's shared game files the setup reads from and
// never writes: what build_native_mod.ps1 takes as -RimWorldManagedDir,
// -HarmonyAssembly and -RimBridgeSdkDir, plus the install the copy
// junctions into.
type Inputs struct {
	// BridgeDir overrides the bridge runtime root for a self-contained bundle.
	BridgeDir  string
	HarmonyMod string
	// CleanProfile prevents importing any machine-local player preferences.
	CleanProfile bool
	// RimWorldDir holds RimWorldWin64.exe.
	RimWorldDir string
	// ManagedDir is RimWorldDir/RimWorldWin64_Data/Managed.
	ManagedDir string
	// Harmony is 0Harmony.dll.
	Harmony string
	// RimBridgeSDK is RimWorldDir/Mods/RimBridgeServer/1.6/Assemblies.
	RimBridgeSDK string
	// GABS is the gabs.exe copied into the bridge root.
	GABS string
	// How each input was found, for the summary.
	Sources map[string]string
}

// Overrides are the explicit paths the caller gives; empty fields are
// discovered.
type Overrides struct {
	HarmonyMod   string
	BridgeDir    string
	RimBridgeSDK string
	// Explicit requires all dependency paths; no Steam or peer discovery occurs.
	Explicit    bool
	RimWorldDir string
	Harmony     string
	GABS        string
	// Repo is the worktree being set up; gabs.exe is looked for in its
	// sibling worktrees and main checkout when nothing names one.
	Repo string
}

// Discover resolves Inputs: each override, then its environment variable,
// then Steam (the registry's install path and every library in
// steamapps/libraryfolders.vdf) for the game and Harmony, and the peer
// worktrees for gabs.exe.
func Discover(o Overrides) (*Inputs, error) {
	in := &Inputs{Sources: map[string]string{}}
	var libraries []string
	var libErr error
	if o.Explicit {
		if o.RimWorldDir == "" || o.Harmony == "" || o.HarmonyMod == "" || o.GABS == "" || o.BridgeDir == "" || o.RimBridgeSDK == "" {
			return nil, fmt.Errorf("explicit setup requires -rimworld, -harmony, -harmony-mod, -gabs, -bridge and -sdk")
		}
		in.CleanProfile = true
		if !isFile(filepath.Join(o.HarmonyMod, "About", "About.xml")) || !isFile(filepath.Join(o.HarmonyMod, "Current", "Assemblies", "HarmonyMod.dll")) {
			return nil, fmt.Errorf("explicit setup requires the complete Harmony runtime mod, not only 0Harmony.dll")
		}
	} else {
		libraries, libErr = SteamLibraries()
	}

	dir, source := o.RimWorldDir, "-rimworld"
	if dir == "" {
		dir, source = os.Getenv(RimWorldDirEnv), RimWorldDirEnv
	}
	if dir == "" {
		for _, lib := range libraries {
			candidate := filepath.Join(lib, "steamapps", "common", "RimWorld")
			if isFile(filepath.Join(candidate, "RimWorldWin64.exe")) {
				dir, source = candidate, "steam library "+lib
				break
			}
		}
	}
	if dir == "" {
		if libErr != nil {
			return nil, fmt.Errorf("no RimWorld install: %v; set %s or pass -rimworld", libErr, RimWorldDirEnv)
		}
		return nil, fmt.Errorf("no RimWorld install in the Steam libraries %v; set %s or pass -rimworld", libraries, RimWorldDirEnv)
	}
	dir = absClean(dir)
	if !isFile(filepath.Join(dir, "RimWorldWin64.exe")) {
		return nil, fmt.Errorf("%s (%s) holds no RimWorldWin64.exe", dir, source)
	}
	in.RimWorldDir, in.Sources["rimworld"] = dir, source
	in.ManagedDir = filepath.Join(dir, "RimWorldWin64_Data", "Managed")
	if !isFile(filepath.Join(in.ManagedDir, "Assembly-CSharp.dll")) {
		return nil, fmt.Errorf("%s holds no Assembly-CSharp.dll", in.ManagedDir)
	}
	in.RimBridgeSDK = filepath.Join(dir, "Mods", "RimBridgeServer", "1.6", "Assemblies")
	if o.RimBridgeSDK != "" {
		in.RimBridgeSDK = absClean(o.RimBridgeSDK)
	}
	in.BridgeDir = o.BridgeDir
	if in.BridgeDir == "" {
		in.BridgeDir = filepath.Join(dir, "Mods", "RimBridgeServer")
	}
	if !isFile(filepath.Join(in.BridgeDir, "About", "About.xml")) {
		return nil, fmt.Errorf("bridge runtime %s holds no About/About.xml", in.BridgeDir)
	}
	if !isFile(filepath.Join(in.RimBridgeSDK, "RimBridgeServer.Sdk.dll")) {
		return nil, fmt.Errorf("RimBridgeServer is not installed under %s (expected %s)", filepath.Join(dir, "Mods"), filepath.Join(in.RimBridgeSDK, "RimBridgeServer.Sdk.dll"))
	}

	harmony, source := o.Harmony, "-harmony"
	if harmony == "" {
		harmony, source = os.Getenv(HarmonyEnv), HarmonyEnv
	}
	if harmony == "" {
		for _, candidate := range harmonyCandidates(dir, libraries) {
			if isFile(candidate) {
				harmony, source = candidate, "steam workshop"
				break
			}
		}
	}
	if harmony == "" {
		return nil, fmt.Errorf("no 0Harmony.dll: subscribe to Harmony (workshop item %s) or set %s", HarmonyWorkshopID, HarmonyEnv)
	}
	harmony = absClean(harmony)
	if !isFile(harmony) {
		return nil, fmt.Errorf("%s (%s) is not a file", harmony, source)
	}
	if o.Explicit {
		buildInfo, err := os.Stat(harmony)
		if err != nil {
			return nil, err
		}
		runtimeInfo, err := os.Stat(filepath.Join(o.HarmonyMod, "Current", "Assemblies", "0Harmony.dll"))
		if err != nil {
			return nil, fmt.Errorf("harmony runtime DLL: %w", err)
		}
		if !os.SameFile(buildInfo, runtimeInfo) {
			return nil, fmt.Errorf("-harmony must name the DLL inside -harmony-mod/Current/Assemblies")
		}
	}
	in.Harmony, in.Sources["harmony"] = harmony, source
	in.HarmonyMod = o.HarmonyMod

	gabs, source := o.GABS, "-gabs"
	if gabs == "" {
		gabs, source = os.Getenv(GABSEnv), GABSEnv
	}
	var searched []string
	if gabs == "" {
		gabs, source, searched = findPeerGABS(o.Repo)
	}
	if gabs == "" {
		return nil, fmt.Errorf("no bridge/gabs/*/gabs.exe under .rimgovernor in this worktree, its main checkout or a sibling worktree (searched %s); set %s or pass -gabs", strings.Join(searched, ", "), GABSEnv)
	}
	gabs = absClean(gabs)
	if !isFile(gabs) {
		return nil, fmt.Errorf("%s (%s) is not a file", gabs, source)
	}
	in.GABS, in.Sources["gabs"] = gabs, source
	return in, nil
}

// harmonyCandidates are where a Steam Harmony lives: the workshop item in
// any library (the game's own library first), then a copy under the
// game's Mods.
func harmonyCandidates(rimworld string, libraries []string) []string {
	tail := filepath.Join("steamapps", "workshop", "content", RimWorldAppID, HarmonyWorkshopID, "Current", "Assemblies", "0Harmony.dll")
	var out []string
	if own := filepath.Dir(filepath.Dir(filepath.Dir(rimworld))); filepath.Base(filepath.Dir(rimworld)) == "common" {
		out = append(out, filepath.Join(own, tail))
	}
	for _, lib := range libraries {
		out = append(out, filepath.Join(lib, tail))
	}
	out = append(out, filepath.Join(rimworld, "Mods", "Harmony", "Current", "Assemblies", "0Harmony.dll"))
	return out
}

// GABSRelative is where the bridge root keeps gabs.exe (the default
// nativeaccept.GABSExecutable resolves).
const GABSRelative = "gabs/gabs-v1.1.1-windows-amd64/gabs.exe"

// findPeerGABS looks for an installed gabs.exe under repo's own bridge
// root, its main checkout's, then every sibling worktree's: the
// GABSRelative path first, then any other bridge/gabs/*/gabs.exe (a
// release the bridge root was installed with by hand). It also returns
// the checkouts it searched, for the error when none holds one.
func findPeerGABS(repo string) (path, source string, searched []string) {
	if repo == "" {
		return "", "", nil
	}
	for _, root := range PeerCheckouts(repo) {
		searched = append(searched, root)
		if found := installedGABS(filepath.Join(root, ".rimgovernor", "bridge")); found != "" {
			return found, "peer checkout " + root, searched
		}
	}
	return "", "", searched
}

// installedGABS is the gabs.exe a bridge root holds: GABSRelative when
// present, else the newest-named bridge/gabs/*/gabs.exe, else "".
func installedGABS(bridge string) string {
	if candidate := filepath.Join(bridge, filepath.FromSlash(GABSRelative)); isFile(candidate) {
		return candidate
	}
	matches, _ := filepath.Glob(filepath.Join(bridge, "gabs", "*", "gabs.exe"))
	sort.Sort(sort.Reverse(sort.StringSlice(matches)))
	for _, m := range matches {
		if isFile(m) {
			return m
		}
	}
	return ""
}

// PeerCheckouts lists repo, then the main checkout it is a worktree of,
// then that checkout's .claude/worktrees siblings: where a fresh worktree
// borrows machine-level files from. A repo that is itself the main
// checkout lists itself and its worktrees.
func PeerCheckouts(repo string) []string {
	repo = absClean(repo)
	out := []string{repo}
	seen := map[string]bool{repo: true}
	main := repo
	if data, err := os.ReadFile(filepath.Join(repo, ".git")); err == nil {
		// A worktree's .git is "gitdir: <main>/.git/worktrees/<name>".
		if line := strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(string(data)), "gitdir:")); line != "" {
			gitdir := absClean(line)
			if filepath.Base(filepath.Dir(gitdir)) == "worktrees" {
				main = filepath.Dir(filepath.Dir(filepath.Dir(gitdir)))
			}
		}
	}
	if !seen[main] {
		seen[main] = true
		out = append(out, main)
	}
	entries, _ := os.ReadDir(filepath.Join(main, ".claude", "worktrees"))
	var siblings []string
	for _, e := range entries {
		if e.IsDir() {
			siblings = append(siblings, filepath.Join(main, ".claude", "worktrees", e.Name()))
		}
	}
	// Newest sibling first: the most recently set-up peer is the most
	// likely to hold a complete layout.
	sort.Slice(siblings, func(i, j int) bool {
		a, _ := os.Stat(siblings[i])
		b, _ := os.Stat(siblings[j])
		if a == nil || b == nil {
			return a != nil
		}
		return a.ModTime().After(b.ModTime())
	})
	for _, s := range siblings {
		s = absClean(s)
		if !seen[s] {
			seen[s] = true
			out = append(out, s)
		}
	}
	return out
}

// SteamLibraries lists the Steam library roots on this machine: the
// install directory from the registry (or the conventional Program Files
// location), plus every path in its steamapps/libraryfolders.vdf.
func SteamLibraries() ([]string, error) {
	var roots []string
	if path, err := steamInstallPath(); err == nil && path != "" {
		roots = append(roots, path)
	}
	for _, conventional := range []string{
		filepath.Join(os.Getenv("ProgramFiles(x86)"), "Steam"),
		filepath.Join(os.Getenv("ProgramFiles"), "Steam"),
	} {
		if isDir(conventional) {
			roots = append(roots, conventional)
		}
	}
	if len(roots) == 0 {
		return nil, errors.New("steam is not installed (no registry install path, no Program Files\\Steam)")
	}
	var out []string
	seen := map[string]bool{}
	add := func(p string) {
		p = absClean(p)
		key := strings.ToLower(p)
		if !seen[key] && isDir(p) {
			seen[key] = true
			out = append(out, p)
		}
	}
	for _, root := range roots {
		add(root)
		if data, err := os.ReadFile(filepath.Join(root, "steamapps", "libraryfolders.vdf")); err == nil {
			for _, p := range LibraryPaths(string(data)) {
				add(p)
			}
		}
	}
	return out, nil
}

var vdfPath = regexp.MustCompile(`(?m)^\s*"path"\s*"((?:[^"\\]|\\.)*)"`)

// LibraryPaths extracts the "path" values of a libraryfolders.vdf, with
// its doubled backslashes undone.
func LibraryPaths(vdf string) []string {
	var out []string
	for _, m := range vdfPath.FindAllStringSubmatch(vdf, -1) {
		out = append(out, strings.NewReplacer(`\\`, `\`, `\"`, `"`).Replace(m[1]))
	}
	return out
}

func isFile(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

func isDir(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

// absClean is path absolute and cleaned, with the on-disk casing when it
// exists (the registry spells Steam's install path in lower case).
func absClean(path string) string {
	if abs, err := filepath.Abs(path); err == nil {
		path = abs
	}
	if real, err := filepath.EvalSymlinks(path); err == nil {
		return filepath.Clean(real)
	}
	return filepath.Clean(path)
}
