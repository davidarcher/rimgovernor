package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

type progressPlan struct {
	Shard        string            `json:"shard"`
	RunID        string            `json:"run_id"`
	Attempt      string            `json:"attempt"`
	HeadSHA      string            `json:"head_sha"`
	TestedCommit string            `json:"tested_commit"`
	Cases        []string          `json:"cases"`
	Outputs      map[string]string `json:"outputs"`
}

func (p progressPlan) externalID() string { return p.RunID + ":" + p.Attempt + ":" + p.Shard }

type progressState struct {
	Status string
	Since  time.Time
	Wall   time.Duration
}

type checkOutput struct {
	Title   string `json:"title"`
	Summary string `json:"summary"`
	Text    string `json:"text,omitempty"`
}

type checkUpdate struct {
	Name       string       `json:"name,omitempty"`
	HeadSHA    string       `json:"head_sha,omitempty"`
	ExternalID string       `json:"external_id,omitempty"`
	Status     string       `json:"status,omitempty"`
	Conclusion string       `json:"conclusion,omitempty"`
	Output     *checkOutput `json:"output,omitempty"`
}

type checkRun struct {
	ID         int64       `json:"id"`
	ExternalID string      `json:"external_id"`
	Status     string      `json:"status"`
	Output     checkOutput `json:"output"`
}

// Rendering uses only trusted names/identity and a fixed vocabulary. No native
// error, argv, log content or path can enter a public check run.
func renderProgress(p progressPlan, states []progressState, complete bool) (string, checkOutput) {
	passed, failed, done := 0, 0, 0
	running := "queued"
	var table strings.Builder
	table.WriteString("| Case | Status | Wall |\n|---|---|---|\n")
	for i, name := range p.Cases {
		s := states[i]
		status, wall := "⏳ queued", ""
		switch s.Status {
		case "running":
			status = "▶ running (since " + s.Since.UTC().Format("15:04Z") + ")"
			running = name
		case "passed":
			status = "✅ passed"
			passed++
			done++
		case "failed":
			status = "❌ failed"
			failed++
			done++
		}
		if s.Status == "passed" || s.Status == "failed" {
			wall = s.Wall.Round(time.Second).String()
		} else if complete {
			status = "❌ missing result"
			failed++
		}
		fmt.Fprintf(&table, "| %s | %s | %s |\n", name, status, wall)
	}
	name := fmt.Sprintf("acceptance-%s · %d/%d · %s", p.Shard, done, len(p.Cases), running)
	if complete {
		name = fmt.Sprintf("acceptance-%s · %d/%d passed", p.Shard, passed, len(p.Cases))
		if failed > 0 {
			name = fmt.Sprintf("acceptance-%s · %d failed", p.Shard, failed)
		}
	}
	return name, checkOutput{Title: name, Summary: "Tested commit: `" + p.TestedCommit + "`. Display only; aggregate owns the verdict.", Text: table.String()}
}

// Terminal states are latched. A partial JSON write is retried next poll, and
// reports are bounded before decoding. Only passed and wall_ms are projected.
func observeProgress(p progressPlan, states []progressState, now time.Time, transition func() error) error {
	for i, name := range p.Cases {
		s := &states[i]
		if s.Status == "passed" || s.Status == "failed" {
			continue
		}
		path := p.Outputs[name]
		if s.Status == "" {
			if info, err := os.Stat(filepath.Dir(path) + ".log"); err == nil && info.Mode().IsRegular() {
				s.Status, s.Since = "running", now
				if err := transition(); err != nil {
					return err
				}
			}
		}
		var report struct {
			Passed *bool `json:"passed"`
			WallMS int64 `json:"wall_ms"`
		}
		if err := readProgressJSON(path, &report); err != nil || report.Passed == nil {
			continue
		}
		s.Status = "failed"
		if *report.Passed {
			s.Status = "passed"
		}
		// Numeric input cannot overflow time.Duration or yield negative walls.
		s.Wall = time.Duration(min(max(report.WallMS, 0), int64((24*time.Hour)/time.Millisecond))) * time.Millisecond
		if err := transition(); err != nil {
			return err
		}
	}
	return nil
}

func readProgressJSON(path string, dst any) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() || info.Size() > 16<<20 {
		return fmt.Errorf("invalid progress file")
	}
	b, err := io.ReadAll(io.LimitReader(f, (16<<20)+1))
	if err != nil {
		return err
	}
	if len(b) > 16<<20 {
		return fmt.Errorf("oversize progress file")
	}
	return json.Unmarshal(b, dst)
}

type progressAPI struct {
	client            *http.Client
	base, repo, token string
}

func (a progressAPI) request(method, path string, body any, dst any) error {
	var data []byte
	var err error
	if body != nil {
		data, err = json.Marshal(body)
		if err != nil {
			return err
		}
	}
	req, err := http.NewRequest(method, a.base+"/repos/"+a.repo+path, bytes.NewReader(data))
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+a.token)
	req.Header.Set("Accept", "application/vnd.github+json")
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-GitHub-Api-Version", "2022-11-28")
	resp, err := a.client.Do(req)
	if err != nil {
		return fmt.Errorf("progress API unavailable")
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("progress API status %d", resp.StatusCode)
	}
	if dst == nil {
		return nil
	}
	return json.NewDecoder(io.LimitReader(resp.Body, 8<<20)).Decode(dst)
}

func (a progressAPI) watch(p progressPlan, idFile, stopFile string, ticks <-chan time.Time) error {
	states := make([]progressState, len(p.Cases))
	name, output := renderProgress(p, states, false)
	var created checkRun
	if err := a.request("POST", "/check-runs", checkUpdate{Name: name, HeadSHA: p.HeadSHA, ExternalID: p.externalID(), Status: "in_progress", Output: &output}, &created); err != nil {
		return err
	}
	if created.ID <= 0 {
		return fmt.Errorf("missing check id")
	}
	if err := os.WriteFile(idFile, []byte(strconv.FormatInt(created.ID, 10)), 0600); err != nil {
		return err
	}
	path := fmt.Sprintf("/check-runs/%d", created.ID)
	update := func() error {
		name, output := renderProgress(p, states, false)
		return a.request("PATCH", path, checkUpdate{Name: name, Output: &output}, nil)
	}
	for {
		if err := observeProgress(p, states, time.Now(), update); err != nil {
			return err
		}
		if _, err := os.Stat(stopFile); err == nil {
			// The suite may have written its last result during the first scan.
			if err := observeProgress(p, states, time.Now(), update); err != nil {
				return err
			}
			name, output := renderProgress(p, states, true)
			conclusion := "success"
			for _, s := range states {
				if s.Status != "passed" {
					conclusion = "failure"
				}
			}
			return a.request("PATCH", path, checkUpdate{Name: name, Output: &output, Status: "completed", Conclusion: conclusion}, nil)
		}
		if _, ok := <-ticks; !ok {
			return fmt.Errorf("progress watch interrupted")
		}
	}
}

func (a progressAPI) finish(idFile, externalID, artifact string) error {
	if artifact == "" {
		return nil
	}
	u, err := url.Parse(artifact)
	if err != nil || u.Scheme != "https" || u.Host != "github.com" || !strings.HasPrefix(u.Path, "/"+a.repo+"/actions/runs/") || strings.ContainsAny(artifact, "\r\n()<> ") {
		return fmt.Errorf("invalid artifact URL")
	}
	b, err := os.ReadFile(idFile)
	if err != nil {
		return err
	}
	id, err := strconv.ParseInt(strings.TrimSpace(string(b)), 10, 64)
	if err != nil || id <= 0 {
		return fmt.Errorf("invalid check id")
	}
	path := fmt.Sprintf("/check-runs/%d", id)
	var check checkRun
	if err := a.request("GET", path, nil, &check); err != nil {
		return err
	}
	if check.ExternalID != externalID {
		return fmt.Errorf("wrong check identity")
	}
	check.Output.Summary += "\n\n[Shard diagnostics](" + artifact + ")"
	return a.request("PATCH", path, checkUpdate{Output: &check.Output}, nil)
}

func (a progressAPI) sweep(head, prefix string) error {
	for page := 1; ; page++ {
		var result struct {
			Checks []checkRun `json:"check_runs"`
		}
		path := fmt.Sprintf("/commits/%s/check-runs?filter=all&per_page=100&page=%d", head, page)
		if err := a.request("GET", path, nil, &result); err != nil {
			return err
		}
		for _, check := range result.Checks {
			if check.Status != "in_progress" || !strings.HasPrefix(check.ExternalID, prefix) || !regexp.MustCompile(`^s[1-9][0-9]*$`).MatchString(strings.TrimPrefix(check.ExternalID, prefix)) {
				continue
			}
			check.Output.Summary += "\n\nShard ended without completing live progress (cancelled, timed out or observer unavailable)."
			if err := a.request("PATCH", fmt.Sprintf("/check-runs/%d", check.ID), checkUpdate{Status: "completed", Conclusion: "cancelled", Output: &check.Output}, nil); err != nil {
				return err
			}
		}
		if len(result.Checks) < 100 {
			return nil
		}
	}
}

// prepareProgress runs from the trusted checkout before tested binaries start.
// The registry, not directory enumeration or tested output, owns the mapping.
func prepareProgress(root, shard, outputRoot string) (progressPlan, error) {
	var selection struct {
		Cases []struct {
			Name string `json:"name"`
			Role string `json:"mod_role"`
		} `json:"cases"`
		Shards []struct {
			ID    string   `json:"id"`
			Cases []string `json:"cases"`
		} `json:"shards"`
	}
	var run struct {
		TestedCommit string `json:"tested_commit"`
	}
	p := progressPlan{Shard: shard, RunID: os.Getenv("GITHUB_RUN_ID"), Attempt: os.Getenv("GITHUB_RUN_ATTEMPT"), HeadSHA: os.Getenv("GITHUB_SHA"), Outputs: map[string]string{}}
	if err := readProgressJSON(filepath.Join(root, "selection.json"), &selection); err != nil {
		return p, err
	}
	if err := readProgressJSON(filepath.Join(root, "run.json"), &run); err != nil {
		return p, err
	}
	p.TestedCommit = run.TestedCommit
	selected := map[string]bool{}
	for _, s := range selection.Shards {
		if s.ID == shard {
			for _, n := range s.Cases {
				selected[n] = true
			}
		}
	}
	for _, role := range []string{"fixture", "production"} {
		for _, row := range selection.Cases {
			if !selected[row.Name] || row.Role != role {
				continue
			}
			registered, ok := cases.Lookup(row.Name)
			if !ok {
				return p, fmt.Errorf("case unavailable in trusted registry")
			}
			if _, exists := p.Outputs[row.Name]; exists {
				return p, fmt.Errorf("duplicate progress case")
			}
			p.Cases = append(p.Cases, row.Name)
			dir := (cases.Options{Output: filepath.Join(outputRoot, role, "job", "out")}).CaseOutput(registered)
			p.Outputs[row.Name] = filepath.Join(dir, "result.json")
		}
	}
	if len(p.Cases) != len(selected) {
		return p, fmt.Errorf("incomplete progress projection")
	}
	return p, validateProgress(p)
}

func validateProgress(p progressPlan) error {
	if !regexp.MustCompile(`^s[1-9][0-9]*$`).MatchString(p.Shard) || !regexp.MustCompile(`^[1-9][0-9]*$`).MatchString(p.RunID) || !regexp.MustCompile(`^[1-9][0-9]*$`).MatchString(p.Attempt) || !regexp.MustCompile(`^[0-9a-f]{40}$`).MatchString(p.HeadSHA) || !regexp.MustCompile(`^[0-9a-f]{40}$`).MatchString(p.TestedCommit) || len(p.Cases) == 0 {
		return fmt.Errorf("invalid progress identity")
	}
	seen := map[string]bool{}
	for _, name := range p.Cases {
		if _, ok := cases.Lookup(name); !ok || seen[name] || !filepath.IsAbs(p.Outputs[name]) {
			return fmt.Errorf("invalid progress case")
		}
		seen[name] = true
	}
	return nil
}

func progressCommand(args []string) error {
	f := flag.NewFlagSet("progress", flag.ContinueOnError)
	planFile := f.String("plan", `C:\rg\progress.json`, "trusted progress plan")
	idFile := f.String("id-file", `C:\rg\progress-id.txt`, "check run id")
	stopFile := f.String("stop-file", `C:\rg\progress.stop`, "watch stop signal")
	root := f.String("root", `C:\rg\evidence`, "trusted evidence root")
	outputRoot := f.String("output-root", `C:\rg`, "private role layouts")
	shard := f.String("shard", os.Getenv("SHARD"), "shard id")
	prepare := f.Bool("prepare", false, "write trusted registry output map")
	watch := f.Bool("watch", false, "observe planned case files")
	finish := f.Bool("finish", false, "add artifact link")
	sweep := f.Bool("sweep", false, "cancel abandoned checks for this attempt")
	artifact := f.String("artifact-url", "", "uploaded shard diagnostics")
	if err := f.Parse(args); err != nil {
		return err
	}
	modes := 0
	for _, m := range []bool{*prepare, *watch, *finish, *sweep} {
		if m {
			modes++
		}
	}
	if modes != 1 || f.NArg() != 0 {
		return fmt.Errorf("choose one progress mode")
	}
	if *prepare {
		p, err := prepareProgress(*root, *shard, *outputRoot)
		if err != nil {
			return err
		}
		b, err := json.Marshal(p)
		if err != nil {
			return err
		}
		return os.WriteFile(*planFile, b, 0600)
	}
	// Credentials are accepted only through stdin, including finish and sweep.
	b, err := io.ReadAll(io.LimitReader(os.Stdin, 16385))
	if err != nil || len(b) > 16384 || strings.TrimSpace(string(b)) == "" {
		return fmt.Errorf("missing progress credential")
	}
	repo := os.Getenv("GITHUB_REPOSITORY")
	if !regexp.MustCompile(`^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`).MatchString(repo) {
		return fmt.Errorf("invalid repository")
	}
	a := progressAPI{client: &http.Client{Timeout: 5 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}, base: "https://api.github.com", repo: repo, token: strings.TrimSpace(string(b))}
	if *sweep {
		head, run, attempt := os.Getenv("GITHUB_SHA"), os.Getenv("GITHUB_RUN_ID"), os.Getenv("GITHUB_RUN_ATTEMPT")
		if !regexp.MustCompile(`^[0-9a-f]{40}$`).MatchString(head) || !regexp.MustCompile(`^[1-9][0-9]*$`).MatchString(run) || !regexp.MustCompile(`^[1-9][0-9]*$`).MatchString(attempt) {
			return fmt.Errorf("invalid sweep identity")
		}
		return a.sweep(head, run+":"+attempt+":")
	}
	var p progressPlan
	if err := readProgressJSON(*planFile, &p); err != nil {
		return err
	}
	if err := validateProgress(p); err != nil {
		return err
	}
	if p.HeadSHA != os.Getenv("GITHUB_SHA") || p.RunID != os.Getenv("GITHUB_RUN_ID") || p.Attempt != os.Getenv("GITHUB_RUN_ATTEMPT") {
		return fmt.Errorf("wrong progress invocation")
	}
	if *finish {
		return a.finish(*idFile, p.externalID(), *artifact)
	}
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	return a.watch(p, *idFile, *stopFile, ticker.C)
}
