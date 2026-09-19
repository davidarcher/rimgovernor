package main

import (
	"io"
	"path/filepath"
	"strings"
	"testing"

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
	return []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db")}
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

// Ultrafast is an ordinary --clock-speed; --clock-test-acceleration rides on
// it only, and reaches the window start request.
func TestServeClockTestAccelerationRequiresUltrafast(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	for _, speed := range []string{"Normal", "Fast", "Superfast"} {
		if _, err := parseServe(append(serveBase(dir), "--profile", dir, "--clock-speed", speed, "--clock-test-acceleration"), io.Discard); err == nil {
			t.Fatalf("test acceleration accepted at %s", speed)
		}
	}
	c, err := parseServe(append(serveBase(dir), "--profile", dir, "--clock-speed", "Ultrafast", "--clock-test-acceleration"), io.Discard)
	if err != nil || !c.clockTestAcceleration {
		t.Fatalf("ultrafast acceleration: %+v %v", c, err)
	}
	config := serviceClockConfig(dir, parseClockSpeed(c.clockSpeed), c.clockTestAcceleration, uint32(c.clockWindowTicks))
	if config.Start.Speed != k.Speed_SPEED_ULTRAFAST || !config.Start.TestAcceleration {
		t.Fatalf("window start: %+v", config.Start)
	}
	if c, err := parseServe(append(serveBase(dir), "--profile", dir, "--clock-speed", "Ultrafast"), io.Discard); err != nil || c.clockTestAcceleration {
		t.Fatalf("plain ultrafast: %+v %v", c, err)
	}
}

func TestServeObserveTakesNoControlOptions(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	c, err := parseServe(append(serveBase(dir), "--observe"), io.Discard)
	if err != nil || c.playerControl || c.clockControl || c.routineReviews || c.routineMethods || c.profile != "" || len(c.activeRoutineFamilies()) != 0 {
		t.Fatalf("observe configuration: %+v %v", c, err)
	}
	for _, extra := range [][]string{{"--profile", dir}, {"--resource-rule", "WoodLog:stop:10"}, {"--routine-project-limit", "2"}, {"--chat-model", "m"}, {"--resume"}, {"--clock-speed", "Fast"}, {"--clock-speed", "Ultrafast", "--clock-test-acceleration"}, {"--world-evaluation-food-margin-days", "1"}, {"unexpected"}} {
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
	for _, extra := range [][]string{{"--routine-resource-target", "Steel:100"}, {"--routine-stone-block-target", "60"}, {"--routine-resource-reserve", "Steel:100"}, {"--routine-allow-slaughter"}} {
		if _, err := parseServe(append(append(serveBase(dir), "--profile", dir), extra...), io.Discard); err == nil {
			t.Fatalf("accepted %v without its family", extra)
		}
	}
	withRoutineFamilies(t, "", true)
	c, err = parseServe(append(serveBase(dir), "--profile", dir, "--routine-resource-target", "Steel:100"), io.Discard)
	if err != nil || len(c.routineResourceTargets) != 1 {
		t.Fatal(c, err)
	}
	c, err = parseServe(append(serveBase(dir), "--profile", dir, "--routine-stone-block-target", "60"), io.Discard)
	if err != nil || c.routineStoneBlockTarget != 60 || !c.resourceTargetsConfigured() {
		t.Fatal(c, err)
	}
	if _, err := parseServe(append(serveBase(dir), "--profile", dir, "--routine-stone-block-target", "10001"), io.Discard); err == nil {
		t.Fatal("stone block target past the bill bound accepted")
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
	for _, extra := range [][]string{{"--chat-base-url", "http://127.0.0.1:1/v1"}, {"--chat-context-tokens", "8192"}, {"--chat-model", "m", "--chat-context-tokens", "100"}, {"--chat-model", "m", "--chat-max-output-tokens", "8000"}} {
		if _, err := parseServe(append(append(serveBase(dir), "--profile", dir), extra...), io.Discard); err == nil {
			t.Fatalf("accepted %v", extra)
		}
	}
}
