package bridge

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// TimelineRecord is one parsed row, or a synthetic "recording_gap" row
// standing in for a corrupt line or a sequence discontinuity caused by
// retention (an older segment was rotated away) or a lost write.
type TimelineRecord struct {
	Kind     string
	Sequence uint64
	HasSeq   bool
	Context  map[string]any
	Payload  map[string]any
	// Gap fields, set only when Kind == "recording_gap".
	Reason string
	File   string
	Line   int
	Before uint64
	After  uint64
}

// ReadTimeline reads path's retained segments oldest-first, then the active
// file, yielding one TimelineRecord per line plus synthetic gap records for
// corrupt lines or sequence discontinuities.
func ReadTimeline(path string) ([]TimelineRecord, error) {
	dir, base := filepath.Dir(path), filepath.Base(path)
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, fmt.Errorf("flightrecorder: read dir: %w", err)
	}
	type segment struct {
		index int
		file  string
	}
	var segments []segment
	prefix := base + "."
	for _, entry := range entries {
		name := entry.Name()
		if !strings.HasPrefix(name, prefix) {
			continue
		}
		suffix := strings.TrimPrefix(name, prefix)
		index, convErr := strconv.Atoi(suffix)
		if convErr != nil {
			continue
		}
		segments = append(segments, segment{index, filepath.Join(dir, name)})
	}
	sort.Slice(segments, func(i, j int) bool { return segments[i].index > segments[j].index })
	files := make([]string, 0, len(segments)+1)
	for _, s := range segments {
		files = append(files, s.file)
	}
	if _, err = os.Stat(path); err == nil {
		files = append(files, path)
	}
	var records []TimelineRecord
	var previous uint64
	havePrevious := false
	for _, file := range files {
		lines, err := readFlightLines(file)
		if err != nil {
			return nil, err
		}
		for number, line := range lines {
			if len(strings.TrimSpace(line)) == 0 {
				continue
			}
			var raw struct {
				Sequence *uint64        `json:"sequence"`
				Kind     *string        `json:"kind"`
				Context  map[string]any `json:"context"`
				Payload  map[string]any `json:"payload"`
			}
			if err := json.Unmarshal([]byte(line), &raw); err != nil || raw.Sequence == nil || raw.Kind == nil || raw.Payload == nil || raw.Context == nil {
				records = append(records, TimelineRecord{Kind: "recording_gap", File: file, Line: number + 1, Reason: "Incomplete or corrupt record"})
				continue
			}
			sequence := *raw.Sequence
			if (!havePrevious && sequence != 1) || (havePrevious && sequence != previous+1) {
				records = append(records, TimelineRecord{Kind: "recording_gap", Reason: "Retention or sequence discontinuity", Before: sequence, After: previous})
			}
			previous, havePrevious = sequence, true
			records = append(records, TimelineRecord{Kind: *raw.Kind, Sequence: sequence, HasSeq: true, Context: raw.Context, Payload: raw.Payload})
		}
	}
	return records, nil
}

func readFlightLines(path string) ([]string, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("flightrecorder: open %s: %w", path, err)
	}
	defer file.Close()
	var lines []string
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 0, 64*1024), 16<<20)
	for scanner.Scan() {
		lines = append(lines, scanner.Text())
	}
	if err = scanner.Err(); err != nil {
		return nil, fmt.Errorf("flightrecorder: scan %s: %w", path, err)
	}
	return lines, nil
}
