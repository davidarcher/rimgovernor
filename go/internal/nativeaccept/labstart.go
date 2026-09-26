package nativeaccept

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// The lab's world: a fixed seed on a temperate tile, generated at LabMapSize
// and then wiped, so every lab is the same map.
const (
	LabMapSize         = 100
	LabSeed            = "rimgovernor-lab"
	LabBiome           = "TemperateForest"
	DefaultLabColonist = 3
)

// LabStart is the blank lab (#729): a fixed-seed LabMapSize map wiped to
// Soil with Colonists fixture-made colonists (zero: DefaultLabColonist),
// clear weather, a pinned temperature and a quiet storyteller. It caches
// as RimGovernor-lab-<size>[-<expansions>][-c<n>] beside a .stamp file
// holding the installed mod's hash, so a mod or fixture rebuild
// regenerates it; a later start pays only the load.
type LabStart struct {
	Colonists int
}

func (l LabStart) colonists() int {
	if l.Colonists <= 0 {
		return DefaultLabColonist
	}
	return l.Colonists
}

func (l LabStart) base() DebugStart {
	return DebugStart{MapSize: LabMapSize, PlanetCoverage: DefaultPlanetCoverage, Seed: LabSeed, Biomes: LabBiome}
}

func (l LabStart) saves() []string      { return nil }
func (l LabStart) fixtureOps() []string { return nil }

func (l LabStart) world() worldSource {
	src := worldSource{seed: LabSeed, pinned: true}
	if name := labCacheName(l.colonists()); CachedStart() && startCache.root != "" {
		if _, have := cachedStartPath(name); have {
			src.save = name
		}
	}
	return src
}

// labCacheName is the lab save's profile name.
func labCacheName(colonists int) string {
	name := fmt.Sprintf("RimGovernor-lab-%d", LabMapSize)
	if len(startCache.expansions) > 0 {
		name += "-" + strings.ToLower(strings.Join(startCache.expansions, "-"))
	}
	if colonists != DefaultLabColonist {
		name += fmt.Sprintf("-c%d", colonists)
	}
	return strings.ReplaceAll(name, ".", "_")
}

// labStamp is the hash over the installed package's files (PackageFiles),
// the cache key a rebuild of the mod or its fixtures changes.
func labStamp(files map[string]string) string {
	keys := make([]string, 0, len(files))
	for k := range files {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	h := sha256.New()
	for _, k := range keys {
		fmt.Fprintf(h, "%s=%s\n", filepath.ToSlash(k), files[k])
	}
	return hex.EncodeToString(h.Sum(nil))[:16]
}

func labStampPath(name string) string {
	return filepath.Join(startCache.root, "profile", "Saves", name+".stamp")
}

// labReplyPath holds the test/lab_start reply (centre, colonist ids) the
// cached save was made with: a cached load replies it as the Lab start's
// Session.Prepared.
func labReplyPath(name string) string {
	return filepath.Join(startCache.root, "profile", "Saves", name+".lab.json")
}

// labCacheFresh reports whether the cached lab save exists, was recorded
// under the profile's expansions and carries stamp.
func labCacheFresh(name, stamp string) (bool, error) {
	path, have := cachedStartPath(name)
	if !have {
		return false, nil
	}
	if _, err := os.Stat(labReplyPath(name)); err != nil {
		return false, nil
	}
	recorded, err := os.ReadFile(labStampPath(name))
	if err != nil || strings.TrimSpace(string(recorded)) != stamp {
		return false, nil
	}
	stale, err := cachedStartStale(path, name)
	if err != nil {
		return false, err
	}
	return !stale, nil
}

func (l LabStart) load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error) {
	h := s.Harness
	if !Contains(s.Names, LabStartTool) {
		return nil, fmt.Errorf("%s not in discovery: rebuild the native mod with any -Fixture flag (every fixture build includes DebugStartFixture)", LabStartTool)
	}
	colonists := l.colonists()
	name := labCacheName(colonists)
	files, _ := s.Report["package_files"].(map[string]string)
	stamp := labStamp(files)
	cached := CachedStart() && startCache.root != ""
	startCache.seed = ""
	row := map[string]any{"kind": "lab", "mapSize": LabMapSize, "colonists": colonists, "save": name}
	fresh := false
	if cached {
		var err error
		if fresh, err = labCacheFresh(name, stamp); err != nil {
			return nil, err
		}
	}
	if fresh {
		if err := loadCachedStart(ctx, h, name); err != nil {
			return nil, err
		}
		raw, err := os.ReadFile(labReplyPath(name))
		if err != nil {
			return nil, fmt.Errorf("lab cache %s: %w", name, err)
		}
		var lab map[string]any
		if err := json.Unmarshal(raw, &lab); err != nil {
			return nil, fmt.Errorf("lab cache %s: %w", name, err)
		}
		row["lab"] = lab
		row["cached"] = true
	} else {
		if err := generateDebugStart(ctx, h, s.Names, l.base()); err != nil {
			return nil, err
		}
		lab, err := h.Call(ctx, "lab-start", LabStartTool, map[string]any{"colonists": colonists})
		if err != nil {
			return nil, fmt.Errorf("%s: %w", LabStartTool, err)
		}
		if ok, _ := AsBool(lab["success"]); !ok {
			return nil, fmt.Errorf("%s refused: %#v", LabStartTool, lab)
		}
		if size := int(AsNumber(lab["mapSize"])); size != LabMapSize {
			return nil, fmt.Errorf("%s made a %d map, want %d", LabStartTool, size, LabMapSize)
		}
		row["digest"] = lab["digest"]
		row["lab"] = lab
		row["cached"] = false
		if cached {
			if err := saveCachedStart(ctx, h, name); err != nil {
				return nil, err
			}
			raw, err := json.Marshal(lab)
			if err != nil {
				return nil, err
			}
			if err := os.WriteFile(labReplyPath(name), raw, 0o644); err != nil {
				return nil, err
			}
			if err := os.WriteFile(labStampPath(name), []byte(stamp+"\n"), 0o644); err != nil {
				return nil, err
			}
		}
	}
	// The lab is quiet by construction and the save persists it; applying
	// again marks the harness quiet and covers a lab made before a change
	// to the quiet op.
	if _, err := applyQuiet(ctx, h, Contains(s.Names, QuietStorytellerTool)); err != nil {
		return nil, err
	}
	s.Report["quiet"] = h.quiet
	return row, nil
}
