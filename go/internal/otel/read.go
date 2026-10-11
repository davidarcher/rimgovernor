package otel

import (
	"os"
	"strconv"
)

// Segments lists the existing files of one signal's ring, oldest first:
// path.N ... path.1, then path itself.
func Segments(path string) []string {
	var out []string
	for n := 1; ; n++ {
		if _, err := os.Stat(path + "." + strconv.Itoa(n)); err != nil {
			break
		}
		out = append(out, path+"."+strconv.Itoa(n))
	}
	for i, j := 0, len(out)-1; i < j; i, j = i+1, j-1 {
		out[i], out[j] = out[j], out[i]
	}
	if _, err := os.Stat(path); err == nil {
		out = append(out, path)
	}
	return out
}

// ReadLines calls fn with each complete, valid-JSON line of the ring at path,
// oldest first. A line torn by an unclean exit (no newline, or invalid JSON) is
// skipped. The line slice is only valid during the call. A rotation racing the
// read can skip or repeat a segment; readers of a live ring tolerate that.
// The typed readers (ReadLogs, ReadSpans, ReadMetrics) and Scan build on it.
func ReadLines(path string, fn func(line []byte) error) error {
	return Scan(path, func(_ Position, line []byte) error { return fn(line) })
}
