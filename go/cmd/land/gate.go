package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

// gatedRoots are the repo-relative roots whose change the lane will not
// land without acceptance results: the native mod's build inputs (the
// shared harness inputs cmd/affected treats as affecting every case) and
// the building runtime, whose plans only a game run proves.
func gatedRoots() []string {
	return append(na.HarnessInputRoots(), "go/internal/buildingruntime")
}

// acceptanceGate is the landing-time check of case results (#308, #273).
type acceptanceGate struct {
	// Results is an `acceptance suite` output directory whose result.json
	// the landing presents; empty presents none.
	Results string
	// Unverified lands a gated diff without results; the caller files the
	// issue naming what is unverified.
	Unverified bool
}

// check refuses the landing when the presented suite did not pass from
// scratch, or when none is presented for a diff under a gated root.
func (g acceptanceGate) check(changed []string) error {
	if g.Results != "" {
		summary, err := readSuiteResults(g.Results)
		if err != nil {
			return err
		}
		fmt.Println("acceptance:", summary)
		return nil
	}
	touched := gatedFiles(changed)
	if len(touched) == 0 {
		return nil
	}
	if g.Unverified {
		fmt.Printf("acceptance: none presented for %d gated file(s) (%s...); landing -unverified, file the issue\n", len(touched), touched[0])
		return nil
	}
	return fmt.Errorf("the diff touches %s (%d file(s) under %s) and presents no acceptance results;\n"+
		"run `acceptance suite -tier land -root <abs root> -output <fresh dir>` from go/ and land with -results <that dir>,\n"+
		"or -unverified and file an issue naming what went unverified",
		touched[0], len(touched), strings.Join(gatedRoots(), ", "))
}

// gatedFiles lists the changed files under a gated root.
func gatedFiles(changed []string) []string {
	var out []string
	for _, file := range changed {
		file = filepath.ToSlash(file)
		for _, root := range gatedRoots() {
			if file == root || strings.HasPrefix(file, root+"/") {
				out = append(out, file)
				break
			}
		}
	}
	return out
}

// readSuiteResults reads a suite report and returns its one-line summary,
// or the reason it is not a landing pass: it failed, or a row resumed from
// a checkpoint (result.json "resumed_from").
func readSuiteResults(dir string) (string, error) {
	path := dir
	if info, err := os.Stat(dir); err == nil && info.IsDir() {
		path = filepath.Join(dir, "result.json")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("-results: %w", err)
	}
	var report struct {
		Passed bool   `json:"passed"`
		Error  string `json:"error"`
		Tier   string `json:"tier"`
		Cases  []struct {
			Name        string `json:"name"`
			ResumedFrom any    `json:"resumed_from"`
		} `json:"cases"`
	}
	if err := json.Unmarshal(data, &report); err != nil {
		return "", fmt.Errorf("-results: %s is not a suite result.json: %w", path, err)
	}
	if report.Cases == nil {
		return "", fmt.Errorf("-results: %s has no cases: give an `acceptance suite` output directory", path)
	}
	var resumed []string
	for _, row := range report.Cases {
		if row.ResumedFrom != nil {
			resumed = append(resumed, row.Name)
		}
	}
	if len(resumed) > 0 {
		return "", fmt.Errorf("-results: %s resumed from a checkpoint (%s); a landing pass runs from scratch (acceptance suite runs every row -fresh)", path, strings.Join(resumed, ", "))
	}
	if !report.Passed {
		reason := report.Error
		if reason == "" {
			reason = "not passed"
		}
		return "", errors.New("-results: " + path + ": " + reason)
	}
	tier := report.Tier
	if tier == "" {
		tier = "untiered"
	}
	return fmt.Sprintf("%s suite, %d case(s) passed fresh, %s", tier, len(report.Cases), path), nil
}
