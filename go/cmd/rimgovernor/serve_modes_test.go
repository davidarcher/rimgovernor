package main

import (
	"io"
	"maps"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

func withRoutineFamilies(t *testing.T, value string, set bool) {
	t.Helper()
	previous := lookupEnv
	lookupEnv = func(name string) (string, bool) {
		if name == routineFamiliesEnv {
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
// supervised clock, routine reviews and methods, every planner family, world
// evaluation and caravan tracking.
func TestServeDefaultsToAutonomousComposition(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if !c.playerControl || !c.clockControl || !c.routineReviews || !c.routineMethods || !c.caravanJourneyTracking || !c.worldEvaluation || c.chat {
		t.Fatalf("autonomous composition: %+v", c)
	}
	families := routineFamilies(&c)
	for _, entry := range families {
		if !*entry.Enabled {
			t.Fatalf("default left %s disabled", entry.Name)
		}
	}
	if got := c.activeRoutineFamilies(); len(got) != len(families) {
		t.Fatalf("active families %v did not cover every family", got)
	}
	if _, err := parseServe(serveBase(dir), io.Discard); err == nil {
		t.Fatal("autonomous serve accepted without a profile")
	}
	if _, err := parseServe(append(serveBase(dir), "--profile", "relative"), io.Discard); err == nil {
		t.Fatal("relative profile accepted")
	}
}

// --clock-test-acceleration pins every window to boosted Ultrafast; without
// it the windows follow the player's speed under player pacing (#875), and
// --clock-speed is gone.
func TestServeClockTestAccelerationPinsUltrafast(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
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
	if config.Start.Speed != k.Speed_SPEED_ULTRAFAST || config.Start.TestAcceleration || !config.Start.PlayerAccelerated || !config.FollowPlayerSpeed {
		t.Fatalf("player window start: %+v", config.Start)
	}
}

func TestServeObserveTakesNoControlOptions(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--observe"), io.Discard)
	if err != nil || c.playerControl || c.clockControl || c.routineReviews || c.routineMethods || c.profile != "" || len(c.activeRoutineFamilies()) != 0 {
		t.Fatalf("observe configuration: %+v %v", c, err)
	}
	for _, extra := range [][]string{{"--profile", dir}, {"--chat-model", "m"}, {"--resume"}, {"--clock-test-acceleration"}, {"unexpected"}} {
		if _, err := parseServe(append(append(serveBase(dir), "--observe"), extra...), io.Discard); err == nil {
			t.Fatalf("observe accepted %v", extra)
		}
	}
	withRoutineFamilies(t, "sleeping", true)
	if _, err := parseServe(append(serveBase(dir), "--observe"), io.Discard); err == nil {
		t.Fatal("observe accepted a routine family selection")
	}
}

// RIMGOVERNOR_ROUTINE_FAMILIES narrows the composed families for targeted
// or debug runs; an unknown name is refused rather than silently ignored.
func TestServeRoutineFamiliesSelection(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, " sleeping, husbandry ", true)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.Join(c.activeRoutineFamilies(), ","); got != "sleeping,husbandry" {
		t.Fatalf("selected families %q", got)
	}
	if !c.routineReviews || !c.routineMethods || c.routineComfortPlans {
		t.Fatalf("selection changed composition: %+v", c)
	}
	withRoutineFamilies(t, "sleeping,unknown", true)
	if _, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard); err == nil {
		t.Fatal("unknown family accepted")
	}
	// Family-scoped tuning flags need their family composed.
	withRoutineFamilies(t, "sleeping", true)
	for _, extra := range [][]string{{"--routine-resource-reserve", "Steel:100"}} {
		if _, err := parseServe(append(append(serveBase(dir), "--profile", dir), extra...), io.Discard); err == nil {
			t.Fatalf("accepted %v without its family", extra)
		}
	}
	withRoutineFamilies(t, "", true)
	// The resource family keeps the default floors; no flag sets them (#875).
	c, err = parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil || !c.resourceTargetsConfigured() || c.resourceTargets()["Steel"] != 200 || c.resourceTargets()["ComponentIndustrial"] != 10 {
		t.Fatal(c, err)
	}
	for _, gone := range []string{"--routine-resource-target", "--routine-component-target", "--routine-stone-block-target"} {
		if _, err := parseServe(append(serveBase(dir), "--profile", dir, gone, "1"), io.Discard); err == nil {
			t.Fatal("deleted flag accepted:", gone)
		}
	}
}

func TestServeResumeFlag(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil || c.resume {
		t.Fatal(c, err)
	}
	if c, err = parseServe(append(serveBase(dir), "--profile", dir, "--resume"), io.Discard); err != nil || !c.resume {
		t.Fatal(c, err)
	}
}

func TestServeChatEnabledByModelName(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--profile", dir, "--chat-model", "local"), io.Discard)
	if err != nil || !c.chat || c.chatModel != "local" {
		t.Fatal(c, err)
	}
	for _, extra := range [][]string{{"--chat-base-url", "http://127.0.0.1:1/v1"}} {
		if _, err := parseServe(append(append(serveBase(dir), "--profile", dir), extra...), io.Discard); err == nil {
			t.Fatalf("accepted %v", extra)
		}
	}
}

// A plain launch keeps the default resource floors (#875); an operator
// target replaces them, and a serve without the resource family keeps none.
func TestServeDefaultResourceFloors(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", true)
	c, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	thresholds, capabilities := routineCapabilities(c)
	if !maps.Equal(thresholds.ResourceTargets, policy.DefaultResourceTargets()) || thresholds.StoneBlockTarget != policy.DefaultStoneBlockTarget || !slices.Contains(capabilities.Methods, policy.MaintainResource) {
		t.Fatalf("default launch floors %v stone %d methods %v", thresholds.ResourceTargets, thresholds.StoneBlockTarget, capabilities.Methods)
	}
	withRoutineFamilies(t, "sleeping", true)
	c, err = parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if thresholds, _ = routineCapabilities(c); len(thresholds.ResourceTargets) != 0 || thresholds.StoneBlockTarget != 0 {
		t.Fatalf("floors without the resource family: %v stone %d", thresholds.ResourceTargets, thresholds.StoneBlockTarget)
	}
}
