package snapshot

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"strconv"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A recording is one stream per serve (#756): routine-stream-<tick>-<pid>.jsonl
// in DirEnv's directory, one JSON line per enabled review. A line is
//
//	{"Tick": t, "Seq": s, "Key": <tree>}   a keyframe: the whole encoded review
//	{"Tick": t, "Seq": s, "Patch": <node>} a patch against the previous line
//
// where <tree> is Encode's JSON for a Routine and <node> is a field-level
// merge patch over that tree, generic over whatever the recorded struct
// holds:
//
//	{"=": v}             replace the value with v (a new or changed leaf)
//	{"-": 1}             drop the object key (the field went zero)
//	{"~": {k: node}}     patch an object key by key
//	{"#": n, "o": runs, "i": {"k": node}} a slice of n rows laid out from the
//	                     old rows by runs, then patched by index (diffSlice)
//
// Seq numbers the reviews at one tick from 1. Every KeyEvery-th line is a
// keyframe, so materialising a review replays at most KeyEvery-1 patches.
// Each keyframe after the first is a sync point: every mirror section held
// is written again as a keyframe (mirror.go) just before it, so loading one
// review or step read replays from the last sync point before it rather
// than from the stream's start.
const KeyEvery = 20

// streamLine is one line of a stream: a review (Key or Patch), a mirror
// section's table (Section, mirror.go) or a planner step read (Step,
// step.go). Mirror names the bound sections a review or step line left out
// of its tree, by the section version that rebuilds them.
type streamLine struct {
	Tick    domain.Tick
	Seq     int
	Key     json.RawMessage   `json:",omitempty"`
	Patch   json.RawMessage   `json:",omitempty"`
	Mirror  map[string]uint64 `json:",omitempty"`
	Section *sectionFrame     `json:",omitempty"`
	Step    *stepFrame        `json:",omitempty"`
	// Combat is a fight stop and CombatFrame the snapshot frame it
	// decided from (combat.go).
	Combat      json.RawMessage `json:",omitempty"`
	CombatFrame json.RawMessage `json:",omitempty"`
}

// combat reports whether the line is a combat recording's (combat.go),
// which leaves the review and the sections as they are.
func (l streamLine) combat() bool { return l.Combat != nil || l.CombatFrame != nil }

// streamWriter is one serve's open stream in a directory.
type streamWriter struct {
	path  string
	prev  any
	lines int
	last  domain.Tick
	seq   int
	// sections are the mirror sections as last recorded (mirror.go).
	sections map[string]*recSection
	// steps numbers the step reads per planner, goal and tick.
	steps map[string]int
	// keyed is set once a review keyframe is written: the later ones are
	// sync points.
	keyed bool
	// combat is the combat frame as last recorded (combat.go).
	combat combatWriter
}

var (
	streamsMu sync.Mutex
	streams   = map[string]*streamWriter{}
)

// openStream is this process's stream in dir, opened (named after tick)
// by its first line. streamsMu is held.
func openStream(dir string, tick domain.Tick) (*streamWriter, error) {
	rec := streams[dir]
	if rec == nil {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return nil, err
		}
		rec = &streamWriter{path: filepath.Join(dir, fmt.Sprintf("routine-stream-%d-%d.jsonl", tick, os.Getpid())), sections: map[string]*recSection{}, steps: map[string]int{}}
		streams[dir] = rec
	}
	return rec, nil
}

// append writes one line. A failed write may leave it torn.
func (rec *streamWriter) append(line streamLine) error {
	data, err := json.Marshal(line)
	if err != nil {
		return err
	}
	f, err := os.OpenFile(rec.path, os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	_, err = f.Write(append(data, '\n'))
	return errors.Join(err, f.Close())
}

// Record appends r to this process's stream in dir, opening it on the first
// line: a keyframe, then a patch against the previous review. A bound
// field the mirror sections recorded in the stream materialise exactly is
// left to them (mirror.go).
func Record(dir string, r Routine) error {
	tree, err := encodeTree(r)
	if err != nil {
		return err
	}
	streamsMu.Lock()
	defer streamsMu.Unlock()
	rec, err := openStream(dir, r.Tick)
	if err != nil {
		return err
	}
	line := streamLine{Tick: r.Tick, Seq: 1}
	if rec.lines > 0 && rec.last == r.Tick {
		line.Seq = rec.seq + 1
	}
	key := rec.lines%KeyEvery == 0
	if key && rec.keyed {
		rec.keySections()
	}
	tree, line.Mirror = elide(tree, rec.sections)
	if key {
		line.Key, err = json.Marshal(tree)
	} else {
		patch, _ := diffTree(rec.prev, tree)
		line.Patch, err = json.Marshal(patch)
	}
	if err != nil {
		return err
	}
	if err = rec.append(line); err != nil {
		// The line may be torn: the next review restarts with a keyframe.
		rec.lines = 0
		return err
	}
	rec.prev, rec.last, rec.seq = tree, r.Tick, line.Seq
	rec.keyed = rec.keyed || key
	rec.lines++
	return nil
}

// encodeTree is r's Encode JSON as a generic tree (objects, slices,
// json.Number and other scalars) the patches diff. The projection's Facts
// ride once, as FromReview and Compress leave them.
func encodeTree(r Routine) (any, error) {
	if r.Projection != nil {
		trimmed := *r.Projection
		trimmed.Facts = policy.RoutineFacts{}
		r.Projection = &trimmed
	}
	data, err := Encode(r)
	if err != nil {
		return nil, err
	}
	return parseTree(data)
}

func parseTree(data []byte) (any, error) {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	var tree any
	err := dec.Decode(&tree)
	return tree, err
}

// diffTree is the patch node turning old into new, false when they are equal.
func diffTree(old, new any) (map[string]any, bool) {
	switch n := new.(type) {
	case map[string]any:
		if o, ok := old.(map[string]any); ok {
			sub := map[string]any{}
			for k, nv := range n {
				ov, had := o[k]
				if !had {
					sub[k] = map[string]any{"=": nv}
				} else if node, changed := diffTree(ov, nv); changed {
					sub[k] = node
				}
			}
			for k := range o {
				if _, kept := n[k]; !kept {
					sub[k] = map[string]any{"-": 1}
				}
			}
			if len(sub) == 0 {
				return nil, false
			}
			return map[string]any{"~": sub}, true
		}
	case []any:
		if o, ok := old.([]any); ok {
			return diffSlice(o, n)
		}
	}
	if reflect.DeepEqual(old, new) {
		return nil, false
	}
	return map[string]any{"=": new}, true
}

// sliceKeys are the object fields, in preference order, that identify a
// row across reviews: a slice whose rows all carry one with distinct values
// in both reviews is diffed row by row whatever the reorder (a moved
// planning window shifts every cell), otherwise by index.
var sliceKeys = []string{"ID", "Cell", "Pawn", "Key", "Token", "Name", "Definition"}

// diffSlice patches old into new: {"#": len, "o": runs, "i": items}. Runs
// are [old index, count] pairs laying out new from old rows in order
// ([-1, count] for rows old lacks); items patch rows by new index. Without
// "o" the layout is old's prefix.
func diffSlice(old, new []any) (map[string]any, bool) {
	from := make([]int, len(new))
	keyed := false
	if key := sliceKey(old, new); key != "" {
		keyed = true
		at := make(map[string]int, len(old))
		for i, row := range old {
			at[keyOf(row, key)] = i
		}
		for i, row := range new {
			if j, ok := at[keyOf(row, key)]; ok {
				from[i] = j
			} else {
				from[i] = -1
			}
		}
	} else {
		for i := range new {
			if from[i] = i; i >= len(old) {
				from[i] = -1
			}
		}
	}
	items := map[string]any{}
	for i, nv := range new {
		if from[i] < 0 {
			items[strconv.Itoa(i)] = map[string]any{"=": nv}
		} else if node, changed := diffTree(old[from[i]], nv); changed {
			items[strconv.Itoa(i)] = node
		}
	}
	patch := map[string]any{"#": len(new), "i": items}
	identity := true
	var runs [][2]int
	for i, f := range from {
		if f != i && !(f < 0 && i >= len(old)) {
			identity = false
		}
		if n := len(runs); n > 0 && (f < 0 && runs[n-1][0] < 0 || f >= 0 && runs[n-1][0] >= 0 && runs[n-1][0]+runs[n-1][1] == f) {
			runs[n-1][1]++
		} else {
			runs = append(runs, [2]int{f, 1})
		}
	}
	if keyed && !identity {
		patch["o"] = runs
	}
	if len(items) == 0 && len(new) == len(old) && patch["o"] == nil {
		return nil, false
	}
	return patch, true
}

// sliceKey is the first of sliceKeys every row of old and new carries with
// values distinct within each, "" when none does.
func sliceKey(old, new []any) string {
	if len(old) == 0 || len(new) == 0 {
		return ""
	}
next:
	for _, key := range sliceKeys {
		for _, rows := range [][]any{old, new} {
			seen := make(map[string]bool, len(rows))
			for _, row := range rows {
				m, ok := row.(map[string]any)
				if !ok {
					return ""
				}
				v, ok := m[key]
				if !ok {
					continue next
				}
				k := canonical(v)
				if seen[k] {
					continue next
				}
				seen[k] = true
			}
		}
		return key
	}
	return ""
}

func keyOf(row any, key string) string { return canonical(row.(map[string]any)[key]) }

func canonical(v any) string {
	data, _ := json.Marshal(v)
	return string(data)
}

// applyPatch is old with the patch node applied; old is not modified
// (unchanged subtrees are shared).
func applyPatch(old any, node map[string]any) (any, error) {
	if v, ok := node["="]; ok {
		return v, nil
	}
	if sub, ok := node["~"].(map[string]any); ok {
		o, _ := old.(map[string]any)
		out := make(map[string]any, len(o)+len(sub))
		for k, v := range o {
			out[k] = v
		}
		for k, raw := range sub {
			child, ok := raw.(map[string]any)
			if !ok {
				return nil, fmt.Errorf("snapshot: patch %q is not a node", k)
			}
			if _, drop := child["-"]; drop {
				delete(out, k)
				continue
			}
			v, err := applyPatch(o[k], child)
			if err != nil {
				return nil, err
			}
			out[k] = v
		}
		return out, nil
	}
	if n, ok := node["#"].(json.Number); ok {
		length, err := n.Int64()
		if err != nil || length < 0 {
			return nil, fmt.Errorf("snapshot: patch length %v", n)
		}
		o, _ := old.([]any)
		out := make([]any, length)
		if runs, keyed := node["o"].([]any); keyed {
			at := 0
			for _, raw := range runs {
				run, _ := raw.([]any)
				if len(run) != 2 {
					return nil, errors.New("snapshot: patch order run")
				}
				a, _ := run[0].(json.Number)
				b, _ := run[1].(json.Number)
				from, err1 := a.Int64()
				count, err2 := b.Int64()
				if err1 != nil || err2 != nil || count < 0 || at+int(count) > len(out) || (from >= 0 && int(from+count) > len(o)) {
					return nil, errors.New("snapshot: patch order run out of range")
				}
				if from >= 0 {
					copy(out[at:], o[from:from+count])
				}
				at += int(count)
			}
		} else {
			copy(out, o)
		}
		items, _ := node["i"].(map[string]any)
		for k, raw := range items {
			i, err := strconv.Atoi(k)
			child, ok := raw.(map[string]any)
			if err != nil || !ok || i < 0 || i >= len(out) {
				return nil, fmt.Errorf("snapshot: patch item %q", k)
			}
			if out[i], err = applyPatch(out[i], child); err != nil {
				return nil, err
			}
		}
		return out, nil
	}
	return nil, errors.New("snapshot: unknown patch node")
}

// IsStream reports whether path names a recorded stream rather than a
// single frame.
func IsStream(path string) bool {
	return strings.HasSuffix(path, ".jsonl") || strings.HasSuffix(path, ".jsonl.gz")
}

// Review names one recorded review in a stream.
type Review struct {
	Tick domain.Tick
	Seq  int
}

func (r Review) String() string { return fmt.Sprintf("%d-%d", r.Tick, r.Seq) }

// Replay steps through the stream at path in order, calling fn with each
// review that want accepts (nil accepts all) until fn returns false. Only
// accepted reviews are decoded; the rest are patched as trees.
func Replay(path string, want func(Review) bool, fn func(Review, Routine) (bool, error)) error {
	return replayFrom(path, 0, want, fn)
}

func replayFrom(path string, from int64, want func(Review) bool, fn func(Review, Routine) (bool, error)) error {
	return walkFrom(path, from, func(line streamLine, st *replayState) (bool, error) {
		if line.Section != nil || line.Step != nil || line.combat() {
			return true, nil
		}
		at := Review{line.Tick, line.Seq}
		if want != nil && !want(at) {
			return true, nil
		}
		tree, err := restore(st.tree, line.Mirror, st.sections)
		if err != nil {
			return false, err
		}
		r, err := treeRoutine(tree)
		if err != nil {
			return false, err
		}
		return fn(at, r)
	})
}

// replayState is a stream replayed through one line: the last review's
// tree (bound fields left out) and the mirror sections.
type replayState struct {
	tree     any
	sections map[string]*recSection
}

// walk replays the stream at path line by line, calling visit after each
// line is applied until it returns false.
func walk(path string, visit func(streamLine, *replayState) (bool, error)) error {
	return walkFrom(path, 0, visit)
}

// walkFrom is walk from byte offset from, a sync point (syncBefore).
func walkFrom(path string, from int64, visit func(streamLine, *replayState) (bool, error)) error {
	in, done, err := openStreamFile(path)
	if err != nil {
		return err
	}
	defer done()
	if _, err = in.Seek(from, io.SeekStart); err != nil {
		return err
	}
	lines := bufio.NewReader(in)
	st := &replayState{sections: map[string]*recSection{}}
	for n := 1; ; n++ {
		raw, err := lines.ReadBytes('\n')
		if len(bytes.TrimSpace(raw)) == 0 {
			if err == io.EOF {
				return nil
			}
			if err != nil {
				return err
			}
			continue
		}
		var line streamLine
		if jerr := json.Unmarshal(raw, &line); jerr != nil {
			if err == io.EOF {
				return nil // a torn last line: the serve died mid-write
			}
			return fmt.Errorf("%s:%d: %w", path, n, jerr)
		}
		switch {
		case line.Section != nil:
			var s *recSection
			if s, err = st.sections[line.Section.Name].apply(*line.Section); err == nil {
				st.sections[line.Section.Name] = s
			}
		case line.Step != nil, line.combat():
			// A step read leaves the review and the sections as they are.
		case line.Key != nil:
			st.tree, err = parseTree(line.Key)
		case st.tree == nil:
			err = errors.New("patch before any keyframe")
		default:
			var node any
			if node, err = parseTree(line.Patch); err == nil {
				patch, _ := node.(map[string]any)
				st.tree, err = applyPatch(st.tree, patch)
			}
		}
		if err != nil {
			return fmt.Errorf("%s:%d: %w", path, n, err)
		}
		more, err := visit(line, st)
		if err != nil {
			return fmt.Errorf("%s:%d: %w", path, n, err)
		}
		if !more {
			return nil
		}
	}
}

func treeRoutine(tree any) (Routine, error) {
	data, err := json.Marshal(tree)
	if err != nil {
		return Routine{}, err
	}
	return decodeRoutine(data)
}

// Reviews lists the reviews a stream recorded, in order.
func Reviews(path string) ([]Review, error) {
	var out []Review
	err := Replay(path, func(r Review) bool { out = append(out, r); return false }, nil)
	return out, err
}

// LoadReview materialises one review of the stream at path: seq 0 takes the
// last review at tick.
func LoadReview(path string, tick domain.Tick, seq int) (Routine, error) {
	var out Routine
	found := false
	err := seek(path, reviewBefore(tick, seq), func(from int64) (bool, error) {
		found = false
		err := replayFrom(path, from, func(r Review) bool { return r.Tick == tick && (seq == 0 || r.Seq == seq) }, func(_ Review, r Routine) (bool, error) {
			out, found = r, true
			return seq == 0, nil
		})
		return found, err
	})
	if err != nil || found {
		return out, err
	}
	reviews, err := Reviews(path)
	if err != nil {
		return Routine{}, err
	}
	ticks := make([]string, 0, len(reviews))
	for _, r := range reviews {
		ticks = append(ticks, r.String())
	}
	return Routine{}, fmt.Errorf("%s: no review %d (seq %d); recorded %s", path, tick, seq, strings.Join(ticks, " "))
}
