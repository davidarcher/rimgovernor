package nativeaccept

import (
	"bufio"
	"bytes"
	"io"
	"os"
	"path/filepath"
)

// GameLogName is the per-case copy of the game's own log under the case's
// output directory.
const GameLogName = "game.log"

// GameLogKey is the result.json field GameLogCapture.Close records.
const GameLogKey = "game_log"

// GameLogCapture retains the slice of the game's log (Player.log or
// HeadlessPlayer.log under the root, shared by every case on a kept
// process and overwritten by a relaunch) that a case wrote: OpenGameLog
// notes where the log ends when the case opens, and Close copies from
// there to <output>/game.log. A native exception mid-case is then in the
// case's own evidence instead of a root-level file the next case overwrites.
type GameLogCapture struct {
	Source string
	Offset int64
}

// OpenGameLog starts a capture at the current end of source (zero when
// the log does not exist yet: a launch is coming).
func OpenGameLog(source string) GameLogCapture {
	capture := GameLogCapture{Source: source}
	if info, err := os.Stat(source); err == nil {
		capture.Offset = info.Size()
	}
	return capture
}

// Close copies the log from the capture's offset (from the start when the
// log is shorter than it was at open: the process relaunched) into
// <output>/game.log and records on report under game_log the copy's path,
// bytes and exceptions, the count of lines naming an Exception. The count
// is recorded, not judged. A missing source records nothing.
func (c GameLogCapture) Close(output string, report Report) {
	source, err := os.Open(c.Source)
	if err != nil {
		return
	}
	defer source.Close()
	info, err := source.Stat()
	if err != nil {
		return
	}
	offset := c.Offset
	if info.Size() < offset {
		offset = 0
	}
	if _, err := source.Seek(offset, io.SeekStart); err != nil {
		return
	}
	path := filepath.Join(output, GameLogName)
	target, err := os.Create(path)
	if err != nil {
		return
	}
	defer target.Close()
	written, err := io.Copy(target, source)
	if err != nil {
		return
	}
	if _, err := target.Seek(0, io.SeekStart); err != nil {
		return
	}
	if report != nil {
		report[GameLogKey] = map[string]any{"path": path, "bytes": written, "exceptions": countExceptionLines(target)}
	}
}

// countExceptionLines counts the lines of r that name an Exception
// (NullReferenceException, InvalidOperationException, ...).
func countExceptionLines(r io.Reader) int {
	reader := bufio.NewReader(r)
	count := 0
	for {
		line, err := reader.ReadBytes('\n')
		if bytes.Contains(line, []byte("Exception")) {
			count++
		}
		if err != nil {
			return count
		}
	}
}
