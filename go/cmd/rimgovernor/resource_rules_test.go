package main

import (
	"io"
	"path/filepath"
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

func TestServeResourceRulesRequirePlayerControl(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--resource-rule", "WoodLog:stop:10"}
	if _, err := parseServe(append(base, "--read-only"), io.Discard); err == nil {
		t.Fatal("read-only spending configuration accepted")
	}
	config, err := parseServe(append(base, "--player-control", "--profile", dir), io.Discard)
	if err != nil || len(config.resourceRules) != 1 || config.resourceRules[0].Reserve != 10 {
		t.Fatal(config, err)
	}
}
