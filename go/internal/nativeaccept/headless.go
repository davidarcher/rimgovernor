// Package nativeaccept is the headless-launch and disposable-worker harness for
// the native acceptance
// binaries under go/internal/nativeaccept/cmd. It owns the "generic native call is
// fine in an acceptance harness" carve-out via bridge.Client.NativeCall, rather than
// loosening the bridge package's own reviewed-reads boundary.
package nativeaccept

import (
	"encoding/json"
	"encoding/xml"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// NativePackage is the unified native mod's package ID.
const NativePackage = "davidarcher.rimgovernor.native"

// LegacyPackages are split/superseded native package IDs that must never coexist
// with the unified package in a fresh worker's Mods folder or ModsConfig.xml.
var LegacyPackages = map[string]bool{
	"davidarcher.rimgovernor.observations": true,
	"redeyedev.headlessrim":                true,
}

func casefold(s string) string { return strings.ToLower(s) }

// RequireNativePackage validates the complete unified package before preparing a
// private worker: the package's files must exist, its About.xml must self-identify
// as NativePackage, and no legacy split package may be installed alongside it.
func RequireNativePackage(mods string) error {
	pkg := filepath.Join(mods, "RimGovernor")
	for _, relative := range []string{
		filepath.Join("About", "About.xml"),
		filepath.Join("Assemblies", "RimGovernor.Runtime.dll"),
		filepath.Join("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"),
	} {
		if info, err := os.Stat(filepath.Join(pkg, relative)); err != nil || info.IsDir() {
			return fmt.Errorf("missing unified native input: %s", filepath.Join(pkg, relative))
		}
	}
	identity, err := packageID(filepath.Join(pkg, "About", "About.xml"))
	if err != nil {
		return err
	}
	if casefold(identity) != NativePackage {
		return fmt.Errorf("RimGovernor package metadata does not identify the unified native mod")
	}
	matches, err := filepath.Glob(filepath.Join(mods, "*", "About", "About.xml"))
	if err != nil {
		return fmt.Errorf("scan installed packages: %w", err)
	}
	unified := 0
	for _, metadata := range matches {
		other, err := packageID(metadata)
		if err != nil {
			return err
		}
		folded := casefold(other)
		if folded == NativePackage {
			unified++
		}
		if LegacyPackages[folded] {
			return fmt.Errorf("remove split native packages from fresh worker inputs: %s", filepath.Dir(filepath.Dir(metadata)))
		}
	}
	if unified != 1 {
		return fmt.Errorf("fresh worker inputs require exactly one unified native package, found %d", unified)
	}
	return nil
}

func packageID(aboutXMLPath string) (string, error) {
	data, err := os.ReadFile(aboutXMLPath)
	if err != nil {
		return "", fmt.Errorf("read %s: %w", aboutXMLPath, err)
	}
	_, root, err := parseXML(data)
	if err != nil {
		return "", fmt.Errorf("parse %s: %w", aboutXMLPath, err)
	}
	return strings.TrimSpace(root.childText("packageId")), nil
}

// ExpansionPrefix is the package-ID prefix every official Ludeon expansion
// (Royalty, Ideology, Biotech, Anomaly, Odyssey) shares; the base game itself
// is exactly CorePackage.
const (
	CorePackage     = "ludeon.rimworld"
	ExpansionPrefix = "ludeon.rimworld."
)

// ExpansionsEnv names the environment variable a harness run may set to opt
// specific expansions back in (comma-separated, e.g. "royalty,biotech" or the
// full "ludeon.rimworld.royalty") when a Config leaves Expansions nil.
const ExpansionsEnv = "RIMGOVERNOR_ACCEPT_EXPANSIONS"

// ExpansionPackage normalizes a short expansion name ("royalty") or a full
// package ID ("ludeon.rimworld.royalty") to its casefolded package ID.
func ExpansionPackage(name string) (string, error) {
	folded := casefold(strings.TrimSpace(name))
	if folded == "" || folded == CorePackage {
		return "", fmt.Errorf("expansion name must not be empty or the core package: %q", name)
	}
	if !strings.HasPrefix(folded, ExpansionPrefix) {
		folded = ExpansionPrefix + folded
	}
	if strings.ContainsAny(strings.TrimPrefix(folded, ExpansionPrefix), ". \t") {
		return "", fmt.Errorf("invalid expansion name: %q", name)
	}
	return folded, nil
}

// ExpansionsFromEnv parses ExpansionsEnv; unset or empty means no expansions.
func ExpansionsFromEnv() ([]string, error) {
	raw := strings.TrimSpace(os.Getenv(ExpansionsEnv))
	if raw == "" {
		return nil, nil
	}
	var out []string
	for _, part := range strings.Split(raw, ",") {
		if strings.TrimSpace(part) == "" {
			continue
		}
		id, err := ExpansionPackage(part)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", ExpansionsEnv, err)
		}
		out = append(out, id)
	}
	return out, nil
}

// PrepareNativeModConfig rewrites ModsConfig.xml's activeMods to the core game,
// only the requested expansions, and exactly brrainz.harmony,
// brrainz.rimbridgeserver and NativePackage, removing every other official
// expansion plus legacy or duplicate entries first. Acceptance runs are
// Core-only by default (issue #91): each active expansion adds def loading and
// per-tick systems no harness needs unless it tests that DLC. knownExpansions
// is left alone so RimWorld still recognizes the installed content.
func PrepareNativeModConfig(modsConfigXMLPath string, expansions ...string) error {
	data, err := os.ReadFile(modsConfigXMLPath)
	if err != nil {
		return fmt.Errorf("read ModsConfig.xml: %w", err)
	}
	header, root, err := parseXML(data)
	if err != nil {
		return fmt.Errorf("parse ModsConfig.xml: %w", err)
	}
	active := root.find("activeMods")
	if active == nil {
		return fmt.Errorf("native profile is missing activeMods")
	}
	required := []string{"brrainz.harmony", "brrainz.rimbridgeserver", NativePackage}
	drop := map[string]bool{}
	for _, id := range required {
		drop[casefold(id)] = true
	}
	for id := range LegacyPackages {
		drop[casefold(id)] = true
	}
	keep := map[string]bool{}
	var wanted []string
	for _, name := range expansions {
		id, err := ExpansionPackage(name)
		if err != nil {
			return err
		}
		if !keep[id] {
			keep[id] = true
			wanted = append(wanted, id)
		}
	}
	item := func(id string) xmlItem {
		return xmlItem{elem: &xmlElem{name: xml.Name{Local: "li"}, kids: []xmlItem{{text: id}}}}
	}
	kept := active.kids[:0]
	seenCore := false
	for _, kid := range active.kids {
		if kid.elem != nil && kid.elem.name.Local == "li" {
			id := casefold(kid.elem.text())
			if drop[id] || strings.HasPrefix(id, ExpansionPrefix) {
				continue
			}
			if id == CorePackage {
				if seenCore {
					continue
				}
				seenCore = true
				kept = append(kept, kid)
				// Expansions load directly after the core game, as RimWorld's
				// own mod manager orders them.
				for _, id := range wanted {
					kept = append(kept, item(id))
				}
				continue
			}
		}
		kept = append(kept, kid)
	}
	if !seenCore {
		return fmt.Errorf("native profile activeMods is missing %s", CorePackage)
	}
	active.kids = kept
	for _, id := range required {
		active.kids = append(active.kids, item(id))
	}
	out, err := writeXML(header, root)
	if err != nil {
		return fmt.Errorf("encode ModsConfig.xml: %w", err)
	}
	return os.WriteFile(modsConfigXMLPath, out, 0644)
}

// requireOwnedLaunch enforces launchMode == DirectPath and strips stopProcessName so
// GABS uses its own recorded process identity instead of matching any process by name.
func requireOwnedLaunch(game map[string]any) error {
	if mode, _ := game["launchMode"].(string); mode != "DirectPath" {
		return fmt.Errorf("disposable profiles require DirectPath PID-owned launches")
	}
	delete(game, "stopProcessName")
	return nil
}

func loadConfig(path string) (map[string]any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", path, err)
	}
	var config map[string]any
	if err := json.Unmarshal(data, &config); err != nil {
		return nil, fmt.Errorf("parse %s: %w", path, err)
	}
	return config, nil
}

func writeConfig(path string, config map[string]any) error {
	data, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return fmt.Errorf("encode %s: %w", path, err)
	}
	return os.WriteFile(path, data, 0644)
}

func gameSection(config map[string]any) (map[string]any, error) {
	games, ok := config["games"].(map[string]any)
	if !ok {
		return nil, fmt.Errorf("configuration is missing games")
	}
	game, ok := games["rimgovernor-trial"].(map[string]any)
	if !ok {
		return nil, fmt.Errorf("configuration is missing games.rimgovernor-trial")
	}
	return game, nil
}

// GABSExecutable resolves the GABS binary path from rimgovernor.gabsExecutable in
// configDir/config.json (default: configDir if empty, root/config), defaulting to
// gabs/gabs-v1.1.1-windows-amd64/gabs.exe relative to root.
func GABSExecutable(root, configDir string) (string, error) {
	root = mustAbs(root)
	if configDir == "" {
		configDir = filepath.Join(root, "config")
	} else {
		configDir = mustAbs(configDir)
	}
	config, err := loadConfig(filepath.Join(configDir, "config.json"))
	if err != nil {
		return "", err
	}
	configured := ""
	if section, ok := config["rimgovernor"].(map[string]any); ok {
		configured, _ = section["gabsExecutable"].(string)
	}
	if configured == "" {
		configured = filepath.Join("gabs", "gabs-v1.1.1-windows-amd64", "gabs.exe")
	}
	if filepath.IsAbs(configured) {
		return configured, nil
	}
	return filepath.Join(root, configured), nil
}

func mustAbs(path string) string {
	abs, err := filepath.Abs(path)
	if err != nil {
		return path
	}
	return abs
}

// IsolatedRoot builds a fresh disposable worker root: it copies the GABS binary
// (respecting a configured relative path), config.json, and profile Config/Saves
// files into a brand-new directory. destination must not already exist.
func IsolatedRoot(source, destination string) (string, error) {
	source, destination = mustAbs(source), mustAbs(destination)
	if _, err := os.Stat(destination); err == nil {
		return "", fmt.Errorf("worker root already exists; use a fresh directory")
	} else if !os.IsNotExist(err) {
		return "", err
	}
	config, err := loadConfig(filepath.Join(source, "config", "config.json"))
	if err != nil {
		return "", err
	}
	game, err := gameSection(config)
	if err != nil {
		return "", err
	}
	if err := requireOwnedLaunch(game); err != nil {
		return "", err
	}
	binary, err := GABSExecutable(source, filepath.Join(source, "config"))
	if err != nil {
		return "", err
	}
	var relative string
	if rel, err := filepath.Rel(source, binary); err == nil && !strings.HasPrefix(rel, "..") {
		relative = rel
	} else {
		relative = filepath.Join("gabs", filepath.Base(binary))
	}
	section, _ := config["rimgovernor"].(map[string]any)
	if section == nil {
		section = map[string]any{}
		config["rimgovernor"] = section
	}
	section["gabsExecutable"] = filepath.ToSlash(relative)
	target := filepath.Join(destination, relative)
	if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
		return "", err
	}
	if err := copyFile(binary, target); err != nil {
		return "", err
	}
	if err := os.MkdirAll(filepath.Join(destination, "config"), 0755); err != nil {
		return "", err
	}
	if err := writeConfig(filepath.Join(destination, "config", "config.json"), config); err != nil {
		return "", err
	}
	for _, relative := range []string{
		filepath.Join("profile", "Config", "Prefs.xml"),
		filepath.Join("profile", "Config", "ModsConfig.xml"),
		filepath.Join("profile", "Saves", "RimGovernor-tribal8-baseline.rws"),
	} {
		target := filepath.Join(destination, relative)
		if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
			return "", err
		}
		if err := copyFile(filepath.Join(source, relative), target); err != nil {
			return "", err
		}
	}
	return destination, nil
}

// CopyFile copies one regular file, creating or truncating destination.
func CopyFile(source, destination string) error { return copyFile(source, destination) }

func copyFile(source, destination string) error {
	in, err := os.Open(source)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(destination, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0644)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// PrepareRendered launches the unified native package without batch-mode flags, for
// a visible window. Returns the rewritten config directory (root/config).
func PrepareRendered(root string, expansions ...string) (string, error) {
	root = mustAbs(root)
	configuration := filepath.Join(root, "config")
	config, err := loadConfig(filepath.Join(configuration, "config.json"))
	if err != nil {
		return "", err
	}
	game, err := gameSection(config)
	if err != nil {
		return "", err
	}
	if err := requireOwnedLaunch(game); err != nil {
		return "", err
	}
	workingDir, _ := game["workingDir"].(string)
	if err := RequireNativePackage(filepath.Join(workingDir, "Mods")); err != nil {
		return "", err
	}
	profile := filepath.Join(root, "profile")
	if err := PrepareNativeModConfig(filepath.Join(profile, "Config", "ModsConfig.xml"), expansions...); err != nil {
		return "", err
	}
	game["args"] = []any{
		"-savedatafolder=" + profile, "-logFile", filepath.Join(root, "Player.log"),
		"-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720", "-rimgovernor-pause-on-load",
	}
	if err := writeConfig(filepath.Join(configuration, "config.json"), config); err != nil {
		return "", err
	}
	return configuration, nil
}

// Prepare builds the headless-profile subdirectory (copying Prefs.xml/ModsConfig.xml
// and every profile/Saves/*.rws save into it, Prefs.xml trimmed per HeadlessPrefs), rewrites config.json's args for batch
// mode, writes config-headless/config.json, and returns that directory.
func Prepare(root string, expansions ...string) (string, error) {
	root = mustAbs(root)
	config, err := loadConfig(filepath.Join(root, "config", "config.json"))
	if err != nil {
		return "", err
	}
	game, err := gameSection(config)
	if err != nil {
		return "", err
	}
	if err := requireOwnedLaunch(game); err != nil {
		return "", err
	}
	workingDir, _ := game["workingDir"].(string)
	if err := RequireNativePackage(filepath.Join(workingDir, "Mods")); err != nil {
		return "", err
	}
	profile := filepath.Join(root, "headless-profile")
	if err := os.MkdirAll(filepath.Join(profile, "Config"), 0755); err != nil {
		return "", err
	}
	if err := os.MkdirAll(filepath.Join(profile, "Saves"), 0755); err != nil {
		return "", err
	}
	// The native clock journal (ClockEventJournal.cs, one XML row per event
	// under the save-data folder) outlives the game process. A headless run
	// is a fresh supervised session, and a service starting from cursor 1
	// pages every stale row before it sees a live event -- thousands of them
	// after a day of runs, enough to eat the first review's budget.
	if err := os.RemoveAll(filepath.Join(profile, "RimGovernorClockEvents")); err != nil {
		return "", err
	}
	for _, name := range []string{"Prefs.xml", "ModsConfig.xml"} {
		if err := copyFile(filepath.Join(root, "profile", "Config", name), filepath.Join(profile, "Config", name)); err != nil {
			return "", err
		}
	}
	if err := PrepareNativeModConfig(filepath.Join(profile, "Config", "ModsConfig.xml"), expansions...); err != nil {
		return "", err
	}
	if err := TrimPrefs(filepath.Join(profile, "Config", "Prefs.xml")); err != nil {
		return "", err
	}
	// Every save under profile/Saves -- not just the tribal8 baseline -- so a
	// headless run (the default for the native acceptance binaries) can load
	// any variant save deposited there, e.g. by variantsavegen, the same way
	// a rendered run already can (PrepareRendered points RimWorld straight at
	// profile/Saves with no copy step). The baseline itself stays required:
	// its absence is exactly the "fresh checkout, save not staged yet" state
	// docs/players/setup.md describes.
	const baseline = "RimGovernor-tribal8-baseline.rws"
	if _, err := os.Stat(filepath.Join(root, "profile", "Saves", baseline)); err != nil {
		return "", fmt.Errorf("required baseline save missing: %w", err)
	}
	saves, err := filepath.Glob(filepath.Join(root, "profile", "Saves", "*.rws"))
	if err != nil {
		return "", err
	}
	for _, save := range saves {
		if err := copyFile(save, filepath.Join(profile, "Saves", filepath.Base(save))); err != nil {
			return "", err
		}
	}
	game["args"] = []any{
		"-savedatafolder=" + profile, "-logFile", filepath.Join(root, "HeadlessPlayer.log"),
		"-batchmode", "-nographics", "-rimgovernor-pause-on-load",
	}
	destination := filepath.Join(root, "config-headless")
	if err := os.MkdirAll(destination, 0755); err != nil {
		return "", err
	}
	if err := writeConfig(filepath.Join(destination, "config.json"), config); err != nil {
		return "", err
	}
	return destination, nil
}

// SaveExpansions reads the official expansions a save at
// root/profile/Saves/<saveName>.rws was recorded with (its <modIds> header).
// A save refuses to load (save.missing_mods) under a profile missing any of
// them, so a harness that loads a save passes its result to Config.Expansions
// rather than relying on the Core-only default.
func SaveExpansions(root, saveName string) ([]string, error) {
	path := filepath.Join(mustAbs(root), "profile", "Saves", saveName+".rws")
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("read save header: %w", err)
	}
	defer f.Close()
	// modIds sit in the first few hundred bytes of the meta block; 64 KiB
	// bounds the scan of a multi-megabyte save.
	head := make([]byte, 64*1024)
	n, err := io.ReadFull(f, head)
	if err != nil && err != io.ErrUnexpectedEOF && err != io.EOF {
		return nil, fmt.Errorf("read save header: %w", err)
	}
	text := string(head[:n])
	start := strings.Index(text, "<modIds>")
	end := strings.Index(text, "</modIds>")
	if start < 0 || end < start {
		return nil, fmt.Errorf("save %s has no modIds header in its first %d bytes", saveName, n)
	}
	var out []string
	for _, m := range saveModItem.FindAllStringSubmatch(text[start:end], -1) {
		id := casefold(m[1])
		if strings.HasPrefix(id, ExpansionPrefix) {
			out = append(out, id)
		}
	}
	return out, nil
}

var saveModItem = regexp.MustCompile(`<li>\s*([^<\s]+)\s*</li>`)

// UseSaveExpansions sets Expansions to the union of the official expansions
// the named saves were recorded with, so PrepareConfig keeps exactly those
// active. Empty names are skipped; when every name is empty Expansions is
// left as it was (nil: the Core-only default or ExpansionsEnv).
func (c *Config) UseSaveExpansions(saveNames ...string) error {
	seen := map[string]bool{}
	var out []string
	any := false
	for _, name := range saveNames {
		if name == "" {
			continue
		}
		any = true
		ids, err := SaveExpansions(c.Root, name)
		if err != nil {
			return err
		}
		for _, id := range ids {
			if !seen[id] {
				seen[id] = true
				out = append(out, id)
			}
		}
	}
	if !any {
		return nil
	}
	if out == nil {
		out = []string{}
	}
	c.Expansions = out
	return nil
}
