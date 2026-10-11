package logview

import (
	"bufio"
	"bytes"
	"context"
	"io"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/otel"
)

// Follow calls emit with each log row appended to the ring at path after the
// call starts that passes f, polling every interval until ctx ends. Rows
// already present are skipped: the caller prints those first. Follow does not
// collapse.
//
// The sink rotates by renaming logs.jsonl to logs.jsonl.1 (and shifting the
// older segments up), so a byte offset into "the active file" does not
// survive a rotation. Follow therefore remembers the first line of the segment
// it is reading: each poll finds that segment again wherever it now sits,
// reads the rest of it, then every newer segment whole.
func Follow(ctx context.Context, path string, f Filter, interval time.Duration, emit func(bridge.TimelineRecord)) error {
	var t tail
	t.start(path)
	for {
		if err := t.poll(path, func(line []byte) {
			l, ok := otel.DecodeLog(line)
			if !ok {
				return
			}
			rec, ok := bridge.RecordFromLog(l)
			if !ok {
				return
			}
			for _, kept := range f.Apply([]bridge.TimelineRecord{rec}) {
				emit(kept)
			}
		}); err != nil {
			return err
		}
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(interval):
		}
	}
}

// tail is the read position in a ring: the segment, identified by its first
// line, and the byte offset after the last complete line consumed from it.
type tail struct {
	head string
	off  int64
}

// start positions the tail at the end of the ring's complete lines.
func (t *tail) start(path string) {
	segs := otel.Segments(path)
	if len(segs) == 0 {
		return
	}
	last := segs[len(segs)-1]
	data, err := os.ReadFile(last)
	if err != nil {
		return
	}
	t.head, _ = headOf(last)
	t.off = int64(bytes.LastIndexByte(data, '\n') + 1)
}

// poll emits the lines appended since the last poll. Windows refuses to open
// a file for a moment while the sink renames it, so a probe or read that
// fails is retried next poll, never mistaken for a rotation that lost the
// position (which would re-read everything).
func (t *tail) poll(path string, line func([]byte)) error {
	segs := otel.Segments(path)
	from, off := 0, int64(0)
	if t.head != "" {
		// The segment last read, wherever the sink has since renamed it.
		found, unsure := false, false
		for i := len(segs) - 1; i >= 0 && !found; i-- {
			head, ok := headOf(segs[i])
			switch {
			case head == t.head:
				from, off, found = i, t.off, true
			case !ok:
				unsure = true
			}
		}
		if !found && unsure {
			return nil
		}
		// Not found with every probe clean: rotated away whole; read what is left.
	}
	for i := from; i < len(segs); i++ {
		consumed, first, err := readLines(segs[i], off, line)
		if err != nil {
			return nil
		}
		switch {
		case off > 0:
			t.off = consumed
		case first != "":
			t.head, t.off = first, consumed
		}
		off = 0
	}
	return nil
}

// readLines calls line with each complete line of the file after byte off and
// returns the offset after the last one and, when it read from the start, the
// first line (with its newline).
func readLines(path string, off int64, line func([]byte)) (consumed int64, first string, err error) {
	f, err := os.Open(path)
	if err != nil {
		return off, "", err
	}
	defer f.Close()
	if _, err := f.Seek(off, io.SeekStart); err != nil {
		return off, "", err
	}
	r := bufio.NewReaderSize(f, 1<<20)
	for {
		l, err := r.ReadBytes('\n')
		if len(l) == 0 || l[len(l)-1] != '\n' {
			return off, first, nil // EOF, or a line still being written
		}
		if off == 0 && first == "" {
			first = string(l)
		}
		off += int64(len(l))
		if l = bytes.TrimRight(l, "\r\n"); len(l) > 0 {
			line(l)
		}
		if err != nil {
			return off, first, nil
		}
	}
}

// headOf is the first complete line of the file (with its newline) and whether
// the file was readable; a readable file with no complete line yet has head "".
func headOf(path string) (string, bool) {
	f, err := os.Open(path)
	if err != nil {
		return "", false
	}
	defer f.Close()
	l, err := bufio.NewReaderSize(f, 1<<16).ReadString('\n')
	if err != nil {
		return "", err == io.EOF
	}
	return l, true
}
