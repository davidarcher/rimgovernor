package bridge

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// TimelineRecord is one parsed row, or a synthetic "recording_gap" row
// standing in for a corrupt line or a sequence discontinuity caused by
// retention (an older segment was rotated away) or a lost write.
type TimelineRecord struct {
	Kind     string
	Sequence uint64
	HasSeq   bool
	// Run is the recorder run the row belongs to. A ring kept under a
	// profile outlives the process that wrote it (#299), so the rows of
	// successive launches share one file; the sequence continues across
	// them and Run tells them apart.
	Run string
	// WallTime is the row's Unix time in seconds, as the recorder wrote it.
	WallTime float64
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
// corrupt lines or sequence discontinuities. A caller that reads the same
// ring repeatedly keeps a TimelineReader instead.
func ReadTimeline(path string) ([]TimelineRecord, error) {
	return NewTimelineReader(path).Read()
}

// TimelineReader reads one ring repeatedly, decoding each byte once (#375).
// A rotated segment never changes, so its rows are kept by content identity
// (size, modification time and leading bytes, which survive the rename
// chain a rotation performs); the active file only grows, so each Read
// decodes the bytes appended since the last one. A rotation moves the
// active file's decoded rows to its new name without a second decode. A
// file that shrank or was replaced is decoded afresh. The rows Read returns
// are shared between calls: a caller reads them and does not modify them.
// Safe for concurrent use; concurrent reads serialize.
type TimelineReader struct {
	path string

	mu      sync.Mutex
	rotated map[string]*timelineSegment // by file name
	active  *timelineSegment
	decoded int64 // bytes decoded over the reader's life, for tests
}

// timelineSegment is one file's decoded rows. records holds the rows of
// the committed prefix (parsed bytes, lines lines) without the gap a
// sequence discontinuity against the previous file would add: that gap
// depends on the neighbour and is inserted at assembly before the row at
// index lead.
type timelineSegment struct {
	file     string
	size     int64
	modTime  time.Time
	head     []byte
	parsed   int64
	lines    int
	records  []TimelineRecord
	first    uint64
	hasFirst bool
	lead     int
	last     uint64
	hasLast  bool
}

const timelineHeadBytes = 512

// NewTimelineReader prepares a reader over path's ring; nothing is read
// until Read.
func NewTimelineReader(path string) *TimelineReader {
	return &TimelineReader{path: path, rotated: map[string]*timelineSegment{}}
}

// Read returns the ring's rows, oldest first, as ReadTimeline would.
func (r *TimelineReader) Read() ([]TimelineRecord, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	files, err := timelineFiles(r.path)
	if err != nil {
		return nil, err
	}
	segments := make([]*timelineSegment, 0, len(files))
	seen := map[string]bool{}
	var tail []TimelineRecord
	for _, file := range files {
		if file == r.path {
			segment, pending, readErr := r.readActive()
			if readErr != nil {
				return nil, readErr
			}
			if segment != nil {
				segments = append(segments, segment)
				tail = pending
			}
			continue
		}
		seen[file] = true
		segment, readErr := r.readRotated(file)
		if readErr != nil {
			return nil, readErr
		}
		if segment != nil {
			segments = append(segments, segment)
		}
	}
	for name := range r.rotated {
		if !seen[name] {
			delete(r.rotated, name)
		}
	}
	return r.assemble(segments, tail), nil
}

// timelineFiles lists the ring's files oldest first: the rotated segments
// by descending index, then the active path.
func timelineFiles(path string) ([]string, error) {
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
		index, convErr := strconv.Atoi(strings.TrimPrefix(name, prefix))
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
	return files, nil
}

// readRotated returns file's rows: the cached entry when the file is the
// one decoded before (same size, modification time and leading bytes), the
// active file's rows when the file is the segment a rotation just
// archived, else a fresh decode. Nil when the file vanished under the
// listing (a rotation dropped it).
func (r *TimelineReader) readRotated(file string) (*timelineSegment, error) {
	info, err := os.Stat(file)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, fmt.Errorf("flightrecorder: stat %s: %w", file, err)
	}
	head, err := readHead(file, info.Size())
	if err != nil {
		return nil, err
	}
	if cached := r.rotated[file]; cached != nil && sameSegment(cached, info, head) {
		return cached, nil
	}
	for name, cached := range r.rotated {
		if name != file && sameSegment(cached, info, head) {
			delete(r.rotated, name)
			cached.file = file
			relabelGaps(cached.records, file)
			r.rotated[file] = cached
			return cached, nil
		}
	}
	if active := r.active; active != nil && active.parsed == info.Size() && bytes.HasPrefix(head, active.head) {
		// The archived active file: its committed rows are the whole file.
		r.active = nil
		active.file, active.size, active.modTime, active.head = file, info.Size(), info.ModTime(), head
		relabelGaps(active.records, file)
		r.rotated[file] = active
		return active, nil
	}
	data, err := os.ReadFile(file)
	if err != nil {
		return nil, fmt.Errorf("flightrecorder: read %s: %w", file, err)
	}
	segment := &timelineSegment{file: file, size: info.Size(), modTime: info.ModTime(), head: head}
	r.decoded += int64(len(data))
	segment.absorb(data, true)
	r.rotated[file] = segment
	return segment, nil
}

// readActive returns the active file's committed rows plus the rows of an
// unterminated last line, which are decoded again on the next read once
// the line is complete. Nil when the file vanished.
func (r *TimelineReader) readActive() (*timelineSegment, []TimelineRecord, error) {
	info, err := os.Stat(r.path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil, nil
		}
		return nil, nil, fmt.Errorf("flightrecorder: stat %s: %w", r.path, err)
	}
	head, err := readHead(r.path, info.Size())
	if err != nil {
		return nil, nil, err
	}
	active := r.active
	if active == nil || info.Size() < active.parsed || (len(active.head) > 0 && !bytes.HasPrefix(head, active.head)) {
		active = &timelineSegment{file: r.path}
		r.active = active
	}
	if len(head) > len(active.head) {
		active.head = head
	}
	active.size, active.modTime = info.Size(), info.ModTime()
	if info.Size() == active.parsed {
		return active, nil, nil
	}
	data, err := readFrom(r.path, active.parsed, info.Size()-active.parsed)
	if err != nil {
		return nil, nil, err
	}
	r.decoded += int64(len(data))
	tail := active.absorb(data, false)
	return active, tail, nil
}

// absorb decodes data, the file's bytes from offset parsed on, into the
// segment. Complete lines are committed; the rows of a trailing line
// without a newline are returned instead unless final, when the line is
// committed as the file's last.
func (s *timelineSegment) absorb(data []byte, final bool) []TimelineRecord {
	committed := len(data)
	if !final {
		if cut := bytes.LastIndexByte(data, '\n'); cut < 0 {
			committed = 0
		} else {
			committed = cut + 1
		}
	}
	s.decodeLines(data[:committed], true)
	s.parsed += int64(committed)
	if committed == len(data) {
		return nil
	}
	return s.decodeLines(data[committed:], false)
}

// decodeLines decodes rows from lines; commit adds them to the segment,
// else they are returned with the segment's state left as it was.
func (s *timelineSegment) decodeLines(data []byte, commit bool) []TimelineRecord {
	if len(data) == 0 {
		return nil
	}
	var pending []TimelineRecord
	number, last, hasLast := s.lines, s.last, s.hasLast
	for len(data) > 0 {
		var line []byte
		if cut := bytes.IndexByte(data, '\n'); cut < 0 {
			line, data = data, nil
		} else {
			line, data = data[:cut], data[cut+1:]
		}
		number++
		if len(bytes.TrimSpace(line)) == 0 {
			continue
		}
		var record TimelineRecord
		var raw struct {
			Run      string         `json:"run"`
			Sequence *uint64        `json:"sequence"`
			WallTime float64        `json:"wall_time"`
			Kind     *string        `json:"kind"`
			Context  map[string]any `json:"context"`
			Payload  map[string]any `json:"payload"`
		}
		if err := json.Unmarshal(line, &raw); err != nil || raw.Sequence == nil || raw.Kind == nil || raw.Payload == nil || raw.Context == nil {
			record = TimelineRecord{Kind: "recording_gap", File: s.file, Line: number, Reason: "Incomplete or corrupt record"}
		} else {
			sequence := *raw.Sequence
			if hasLast && sequence != last+1 {
				pending = append(pending, TimelineRecord{Kind: "recording_gap", Reason: "Retention or sequence discontinuity", Before: sequence, After: last})
			} else if !hasLast && commit {
				s.first, s.hasFirst, s.lead = sequence, true, len(s.records)+len(pending)
			}
			last, hasLast = sequence, true
			record = TimelineRecord{Kind: *raw.Kind, Sequence: sequence, HasSeq: true, Run: raw.Run, WallTime: raw.WallTime, Context: raw.Context, Payload: raw.Payload}
		}
		pending = append(pending, record)
	}
	if !commit {
		return pending
	}
	s.records = append(s.records, pending...)
	s.lines, s.last, s.hasLast = number, last, hasLast
	return nil
}

// assemble concatenates the segments' rows oldest first, inserting the
// sequence gap each file's first row opens against the previous file.
func (r *TimelineReader) assemble(segments []*timelineSegment, tail []TimelineRecord) []TimelineRecord {
	total := len(tail)
	for _, s := range segments {
		total += len(s.records) + 1
	}
	if total == 0 {
		return nil
	}
	out := make([]TimelineRecord, 0, total)
	var previous uint64
	havePrevious := false
	for _, s := range segments {
		if !s.hasFirst {
			out = append(out, s.records...)
			continue
		}
		if (!havePrevious && s.first != 1) || (havePrevious && s.first != previous+1) {
			out = append(out, s.records[:s.lead]...)
			out = append(out, TimelineRecord{Kind: "recording_gap", Reason: "Retention or sequence discontinuity", Before: s.first, After: previous})
			out = append(out, s.records[s.lead:]...)
		} else {
			out = append(out, s.records...)
		}
		previous, havePrevious = s.last, true
	}
	if len(tail) > 0 {
		// The tail's gaps against the active file's committed rows are
		// decoded with it; only when the file has no committed row does
		// the tail's first row open a gap against the previous file.
		lead := len(tail)
		if last := segments[len(segments)-1]; !last.hasLast {
			for i, row := range tail {
				if row.HasSeq {
					if (!havePrevious && row.Sequence != 1) || (havePrevious && row.Sequence != previous+1) {
						lead = i
					}
					break
				}
			}
		}
		out = append(out, tail[:lead]...)
		if lead < len(tail) {
			out = append(out, TimelineRecord{Kind: "recording_gap", Reason: "Retention or sequence discontinuity", Before: tail[lead].Sequence, After: previous})
		}
		out = append(out, tail[lead:]...)
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

func sameSegment(cached *timelineSegment, info os.FileInfo, head []byte) bool {
	return cached.size == info.Size() && cached.modTime.Equal(info.ModTime()) && bytes.Equal(cached.head, head)
}

// relabelGaps names file on the corrupt-line gaps of rows decoded under
// the active path.
func relabelGaps(records []TimelineRecord, file string) {
	for i := range records {
		if records[i].Kind == "recording_gap" && records[i].File != "" {
			records[i].File = file
		}
	}
}

func readHead(path string, size int64) ([]byte, error) {
	n := size
	if n > timelineHeadBytes {
		n = timelineHeadBytes
	}
	if n == 0 {
		return nil, nil
	}
	return readFrom(path, 0, n)
}

func readFrom(path string, offset, length int64) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("flightrecorder: open %s: %w", path, err)
	}
	defer file.Close()
	data := make([]byte, length)
	n, err := file.ReadAt(data, offset)
	if err != nil && err != io.EOF {
		return nil, fmt.Errorf("flightrecorder: read %s: %w", path, err)
	}
	return data[:n], nil
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
