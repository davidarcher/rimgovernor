package main

import (
	"io"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestResourceRuleFlagsValidateAndPreservePreviousRules(t *testing.T) {
	var rules resourceRuleFlags
	for _, value := range []string{"WoodLog:allow:50", "Steel:defense_only:0", "Mod:Resource:stop:0"} {
		if err := rules.Set(value); err != nil {
			t.Fatal(err)
		}
	}
	want := append(resourceRuleFlags(nil), rules...)
	for _, value := range []string{"WoodLog:stop:0", "Gold:allow:-1", "Gold:allow:01", "Gold:allow:+1", "Gold:allow:9223372036854775808", "Gold:unknown:0", ":allow:0", "Gold"} {
		if err := rules.Set(value); err == nil || !reflect.DeepEqual(rules, want) {
			t.Fatal(value, rules, err)
		}
	}
	if rules[2].Resource != "Mod:Resource" || rules[1].Spending != policy.DefenseOnly {
		t.Fatal(rules)
	}
}

func TestServeResourceRules(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	config, err := parseServe(append(serveBase(dir), "--profile", dir, "--resource-rule", "WoodLog:stop:10"), io.Discard)
	if err != nil || len(config.resourceRules) != 1 || config.resourceRules[0].Reserve != 10 {
		t.Fatal(config, err)
	}
}

func TestServeRoutineProjectLimit(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	for _, value := range []string{"0", "9", "-1", "two"} {
		if _, err := parseServe(append(serveBase(dir), "--profile", dir, "--routine-project-limit", value), io.Discard); err == nil {
			t.Fatal(value)
		}
	}
	c, err := parseServe(append(serveBase(dir), "--profile", dir, "--routine-project-limit", "1"), io.Discard)
	if err != nil || c.routineProjectLimit != 1 || c.routineProjectAuto {
		t.Fatal(c, err)
	}
	// The default is unchanged; auto is the slot bound plus the distinct
	// worker census, and a later explicit value replaces it.
	c, err = parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil || c.routineProjectLimit != 2 || c.routineProjectAuto {
		t.Fatal(c, err)
	}
	c, err = parseServe(append(serveBase(dir), "--profile", dir, "--routine-project-limit", "auto"), io.Discard)
	if err != nil || c.routineProjectLimit != policy.MaxAutoDevelopmentProjects || !c.routineProjectAuto {
		t.Fatal(c, err)
	}
	thresholds, _ := routineCapabilities(c)
	if !thresholds.AutoDevelopment || thresholds.Validate() != nil {
		t.Fatal(thresholds.AutoDevelopment)
	}
	c, err = parseServe(append(serveBase(dir), "--profile", dir, "--routine-project-limit", "auto", "--routine-project-limit", "3"), io.Discard)
	if err != nil || c.routineProjectLimit != 3 || c.routineProjectAuto {
		t.Fatal(c, err)
	}
}
