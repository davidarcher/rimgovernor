package remoteaccept

import (
	"encoding/json"
	"fmt"
	"reflect"
	"sort"
	"strings"
	"time"
)

// Evaluation contains a recomputed verdict; a supplied aggregate's pass bit is
// never evidence. Files is the digest-verified closure, useful to upload tooling.
type Evaluation struct {
	Aggregate Aggregate
	Report    Report
	Run       Run
	Selection Selection
	Files     []string
}

func Evaluate(root string, runRef, selectionRef Ref, shards []Shard) (Evaluation, error) {
	e := Evaluation{Aggregate: Aggregate{Version: 1, Run: runRef, Selection: selectionRef, Shards: shards, Status: "passed", Passed: true, Cases: []Case{}}, Report: Report{Cases: []json.RawMessage{}}}
	t, err := openTree(root)
	if err != nil {
		return e, err
	}
	if err = t.read(runRef, &e.Run); err != nil {
		return e, err
	}
	r := e.Run
	if r.Version != 1 || !repository.MatchString(r.Repository) || !oid.MatchString(r.TestedCommit) || !oid.MatchString(r.BaseCommit) || !oid.MatchString(r.WorkflowCommit) || (r.Tier != "land" && r.Tier != "smoke" && r.Tier != "full") || (r.Tier == "land" && r.TestedCommit == r.BaseCommit) {
		return e, fmt.Errorf("invalid run identity/version/tier")
	}
	if r.Trigger.RunID <= 0 || r.Trigger.Attempt < 1 || r.Trigger.Actor == "" || !strings.HasPrefix(r.Trigger.PublishedRef, "refs/") || (r.Trigger.Event != "push" && r.Trigger.Event != "workflow_dispatch" && r.Trigger.Event != "schedule") || r.ID != fmt.Sprintf("gh:%s:%d:%d", r.Repository, r.Trigger.RunID, r.Trigger.Attempt) {
		return e, fmt.Errorf("invalid trigger/run ID")
	}
	l := r.Limits
	if l.Shards < 1 || l.Shards > 32 || l.Parallel < 1 || l.Parallel > 4 || l.Workers != 1 || l.Attempts < 1 || l.Attempts > 2 || l.Bytes < 1 || l.Bytes > 1<<30 || l.Paid || l.Runner != "windows-2022" || l.JobMinutes < 1 || l.JobMinutes > 60 || l.SuiteMinutes < 1 || l.SuiteMinutes > 45 || l.Retention < 1 || l.Retention > 7 {
		return e, fmt.Errorf("invalid run limits")
	}
	var bundle struct {
		Version int `json:"schema_version"`
	}
	if err = t.read(r.Bundle, &bundle); err != nil {
		return e, err
	}
	if bundle.Version != 1 {
		return e, fmt.Errorf("unknown bundle version")
	}
	if err = t.read(selectionRef, &e.Selection); err != nil {
		return e, err
	}
	s := e.Selection
	if s.Version != 1 || s.Run != runRef || s.Planner != r.TestedCommit || s.DiffMode != "ancestor-tree" || s.Algorithm != "sorted-round-robin-v1" {
		return e, fmt.Errorf("selection identity/version/algorithm mismatch")
	}
	if len(s.Cases) == 0 || len(s.Shards) == 0 || len(s.Shards) > l.Shards {
		return e, fmt.Errorf("empty or oversized selection")
	}
	if !sort.StringsAreSorted(s.Changed) {
		return e, fmt.Errorf("changed files not sorted")
	}
	for i, p := range s.Changed {
		if !validPath(p) || (i > 0 && p == s.Changed[i-1]) {
			return e, fmt.Errorf("invalid changed files")
		}
	}
	planned := map[string]string{}
	names := []string{}
	for _, c := range s.Cases {
		if !validPath(c.Name) || strings.Count(c.Name, "/") != 1 || len(c.Reasons) == 0 {
			return e, fmt.Errorf("invalid selected case %s", c.Name)
		}
		if len(names) > 0 && names[len(names)-1] >= c.Name {
			return e, fmt.Errorf("duplicate/unsorted selected case %s", c.Name)
		}
		for _, reason := range c.Reasons {
			area, _, _ := strings.Cut(c.Name, "/")
			if reason != "smoke" && !(reason == "full" && r.Tier == "full") && reason != "affected:"+area && reason != "sampled:"+area {
				return e, fmt.Errorf("invalid reason %s", reason)
			}
		}
		names = append(names, c.Name)
	}
	n := min(len(names), l.Shards)
	if len(s.Shards) != n {
		return e, fmt.Errorf("shard count does not match algorithm")
	}
	for i, sh := range s.Shards {
		if sh.ID != fmt.Sprintf("s%d", i+1) {
			return e, fmt.Errorf("invalid/duplicate planned shard %s", sh.ID)
		}
		want := []string{}
		for j := i; j < len(names); j += n {
			want = append(want, names[j])
			planned[names[j]] = sh.ID
		}
		if !reflect.DeepEqual(sh.Cases, want) {
			return e, fmt.Errorf("shard %s does not match selection", sh.ID)
		}
	}
	actual := map[string]Shard{}
	for _, sh := range shards {
		if _, ok := actual[sh.ID]; ok {
			return e, fmt.Errorf("duplicate shard %s", sh.ID)
		}
		actual[sh.ID] = sh
		found := false
		for _, p := range s.Shards {
			if p.ID == sh.ID {
				found = true
			}
		}
		if !found {
			return e, fmt.Errorf("unknown shard %s", sh.ID)
		}
		if sh.Status != "complete" && sh.Status != "missing" && sh.Status != "cancelled" && sh.Status != "timed_out" {
			return e, fmt.Errorf("unknown shard status %s", sh.Status)
		}
	}
	rows := map[string]json.RawMessage{}
	summary := map[string]Case{}
	for _, sh := range s.Shards {
		got, ok := actual[sh.ID]
		if !ok {
			got = Shard{ID: sh.ID, Status: "missing"}
			e.Aggregate.Shards = append(e.Aggregate.Shards, got)
		}
		if got.Status != "complete" || got.Attempts == nil {
			e.problem("incomplete", "shard "+sh.ID+" is "+got.Status+" or lacks attempts")
		}
		for _, name := range sh.Cases {
			summary[name] = Case{Name: name, ShardID: sh.ID, Status: "incomplete"}
		}
		if got.Attempts == nil {
			continue
		}
		var a Attempts
		if err = t.read(*got.Attempts, &a); err != nil {
			return e, err
		}
		if a.Version != 1 || a.Run != runRef || a.Selection != selectionRef || a.ShardID != sh.ID {
			return e, fmt.Errorf("shard %s identity mismatch", sh.ID)
		}
		if a.Runner.OS != "windows" || a.Runner.Arch != "x64" || a.Runner.Image == "" || a.Runner.CPUs <= 0 || a.Runner.Memory <= 0 || a.Runner.Disk <= 0 {
			return e, fmt.Errorf("invalid runner %s", sh.ID)
		}
		var bootstrap struct {
			Passed bool `json:"passed"`
		}
		if err = t.read(a.Runner.Bootstrap, &bootstrap); err != nil {
			return e, err
		}
		if !bootstrap.Passed {
			e.problem("incomplete", "bootstrap failed: "+sh.ID)
		}
		previous := map[string]Attempt{}
		for _, attempt := range a.Attempts {
			if planned[attempt.Case] != sh.ID {
				return e, fmt.Errorf("extra or wrong-shard case %s", attempt.Case)
			}
			prev, hasPrev := previous[attempt.Case]
			if err = validateAttempt(attempt, prev, hasPrev, l.Attempts); err != nil {
				return e, err
			}
			if len(attempt.Log) > 0 {
				var log Ref
				if err = Decode(attempt.Log, &log); err != nil {
					return e, fmt.Errorf("missing/malformed attempt log: %w", err)
				}
				if err = t.read(log, nil); err != nil {
					return e, err
				}
			}
			if hasPrev && prev.Evidence.Path == attempt.Evidence.Path {
				return e, fmt.Errorf("retry overwrites prior evidence %s", attempt.Evidence.Path)
			}
			var native map[string]json.RawMessage
			if err = t.read(attempt.Evidence, &native); err != nil {
				return e, err
			}
			if _, ok := native["cases"]; ok {
				native, err = suiteRow(native, attempt.Case, sh.Cases)
				if err != nil {
					return e, err
				}
			}
			var fields struct {
				Name       string `json:"name,omitempty"`
				Case       string `json:"case,omitempty"`
				Passed     bool   `json:"passed"`
				Exit       *int   `json:"exit,omitempty"`
				Postmortem bool   `json:"postmortem_only,omitempty"`
				Fixture    bool   `json:"fixture_only,omitempty"`
				Files      []Ref  `json:"evidence_files,omitempty"`
			}
			b, _ := json.Marshal(native)
			if err = Decode(b, &fields); err != nil {
				return e, err
			}
			if (fields.Name == "" && fields.Case == "") || (fields.Name != "" && fields.Name != attempt.Case) || (fields.Case != "" && fields.Case != attempt.Case) {
				return e, fmt.Errorf("native case identity mismatch: %s", attempt.Case)
			}
			for _, ref := range fields.Files {
				if err = t.read(ref, nil); err != nil {
					return e, err
				}
			}
			for _, key := range []string{"log", "output", "flight_recorder"} {
				if raw, ok := native[key]; ok {
					var p string
					if err = json.Unmarshal(raw, &p); err != nil {
						return e, err
					}
					if !validPath(p) || t.files[strings.ToLower(p)] != p {
						return e, fmt.Errorf("missing/unsafe %s evidence: %s", key, p)
					}
				}
			}
			passed := attempt.Status == "passed"
			if passed != fields.Passed || (fields.Exit != nil && attempt.Exit != nil && *fields.Exit != *attempt.Exit) {
				return e, fmt.Errorf("native verdict/exit disagrees for %s", attempt.Case)
			}
			status := "failed"
			if passed {
				status = "passed"
			}
			if fields.Postmortem || present(native["staged_from"]) || present(native["resumed_from"]) {
				status = "failed"
				e.problem("failed", "nonfresh/staged/postmortem evidence: "+attempt.Case)
			}
			// Retain each native report untouched. The projected row adds only
			// links to the original report and its complete attempt history.
			native["name"], _ = json.Marshal(attempt.Case)
			native["exit"], _ = json.Marshal(attempt.Exit)
			native["remote_evidence"], _ = json.Marshal(attempt.Evidence)
			native["remote_attempts"], _ = json.Marshal(got.Attempts)
			rows[attempt.Case], _ = json.Marshal(native)
			num := attempt.Number
			summary[attempt.Case] = Case{Name: attempt.Case, ShardID: sh.ID, AttemptCount: num, FinalAttempt: &num, Status: status}
			previous[attempt.Case] = attempt
		}
	}
	for _, name := range names {
		c := summary[name]
		e.Aggregate.Cases = append(e.Aggregate.Cases, c)
		if c.Status != "passed" {
			e.problem(c.Status, name+": "+c.Status)
		}
		row, ok := rows[name]
		if !ok {
			row, _ = json.Marshal(struct {
				Name   string `json:"name"`
				Passed bool   `json:"passed"`
			}{Name: name})
		}
		e.Report.Cases = append(e.Report.Cases, row)
	}
	e.Report.Tier = r.Tier
	e.Report.Passed = e.Aggregate.Passed
	e.Report.Error = e.Aggregate.Error
	e.Report.Remote = Remote{Version: 1, TestedCommit: r.TestedCommit, BaseCommit: r.BaseCommit, BundleSHA256: r.Bundle.SHA256}
	for p := range t.used {
		e.Files = append(e.Files, p)
	}
	sort.Strings(e.Files)
	return e, nil
}
func present(v json.RawMessage) bool { return len(v) > 0 && string(v) != "null" }
func suiteRow(suite map[string]json.RawMessage, name string, allowed []string) (map[string]json.RawMessage, error) {
	var envelope struct {
		Passed bool              `json:"passed"`
		Cases  []json.RawMessage `json:"cases"`
	}
	b, _ := json.Marshal(suite)
	if err := Decode(b, &envelope); err != nil {
		return nil, err
	}
	seen := map[string]bool{}
	var found map[string]json.RawMessage
	for _, b := range envelope.Cases {
		var row struct {
			Name   string `json:"name"`
			Passed bool   `json:"passed"`
			Exit   int    `json:"exit"`
		}
		if err := Decode(b, &row); err != nil {
			return nil, err
		}
		ok := false
		for _, n := range allowed {
			if n == row.Name {
				ok = true
			}
		}
		if !ok || seen[row.Name] {
			return nil, fmt.Errorf("extra/duplicate suite case %s", row.Name)
		}
		seen[row.Name] = true
		if envelope.Passed && (!row.Passed || row.Exit != 0) {
			return nil, fmt.Errorf("suite hides failed row %s", row.Name)
		}
		if row.Name == name {
			if err := json.Unmarshal(b, &found); err != nil {
				return nil, err
			}
		}
	}
	if found == nil {
		return nil, fmt.Errorf("suite omits %s", name)
	}
	return found, nil
}
func (e *Evaluation) problem(status, msg string) {
	e.Aggregate.Passed = false
	if e.Aggregate.Status != "incomplete" {
		e.Aggregate.Status = status
	}
	if e.Aggregate.Error == nil {
		e.Aggregate.Error = &msg
	} else {
		s := *e.Aggregate.Error + "; " + msg
		e.Aggregate.Error = &s
	}
}
func validateAttempt(a, prev Attempt, hasPrev bool, maxAttempts int) error {
	if a.Number < 1 || a.Number > maxAttempts || a.Number != prev.Number+1 {
		return fmt.Errorf("duplicate/gapped attempt for %s", a.Case)
	}
	start, e1 := time.Parse(time.RFC3339, a.Started)
	end, e2 := time.Parse(time.RFC3339, a.Finished)
	if e1 != nil || e2 != nil || end.Before(start) || !strings.HasSuffix(a.Started, "Z") || !strings.HasSuffix(a.Finished, "Z") {
		return fmt.Errorf("invalid attempt timestamps")
	}
	if hasPrev {
		priorEnd, _ := time.Parse(time.RFC3339, prev.Finished)
		if a.RetryOf == nil || *a.RetryOf != prev.Number || prev.Status != "failed" || prev.Classification != "infrastructure" || start.Before(priorEnd) {
			return fmt.Errorf("ineligible retry: %s", a.Case)
		}
	} else if a.RetryOf != nil {
		return fmt.Errorf("first attempt has retry_of")
	}
	switch a.Status {
	case "passed":
		if a.Exit == nil || *a.Exit != 0 || a.Classification != "none" || a.Error != nil {
			return fmt.Errorf("invalid successful attempt")
		}
	case "failed":
		if a.Exit == nil || *a.Exit == 0 || a.Error == nil || *a.Error == "" || (a.Classification != "infrastructure" && a.Classification != "assertion" && a.Classification != "unknown") {
			return fmt.Errorf("invalid failed attempt")
		}
	case "cancelled":
		if a.Classification != "cancelled" {
			return fmt.Errorf("invalid cancellation")
		}
	case "timed_out":
		if a.Classification != "timeout" {
			return fmt.Errorf("invalid timeout")
		}
	default:
		return fmt.Errorf("unknown attempt status")
	}
	return nil
}

// Verify recomputes the aggregate and compares every verdict/coverage field.
func Verify(root string, ref Ref) (Evaluation, error) {
	t, err := openTree(root)
	if err != nil {
		return Evaluation{}, err
	}
	var supplied Aggregate
	if err = t.read(ref, &supplied); err != nil {
		return Evaluation{}, err
	}
	e, err := Evaluate(root, supplied.Run, supplied.Selection, supplied.Shards)
	if err != nil {
		return e, err
	}
	if !reflect.DeepEqual(supplied, e.Aggregate) {
		return e, fmt.Errorf("aggregate disagrees with verified shard evidence")
	}
	e.Report.Remote.Aggregate = ref
	return e, nil
}
