package main

import (
	"io"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

func withRoundsFamilies(t *testing.T, value string, set bool) {
	t.Helper()
	previous := lookupEnv
	lookupEnv = func(name string) (string, bool) {
		if name == roundsFamiliesEnv {
			return value, set
		}
		return previous(name)
	}
	t.Cleanup(func() { lookupEnv = previous })
}

func serveBase(dir string) []string {
	return []string{"--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db")}
}

// serve with no mode flag is the autonomous composition: player control,
// supervised clock, rounds and methods, every planner family and world
// evaluation.
func TestServeDefaultsToAutonomousComposition(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if !c.playerControl || !c.clockControl || !c.roundsEnabled || !c.roundsMethods {
		t.Fatalf("autonomous composition: %+v", c)
	}
	families := roundsFamilies(&c)
	for _, entry := range families {
		if !*entry.Enabled {
			t.Fatalf("default left %s disabled", entry.Name)
		}
	}
	if got := c.activeRoundsFamilies(); len(got) != len(families) {
		t.Fatalf("active families %v did not cover every family", got)
	}
	// HoldGatherings is selected beside EnsureMood and EnsureComfort.
	_, capabilities := roundsCapabilities(c)
	for _, goal := range []policy.ConcernID{policy.HoldGatherings, policy.EnsureComfort} {
		if !slices.Contains(capabilities.Methods, goal) {
			t.Errorf("default composition lacks %s: %v", goal, capabilities.Methods)
		}
	}
	if !c.roundsGatheringPlans || !c.roundsMoodPlans {
		t.Error("default composition left the gathering or mood planner off")
	}
	if _, err := parseServe(serveBase(dir), io.Discard); err == nil {
		t.Fatal("autonomous serve accepted without a profile")
	}
	if _, err := parseServe(append(serveBase(dir), "--profile", "relative"), io.Discard); err == nil {
		t.Fatal("relative profile accepted")
	}
}

// --clock-test-acceleration pins every window to boosted Ultrafast; without
// it the windows follow the player's speed under player pacing, and
// --clock-speed is gone.
func TestServeClockTestAccelerationPinsUltrafast(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", false)
	if _, err := parseServe(append(serveBase(dir), "--profile", dir, "--clock-speed", "Fast"), io.Discard); err == nil {
		t.Fatal("accepted the retired --clock-speed")
	}
	c, err := parseServe(append(serveBase(dir), "--profile", dir, "--clock-test-acceleration"), io.Discard)
	if err != nil || !c.clockTestAcceleration {
		t.Fatalf("acceleration: %+v %v", c, err)
	}
	config := serviceClockConfig(dir, c.clockTestAcceleration, defaultClockWindowTicks, uint32(c.clockBlindTicks))
	if config.Start.Speed != k.Speed_SPEED_ULTRAFAST || !config.Start.TestAcceleration || config.Start.PlayerAccelerated || config.FollowPlayerSpeed {
		t.Fatalf("window start: %+v", config.Start)
	}
	config = serviceClockConfig(dir, false, defaultClockWindowTicks, 0)
	if config.Start.Speed != k.Speed_SPEED_ULTRAFAST || config.Start.TestAcceleration || !config.Start.PlayerAccelerated || config.FollowPlayerSpeed {
		t.Fatalf("player window start: %+v", config.Start)
	}
}

func TestServeObserveTakesNoControlOptions(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--observe"), io.Discard)
	if err != nil || c.playerControl || c.clockControl || c.roundsEnabled || c.roundsMethods || c.profile != "" || len(c.activeRoundsFamilies()) != 0 {
		t.Fatalf("observe configuration: %+v %v", c, err)
	}
	for _, extra := range [][]string{{"--profile", dir}, {"--resume"}, {"--clock-test-acceleration"}, {"unexpected"}} {
		if _, err := parseServe(append(append(serveBase(dir), "--observe"), extra...), io.Discard); err == nil {
			t.Fatalf("observe accepted %v", extra)
		}
	}
	withRoundsFamilies(t, "sleeping", true)
	if _, err := parseServe(append(serveBase(dir), "--observe"), io.Discard); err == nil {
		t.Fatal("observe accepted a routine family selection")
	}
}

// RIMGOVERNOR_ROUTINE_FAMILIES narrows the composed families for targeted
// or debug runs; an unknown name is refused rather than silently ignored.
func TestServeRoundsFamiliesSelection(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, " sleeping, husbandry ", true)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.Join(c.activeRoundsFamilies(), ","); got != "sleeping,husbandry" {
		t.Fatalf("selected families %q", got)
	}
	if !c.roundsEnabled || !c.roundsMethods || c.roundsComfortPlans {
		t.Fatalf("selection changed composition: %+v", c)
	}
	withRoundsFamilies(t, "sleeping,unknown", true)
	if _, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard); err == nil {
		t.Fatal("unknown family accepted")
	}
	withRoundsFamilies(t, "", true)
	// The resource family runs on derived needs; no flag sets floors.
	c, err = parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil || !c.resourceTargetsConfigured() {
		t.Fatal(c, err)
	}
	for _, gone := range []string{"--routine-resource-target", "--routine-component-target", "--routine-stone-block-target", "--routine-resource-reserve", "--routine-resource-stop"} {
		if _, err := parseServe(append(serveBase(dir), "--profile", dir, gone, "1"), io.Discard); err == nil {
			t.Fatal("deleted flag accepted:", gone)
		}
	}
}

func TestServeResumeFlag(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil || c.resume {
		t.Fatal(c, err)
	}
	if c, err = parseServe(append(serveBase(dir), "--profile", dir, "--resume"), io.Discard); err != nil || !c.resume {
		t.Fatal(c, err)
	}
}

// The resource family composes by family selection alone: no launch carries a
// floor.
func TestServeResourceFamilyComposition(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", true)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if _, capabilities := roundsCapabilities(c); !slices.Contains(capabilities.Methods, policy.MaintainResource) {
		t.Fatalf("default launch methods %v", capabilities.Methods)
	}
	withRoundsFamilies(t, "sleeping", true)
	if c, err = parseServe(append(serveBase(dir), "--profile", dir), io.Discard); err != nil {
		t.Fatal(err)
	}
	if _, capabilities := roundsCapabilities(c); slices.Contains(capabilities.Methods, policy.MaintainResource) {
		t.Fatalf("methods without the resource family %v", capabilities.Methods)
	}
}

// Every family the routinefamily package declares is composed by the
// registry, and the registry names nothing else.
func TestRoundsFamiliesRegistryMatchesRoutinefamily(t *testing.T) {
	var c serveConfig
	var got []routinefamily.Family
	for _, entry := range roundsFamilies(&c) {
		got = append(got, entry.Name)
	}
	if !slices.Equal(got, routinefamily.All()) {
		t.Fatalf("registry %v\n declared %v", got, routinefamily.All())
	}
}
