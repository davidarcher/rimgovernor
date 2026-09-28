package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/remoteaccept"
)

// acceptanceGate is the landing-time check of optional case results (#308).
// No diff requires results; the nightly tier proves native behaviour.
type acceptanceGate struct {
	// Results is an `acceptance suite` output directory whose result.json
	// the landing presents; empty presents none.
	Results        string
	remoteVerified bool
	remoteReport   *remoteaccept.Report
}

// prepare binds remote evidence to the branch before the lane's normal merge.
// That clean merge does not invalidate the association or trigger another run.
func (g *acceptanceGate) prepare(repo string) error {
	root, remote, err := remoteResults(g.Results)
	if err != nil || !remote {
		return err
	}
	trust, err := remoteaccept.LocalTrust(repo)
	if err != nil {
		return err
	}
	e, err := remoteaccept.VerifyImported(root, repo, trust, remoteaccept.GitHub{})
	if err != nil {
		return fmt.Errorf("-results: %w", err)
	}
	g.remoteVerified = true
	g.remoteReport = &e.Report
	return nil
}

func remoteResults(results string) (string, bool, error) {
	if results == "" {
		return "", false, nil
	}
	p := results
	if info, err := os.Stat(p); err == nil && info.IsDir() {
		p = filepath.Join(p, "result.json")
	}
	b, err := os.ReadFile(p)
	if err != nil {
		return "", false, err
	}
	var fields map[string]json.RawMessage
	if err := remoteaccept.Decode(b, &fields); err != nil {
		return "", false, err
	}
	remote := false
	for key := range fields {
		if strings.EqualFold(key, "remote") {
			remote = true
		}
	}
	return filepath.Dir(p), remote, nil
}

// check refuses the landing when a presented suite did not pass; resumed
// rows are named, not refused.
func (g acceptanceGate) check() error {
	if g.Results != "" {
		root, remote, err := remoteResults(g.Results)
		if err != nil {
			return err
		}
		if remote && !g.remoteVerified {
			return fmt.Errorf("-results: remote evidence has not been authenticated against the pre-merge task source")
		}
		if remote {
			b, err := os.ReadFile(filepath.Join(root, "result.json"))
			if err != nil {
				return err
			}
			var current remoteaccept.Report
			if err := remoteaccept.Decode(b, &current); err != nil {
				return err
			}
			if g.remoteReport == nil || !remoteaccept.ReportsEqual(current, *g.remoteReport) {
				return fmt.Errorf("-results: remote report changed after pre-merge verification")
			}
		}
		summary, err := readSuiteResults(g.Results)
		if err != nil {
			return err
		}
		fmt.Println("acceptance:", summary)
		return nil
	}
	return nil
}

// readSuiteResults reads a suite report and returns its one-line summary,
// or the reason it is not a landing pass: it failed. Rows that resumed from
// a checkpoint (result.json "resumed_from", `acceptance suite -resume`)
// pass, and the summary names them: everything before the resume point
// ran under the earlier revision, so the landing records the fact rather
// than paying a fresh run to erase it (#249, #308). A row that ran
// postmortem-only ("postmortem_only", #275) ran no scenario at all and is
// refused.
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
			Name           string `json:"name"`
			ResumedFrom    any    `json:"resumed_from"`
			StagedFrom     any    `json:"staged_from"`
			PostmortemOnly bool   `json:"postmortem_only"`
		} `json:"cases"`
	}
	if err := json.Unmarshal(data, &report); err != nil {
		return "", fmt.Errorf("-results: %s is not a suite result.json: %w", path, err)
	}
	if report.Cases == nil {
		return "", fmt.Errorf("-results: %s has no cases: give an `acceptance suite` output directory", path)
	}
	var only, staged []string
	for _, row := range report.Cases {
		if row.PostmortemOnly {
			only = append(only, row.Name)
		}
		if row.StagedFrom != nil {
			staged = append(staged, row.Name)
		}
	}
	if len(only) > 0 {
		return "", fmt.Errorf("-results: %s ran postmortem-only (%s); a landing pass runs the scenario", path, strings.Join(only, ", "))
	}
	if len(staged) > 0 {
		return "", fmt.Errorf("-results: %s opened on a cached stage bundle (%s); a landing pass stages from scratch (acceptance suite runs every row -restage unless -stages)", path, strings.Join(staged, ", "))
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
	var resumed []string
	for _, row := range report.Cases {
		if row.ResumedFrom != nil {
			resumed = append(resumed, row.Name)
		}
	}
	if len(resumed) > 0 {
		return fmt.Sprintf("%s suite, %d case(s) passed, %d resumed from a checkpoint (%s: passed past the resume point only), %s", tier, len(report.Cases), len(resumed), strings.Join(resumed, ", "), path), nil
	}
	return fmt.Sprintf("%s suite, %d case(s) passed fresh, %s", tier, len(report.Cases), path), nil
}
