package main

// acceptance profile-compare: the nightly snapshot-capture perf
// check. snapshot-perf.yml times profile-capture on the fixture factory's
// newest bundle; this command records the stats with their commit as the
// run's artifact, compares each family's p90 to the median p90 of the last
// -window successful runs' records, and opens or comments on one
// regression issue naming the families and the commit range.

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

const (
	perfWorkflow   = "snapshot-perf.yml"
	perfArtifact   = "snapshot-perf-"
	perfIssueTitle = "Snapshot capture regression (nightly snapshot-perf)"
	perfMinSamples = 3
)

const profileCompareUsage = `
  acceptance profile-compare -current <profile.json> -commit <sha> -out <record.json> [-history <dir> -fetch -window <n> -threshold <f> -min-ms <ms> -summary <file> -issue]
    records profile-capture -json stats with their commit and compares each family's p90 to the median of the last -window records
    (-fetch downloads them from successful snapshot-perf runs); -issue opens or comments on the regression issue`

// perfRecord is one nightly run's artifact.
type perfRecord struct {
	Commit  string         `json:"commit"`
	RunID   int64          `json:"run_id,omitempty"`
	At      time.Time      `json:"at"`
	Profile profileSummary `json:"profile"`
}

// perfRegression is one span whose p90 exceeds its baseline.
type perfRegression struct {
	Name     string  `json:"name"`
	P90      float64 `json:"p90"`
	Baseline float64 `json:"baseline"`
	Samples  int     `json:"samples"`
}

func (r perfRegression) ratio() float64 { return r.P90/r.Baseline - 1 }

// comparePerf flags the spans (total and families) of current whose p90
// exceeds the median p90 of history by more than threshold and minMs.
// A span with fewer than perfMinSamples history samples has no baseline.
func comparePerf(current profileSummary, history []perfRecord, threshold, minMs float64) []perfRegression {
	samples := map[string][]float64{}
	for _, h := range history {
		samples["total"] = append(samples["total"], h.Profile.Total.P90)
		for _, f := range h.Profile.Families {
			samples[f.Name] = append(samples[f.Name], f.P90)
		}
	}
	var out []perfRegression
	for _, st := range append([]profileStat{current.Total}, current.Families...) {
		values := samples[st.Name]
		if len(values) < perfMinSamples {
			continue
		}
		base := median(values)
		if st.P90 > base*(1+threshold) && st.P90-base > minMs {
			out = append(out, perfRegression{Name: st.Name, P90: st.P90, Baseline: base, Samples: len(values)})
		}
	}
	return out
}

func median(values []float64) float64 {
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)
	n := len(sorted)
	if n%2 == 1 {
		return sorted[n/2]
	}
	return (sorted[n/2-1] + sorted[n/2]) / 2
}

// readPerfHistory reads every record under dir, newest first, at most
// window of them.
func readPerfHistory(dir string, window int) ([]perfRecord, error) {
	var records []perfRecord
	err := filepath.WalkDir(dir, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() || filepath.Base(path) != "record.json" {
			return err
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		var r perfRecord
		if err := json.Unmarshal(data, &r); err != nil {
			return fmt.Errorf("%s: %w", path, err)
		}
		records = append(records, r)
		return nil
	})
	if errors.Is(err, os.ErrNotExist) {
		err = nil
	}
	sort.SliceStable(records, func(i, j int) bool { return records[i].At.After(records[j].At) })
	if len(records) > window {
		records = records[:window]
	}
	return records, err
}

// fetchPerfHistory downloads the last window successful snapshot-perf
// runs' records into dir/<run id>.
func fetchPerfHistory(dir string, window int) error {
	out, err := ghRunner("run", "list", "-R", factoryRepo, "--workflow", perfWorkflow, "--branch", "main",
		"--status", "success", "--limit", strconv.Itoa(window), "--json", "databaseId")
	if err != nil {
		return fmt.Errorf("gh run list %s: %w", perfWorkflow, err)
	}
	var runs []factoryRun
	if err := json.Unmarshal(out, &runs); err != nil {
		return err
	}
	for _, run := range runs {
		id := strconv.FormatInt(run.ID, 10)
		if _, err := ghRunner("run", "download", id, "-R", factoryRepo, "--pattern", perfArtifact+"*", "--dir", filepath.Join(dir, id)); err != nil {
			return fmt.Errorf("gh run download %s: %w", id, err)
		}
	}
	return nil
}

// perfReport is the markdown body for the summary and the issue.
func perfReport(current perfRecord, history []perfRecord, regressions []perfRegression, threshold float64) string {
	var b strings.Builder
	fmt.Fprintf(&b, "Snapshot capture at %s", short(current.Commit))
	if current.RunID != 0 {
		fmt.Fprintf(&b, " ([run](https://github.com/%s/actions/runs/%d))", factoryRepo, current.RunID)
	}
	fmt.Fprintf(&b, ": %d captures, %d pawns, %d things; baseline is the median p90 of %d run(s), threshold +%.0f%%.\n\n",
		current.Profile.Count, current.Profile.Pawns, current.Profile.Things, len(history), threshold*100)
	if len(regressions) == 0 {
		b.WriteString("No family regressed.\n")
		return b.String()
	}
	if len(history) > 0 {
		from := history[0].Commit
		fmt.Fprintf(&b, "Commit range: [%s..%s](https://github.com/%s/compare/%s...%s)\n\n", short(from), short(current.Commit), factoryRepo, from, current.Commit)
	}
	b.WriteString("| span | p90 ms | baseline ms | change |\n|---|---:|---:|---:|\n")
	for _, r := range regressions {
		fmt.Fprintf(&b, "| %s | %.2f | %.2f | +%.0f%% |\n", r.Name, r.P90, r.Baseline, r.ratio()*100)
	}
	return b.String()
}

func short(sha string) string {
	if len(sha) > 10 {
		return sha[:10]
	}
	return sha
}

// filePerfIssue comments on the open regression issue, or opens one.
func filePerfIssue(body string) error {
	out, err := ghRunner("issue", "list", "-R", factoryRepo, "--state", "open", "--search", "in:title \""+perfIssueTitle+"\"", "--json", "number,title")
	if err != nil {
		return fmt.Errorf("gh issue list: %w", err)
	}
	var issues []struct {
		Number int    `json:"number"`
		Title  string `json:"title"`
	}
	if err := json.Unmarshal(out, &issues); err != nil {
		return err
	}
	for _, issue := range issues {
		if issue.Title == perfIssueTitle {
			_, err := ghRunner("issue", "comment", strconv.Itoa(issue.Number), "-R", factoryRepo, "--body", body)
			return err
		}
	}
	_, err = ghRunner("issue", "create", "-R", factoryRepo, "--title", perfIssueTitle, "--label", "priority:P1", "--body", body)
	return err
}

func profileCompare(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("profile-compare", flag.ContinueOnError)
	fs.SetOutput(stderr)
	currentPath := fs.String("current", "", "profile-capture -json output")
	commit := fs.String("commit", "", "commit the stats were taken at")
	runID := fs.Int64("run", 0, "Actions run id of this run")
	outPath := fs.String("out", "", "record to write (the run's artifact)")
	historyDir := fs.String("history", "", "directory of earlier runs' record.json files")
	fetch := fs.Bool("fetch", false, "download the last -window successful runs' records into -history first")
	window := fs.Int("window", 7, "earlier runs the baseline takes the median of")
	threshold := fs.Float64("threshold", 0.3, "relative p90 increase over baseline that regresses")
	minMs := fs.Float64("min-ms", 0.5, "absolute p90 increase (ms) below which a change is noise")
	summary := fs.String("summary", "", "file the markdown report is appended to (e.g. $GITHUB_STEP_SUMMARY)")
	issue := fs.Bool("issue", false, "open or comment on the regression issue when a span regressed")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *currentPath == "" || *commit == "" || *outPath == "" || *window < 1 || (*fetch && *historyDir == "") || len(fs.Args()) > 0 {
		fmt.Fprintln(stderr, "-current, -commit and -out are required; -fetch needs -history; -window >= 1")
		return 2
	}
	fail := func(err error) int { fmt.Fprintln(stderr, err); return 1 }
	data, err := os.ReadFile(*currentPath)
	if err != nil {
		return fail(err)
	}
	current := perfRecord{Commit: *commit, RunID: *runID, At: time.Now().UTC()}
	if err := json.Unmarshal(data, &current.Profile); err != nil {
		return fail(fmt.Errorf("%s: %w", *currentPath, err))
	}
	if current.Profile.Count == 0 {
		return fail(fmt.Errorf("%s holds no captures", *currentPath))
	}
	record, _ := json.MarshalIndent(current, "", "  ")
	if err := os.MkdirAll(filepath.Dir(*outPath), 0755); err != nil {
		return fail(err)
	}
	if err := os.WriteFile(*outPath, append(record, '\n'), 0644); err != nil {
		return fail(err)
	}
	var history []perfRecord
	if *historyDir != "" {
		if *fetch {
			if err := fetchPerfHistory(*historyDir, *window); err != nil {
				return fail(err)
			}
		}
		if history, err = readPerfHistory(*historyDir, *window); err != nil {
			return fail(err)
		}
	}
	regressions := comparePerf(current.Profile, history, *threshold, *minMs)
	report := perfReport(current, history, regressions, *threshold)
	fmt.Fprint(stdout, report)
	if *summary != "" {
		if f, err := os.OpenFile(*summary, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644); err == nil {
			fmt.Fprint(f, report)
			f.Close()
		}
	}
	if len(regressions) > 0 && *issue {
		if err := filePerfIssue(report); err != nil {
			return fail(err)
		}
	}
	return 0
}
