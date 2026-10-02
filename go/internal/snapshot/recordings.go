package snapshot

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecordingsFile is the registry of trimmed testdata that can be
// re-recorded from a newer run (`cmd/rerecord`), relative to go/.
const RecordingsFile = "internal/snapshot/recordings.json"

// Recording names where one trimmed testdata file came from: the
// acceptance case whose stream it was cut from and the review (Tick,
// Seq) or step read (Step) it holds. Out is relative to go/.
type Recording struct {
	Out       string `json:"out"`
	Case      string `json:"case"`
	Tick      int64  `json:"tick,omitempty"`
	Seq       int    `json:"seq,omitempty"`
	Step      string `json:"step,omitempty"`
	KeepCells bool   `json:"keep_cells,omitempty"`
}

// LoadRecordings reads the registry at path; a missing file is empty.
func LoadRecordings(path string) ([]Recording, error) {
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var out []Recording
	if err := json.Unmarshal(data, &out); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	return out, nil
}

// SaveRecordings writes the registry sorted by Out.
func SaveRecordings(path string, recs []Recording) error {
	sort.Slice(recs, func(i, j int) bool { return recs[i].Out < recs[j].Out })
	data, err := json.MarshalIndent(recs, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, append(data, '\n'), 0o644)
}

// Register adds rec to the registry at path, replacing an entry for the
// same Out.
func Register(path string, rec Recording) error {
	recs, err := LoadRecordings(path)
	if err != nil {
		return err
	}
	rec.Out = filepath.ToSlash(rec.Out)
	for i := range recs {
		if recs[i].Out == rec.Out {
			recs[i] = rec
			return SaveRecordings(path, recs)
		}
	}
	return SaveRecordings(path, append(recs, rec))
}

// CaseStreams are the routine streams recorded for caseName under root, a
// RIMGOVERNOR_SNAPSHOT_DIR or a downloaded CI artifact holding one: any
// <...>/snapshots/<case>/ or <root>/<case>/ directory.
func CaseStreams(root, caseName string) ([]string, error) {
	suffix := string(filepath.Separator) + filepath.FromSlash(caseName)
	var out []string
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		name := d.Name()
		if strings.HasPrefix(name, "routine-stream-") && IsStream(name) &&
			strings.HasSuffix(filepath.Dir(p), suffix) {
			out = append(out, p)
		}
		return nil
	})
	sort.Strings(out)
	return out, err
}

// Pick finds rec's counterpart in a newer recording of its case: the
// review (or the same planner and goal's step read) at rec's tick, else
// the first after it, else the last before it. A fresh run's ticks differ
// from the recorded one's, so this is the closest equivalent, not the
// same moment; the test's assertions say whether it still shows the
// behaviour.
func Pick(rec Recording, streams []string) (stream string, review Review, step string, err error) {
	if rec.Step != "" {
		planner, goal, tick, ok := splitStep(rec.Step)
		if !ok {
			return "", Review{}, "", fmt.Errorf("%s: step %q is not step-<planner>-<goal>-<tick>-<seq>", rec.Out, rec.Step)
		}
		var cands []candidate
		for _, s := range streams {
			reads, err := Steps(s)
			if err != nil {
				return "", Review{}, "", err
			}
			for _, r := range reads {
				if r.Planner == planner && string(r.Goal) == goal {
					cands = append(cands, candidate{s, r.Tick, r.Seq, r.String()})
				}
			}
		}
		c, ok := closest(cands, tick)
		if !ok {
			return "", Review{}, "", fmt.Errorf("%s: no %s %s step read in %d stream(s)", rec.Out, planner, goal, len(streams))
		}
		return c.stream, Review{}, c.name, nil
	}
	var cands []candidate
	for _, s := range streams {
		reviews, err := Reviews(s)
		if err != nil {
			return "", Review{}, "", err
		}
		for _, r := range reviews {
			cands = append(cands, candidate{s, r.Tick, r.Seq, r.String()})
		}
	}
	c, ok := closest(cands, domain.Tick(rec.Tick))
	if !ok {
		return "", Review{}, "", fmt.Errorf("%s: no review in %d stream(s)", rec.Out, len(streams))
	}
	return c.stream, Review{Tick: c.tick, Seq: c.seq}, "", nil
}

type candidate struct {
	stream string
	tick   domain.Tick
	seq    int
	name   string
}

func closest(cands []candidate, tick domain.Tick) (candidate, bool) {
	if len(cands) == 0 {
		return candidate{}, false
	}
	sort.SliceStable(cands, func(i, j int) bool {
		if cands[i].tick != cands[j].tick {
			return cands[i].tick < cands[j].tick
		}
		return cands[i].seq < cands[j].seq
	})
	for _, c := range cands {
		if c.tick >= tick {
			return c, true
		}
	}
	return cands[len(cands)-1], true
}

// splitStep parses step-<planner>-<goal>-<tick>-<seq>; the goal may hold
// dashes, the planner does not.
func splitStep(name string) (planner, goal string, tick domain.Tick, ok bool) {
	parts := strings.Split(strings.TrimPrefix(name, "step-"), "-")
	if !strings.HasPrefix(name, "step-") || len(parts) < 4 {
		return "", "", 0, false
	}
	var t int64
	if _, err := fmt.Sscan(parts[len(parts)-2], &t); err != nil {
		return "", "", 0, false
	}
	return parts[0], strings.Join(parts[1:len(parts)-2], "-"), domain.Tick(t), true
}

// TrimTo writes one review (tick, seq) or step read (step) of the
// recording at in to out as gzipped compact JSON, without site cells
// unless keep; a single-frame recording or step-*.json file needs neither.
func TrimTo(in, out string, keep bool, tick int64, seq int, step string) error {
	if step != "" || strings.HasPrefix(filepath.Base(in), "step-") {
		var s Step
		var err error
		if step != "" {
			s, err = LoadStreamStep(in, step)
		} else {
			s, err = LoadStep(in)
		}
		if err != nil {
			return err
		}
		data, err := CompressStep(s, keep)
		if err != nil {
			return err
		}
		return os.WriteFile(out, data, 0o644)
	}
	var r Routine
	var err error
	if IsStream(in) {
		if tick < 0 {
			return fmt.Errorf("%s is a stream: name the review with -tick or a step read with -step (trim -list shows them)", in)
		}
		r, err = LoadReview(in, domain.Tick(tick), seq)
	} else {
		r, err = Load(in)
	}
	if err != nil {
		return err
	}
	if !keep {
		r.TrimCells()
	}
	data, err := Compress(r)
	if err != nil {
		return err
	}
	return os.WriteFile(out, data, 0o644)
}
