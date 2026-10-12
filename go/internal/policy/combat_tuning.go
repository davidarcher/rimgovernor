package policy

import (
	"encoding/json"
	"fmt"
	"strings"
)

// CombatTuningEnv names the test-only environment variable the live service
// reads a CombatTuning from. The value is a JSON object whose fields override
// DefaultCombatTuning; the result must pass Validate. Unset or empty, the
// override is inert and production keeps the default. The calibration sweep
// (`acceptance sweep -variant`) sets it on the child service to measure a
// tactic variant in the real game. Policy never reads the environment: serve
// reads the variable and hands ParseCombatTuning's result to the defense
// planner, which seeds every new fight's CombatMemory with it.
const CombatTuningEnv = "RIMGOVERNOR_COMBAT_TUNING"

// ParseCombatTuning decodes CombatTuningEnv's value over the default tuning.
// An empty value is no override (nil, nil); unknown fields, trailing data and
// out-of-range values are errors.
func ParseCombatTuning(raw string) (*CombatTuning, error) {
	if strings.TrimSpace(raw) == "" {
		return nil, nil
	}
	t := DefaultCombatTuning()
	dec := json.NewDecoder(strings.NewReader(raw))
	dec.DisallowUnknownFields()
	if err := dec.Decode(&t); err != nil {
		return nil, fmt.Errorf("%s: %w", CombatTuningEnv, err)
	}
	if dec.More() {
		return nil, fmt.Errorf("%s: trailing data after the JSON object", CombatTuningEnv)
	}
	if err := t.Validate(); err != nil {
		return nil, fmt.Errorf("%s: %w", CombatTuningEnv, err)
	}
	return &t, nil
}

// CombatTuning is the curated subset of DecideCombat's constants that a
// search may override per trial. It rides in CombatMemory.Tuning, so
// parallel trials each own theirs and DecideCombat stays pure; production
// leaves it nil and gets DefaultCombatTuning (the test-only CombatTuningEnv
// override aside). Every other combat constant stays a package const.
//
// Each field documents its valid range; Validate enforces it.
type CombatTuning struct {
	// DoorCloseRange: a hostile this close (cells) to the potshot door
	// shuts it. 1..DoorOpenRange.
	DoorCloseRange int
	// DoorOpenRange: the nearest hostile beyond this (cells) reopens the
	// potshot door; a choke door admits only beyond it. DoorCloseRange..30.
	DoorOpenRange int
	// DoorGunners is how many gunners shoot from inside the potshot door.
	// 1..8.
	DoorGunners int
	// FlankMinDefenders is the hold size below which no flank is detached.
	// 2..30.
	FlankMinDefenders int
	// FlankSize is the flank detachment's gunner count. 1..8.
	FlankSize int
	// FlankLateral is how far (cells) beside the choke the flank anchors.
	// 1..20.
	FlankLateral int
	// GroupGap is the single-link gap (cells) between hostiles of one raid
	// group. 1..30.
	GroupGap int
	// KiteLeadRange: a hostile this close (cells) to the kiter starts the
	// lead, and an infestation kiter leads this far. 1..30.
	KiteLeadRange int
	// KiteSpeedRatio is how much faster than the fastest slow chaser a kiter
	// must be. 1..3.
	KiteSpeedRatio float64
	// ScatterCells is how far (cells) a scattering defender runs from the
	// raid. 1..60.
	ScatterCells int
	// DoseMargin is the cells beyond weapon range a combat drug is dosed.
	// 0..60.
	DoseMargin float64
	// RushRange: a hostile this close (cells) to the sapper breach sends
	// the posted brawlers at it. 1..30.
	RushRange int
}

// DefaultCombatTuning is the shipped behaviour; production always uses it.
func DefaultCombatTuning() CombatTuning {
	return CombatTuning{
		DoorCloseRange:    doorCloseRange,
		DoorOpenRange:     doorOpenRange,
		DoorGunners:       doorGunners,
		FlankMinDefenders: flankMinDefenders,
		FlankSize:         flankSize,
		FlankLateral:      flankLateral,
		GroupGap:          groupGap,
		KiteLeadRange:     kiteLeadRange,
		KiteSpeedRatio:    kiteSpeedRatio,
		ScatterCells:      scatterCells,
		DoseMargin:        doseMargin,
		RushRange:         rushRange,
	}
}

// Validate reports the first field outside its documented range.
func (t CombatTuning) Validate() error {
	ints := []struct {
		name      string
		v, lo, hi int
	}{
		{"DoorCloseRange", t.DoorCloseRange, 1, max(1, t.DoorOpenRange)},
		{"DoorOpenRange", t.DoorOpenRange, max(1, t.DoorCloseRange), 30},
		{"DoorGunners", t.DoorGunners, 1, 8},
		{"FlankMinDefenders", t.FlankMinDefenders, 2, 30},
		{"FlankSize", t.FlankSize, 1, 8},
		{"FlankLateral", t.FlankLateral, 1, 20},
		{"GroupGap", t.GroupGap, 1, 30},
		{"KiteLeadRange", t.KiteLeadRange, 1, 30},
		{"ScatterCells", t.ScatterCells, 1, 60},
		{"RushRange", t.RushRange, 1, 30},
	}
	for _, f := range ints {
		if f.v < f.lo || f.v > f.hi {
			return fmt.Errorf("combat tuning %s = %d outside %d..%d", f.name, f.v, f.lo, f.hi)
		}
	}
	floats := []struct {
		name      string
		v, lo, hi float64
	}{
		{"KiteSpeedRatio", t.KiteSpeedRatio, 1, 3},
		{"DoseMargin", t.DoseMargin, 0, 60},
	}
	for _, f := range floats {
		if !(f.v >= f.lo && f.v <= f.hi) {
			return fmt.Errorf("combat tuning %s = %g outside %g..%g", f.name, f.v, f.lo, f.hi)
		}
	}
	return nil
}

// tuning is the memory's tuning, the default when none is carried.
func (m CombatMemory) tuning() CombatTuning {
	if m.Tuning == nil {
		return DefaultCombatTuning()
	}
	return *m.Tuning
}

// WithTuning returns the memory carrying t for the rest of the fight.
func (m CombatMemory) WithTuning(t CombatTuning) CombatMemory {
	m.Tuning = &t
	return m
}
