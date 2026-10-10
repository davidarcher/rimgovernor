package remoteaccept

import (
	"encoding/json"
	"fmt"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
)

// IndexCase is one case's row in index.json: where its evidence lives in
// the shard artifact the verdict job downloaded. Paths are relative to
// that artifact's root (which is also the verdict evidence root), and
// empty when the file was not exported.
type IndexCase struct {
	Name      string   `json:"name"`
	Status    string   `json:"status"`
	Attempts  int      `json:"attempt_count"`
	Shard     string   `json:"shard_id"`
	Artifact  string   `json:"artifact"`
	Diagnosis []string `json:"diagnosis,omitempty"`
	Report    string   `json:"report,omitempty"`
	Flight    string   `json:"flight,omitempty"`
	Explain   string   `json:"explain,omitempty"`
	Log       string   `json:"log,omitempty"`
	Snapshots []string `json:"snapshots,omitempty"`
}

// Index is the per-case evidence index of one remote run. Display only:
// the verdict is aggregate.json's.
type Index struct {
	Version int         `json:"schema_version"`
	Status  string      `json:"status"`
	Cases   []IndexCase `json:"cases"`
}

// maxDiagnosis bounds the postmortem lines copied per case.
const maxDiagnosis = 8

// BuildIndex reads aggregate.json, run.json and each case's exported files
// under root. Failing cases sort first, then by name. Missing optional
// evidence leaves a field empty, never an error.
func BuildIndex(root string) (Index, error) {
	var agg Aggregate
	if err := readLoose(root, "aggregate.json", &agg); err != nil {
		return Index{}, err
	}
	var run Run
	_ = readLoose(root, "run.json", &run)
	idx := Index{Version: 1, Status: agg.Status, Cases: []IndexCase{}}
	for _, c := range agg.Cases {
		row := IndexCase{Name: c.Name, Status: c.Status, Attempts: c.AttemptCount, Shard: c.ShardID}
		if c.ShardID != "" {
			row.Artifact = fmt.Sprintf("acceptance-%d-%d-shard-%s", run.Trigger.RunID, run.Trigger.Attempt, c.ShardID)
			base := c.ShardID + "/fixture"
			row.Report = existing(root, base+"/"+c.Name+"/result.json")
			row.Flight = existing(root, base+"/"+c.Name+"/flight.jsonl")
			row.Explain = existing(root, base+"/"+c.Name+"/explain.jsonl")
			row.Log = existing(root, base+"/"+c.Name+".log")
			matches, _ := filepath.Glob(filepath.Join(root, filepath.FromSlash(base+"/"+SnapshotDir+"/"+c.Name), "routine-stream-*.jsonl"))
			sort.Strings(matches)
			for _, m := range matches {
				row.Snapshots = append(row.Snapshots, path.Join(base, SnapshotDir, c.Name, filepath.Base(m)))
			}
			if row.Report != "" {
				row.Diagnosis = diagnosisLines(filepath.Join(root, filepath.FromSlash(row.Report)))
			}
		}
		idx.Cases = append(idx.Cases, row)
	}
	sort.SliceStable(idx.Cases, func(i, j int) bool {
		pi, pj := idx.Cases[i].Status == "passed", idx.Cases[j].Status == "passed"
		if pi != pj {
			return !pi
		}
		return idx.Cases[i].Name < idx.Cases[j].Name
	})
	return idx, nil
}

func readLoose(root, name string, v any) error {
	b, err := os.ReadFile(filepath.Join(root, name))
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}

func existing(root, rel string) string {
	if info, err := os.Stat(filepath.Join(root, filepath.FromSlash(rel))); err == nil && info.Mode().IsRegular() {
		return rel
	}
	return ""
}

// diagnosisLines reads the postmortem digest a failed case's report
// carries ("diagnosis", postmortem.Digest) as "section: text" lines.
func diagnosisLines(report string) []string {
	var r struct {
		Diagnosis *struct {
			Sections []struct {
				Name  string `json:"name"`
				Lines []struct {
					Text string `json:"text"`
				} `json:"lines"`
			} `json:"sections"`
		} `json:"diagnosis"`
	}
	b, err := os.ReadFile(report)
	if err != nil || json.Unmarshal(b, &r) != nil || r.Diagnosis == nil {
		return nil
	}
	var out []string
	for _, s := range r.Diagnosis.Sections {
		for _, l := range s.Lines {
			if len(out) == maxDiagnosis {
				return out
			}
			out = append(out, s.Name+": "+l.Text)
		}
	}
	return out
}

// Markdown renders the index as a step-summary table.
func (x Index) Markdown() string {
	var b strings.Builder
	fmt.Fprintf(&b, "### Case index (%s)\n\n", x.Status)
	b.WriteString("| Case | Status | Attempts | Shard | `gh run download -n` | Evidence | Postmortem |\n|---|---|---|---|---|---|---|\n")
	for _, c := range x.Cases {
		ev := []string{}
		for _, p := range []string{c.Report, c.Flight, c.Explain, c.Log} {
			if p != "" {
				ev = append(ev, "`"+p+"`")
			}
		}
		if len(c.Snapshots) > 0 {
			ev = append(ev, fmt.Sprintf("%d snapshot streams `%s`", len(c.Snapshots), path.Dir(c.Snapshots[0])+"/"))
		}
		fmt.Fprintf(&b, "| %s | %s | %d | %s | %s | %s | %s |\n", cell(c.Name), cell(c.Status), c.Attempts, cell(c.Shard),
			code(c.Artifact), strings.Join(ev, "<br>"), cell(strings.Join(c.Diagnosis, "<br>")))
	}
	return b.String()
}

func code(s string) string {
	if s == "" {
		return ""
	}
	return "`" + s + "`"
}

func cell(s string) string {
	return strings.NewReplacer("|", "\\|", "\n", " ", "\r", "").Replace(s)
}
