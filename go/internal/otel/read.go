package otel

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"io"
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
func ReadLines(path string, fn func(line []byte) error) error {
	for _, seg := range Segments(path) {
		f, err := os.Open(seg)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			return err
		}
		err = readSegment(f, fn)
		f.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

func readSegment(r io.Reader, fn func([]byte) error) error {
	br := bufio.NewReaderSize(r, 1<<20)
	for {
		line, err := br.ReadBytes('\n')
		if len(line) > 0 && line[len(line)-1] == '\n' {
			line = bytes.TrimRight(line, "\r\n")
			if len(line) > 0 && json.Valid(line) {
				if ferr := fn(line); ferr != nil {
					return ferr
				}
			}
		}
		if err != nil {
			if errors.Is(err, io.EOF) {
				return nil
			}
			return err
		}
	}
}
