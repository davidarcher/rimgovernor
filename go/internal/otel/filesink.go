package otel

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"sync"
)

// rotatingFile appends lines to path. Once the active file reaches
// segmentBytes it is renamed to path.1 (older ones shift to path.2 ... and the
// oldest beyond `segments` files in total is deleted), so disk use is bounded by
// segmentBytes*segments. Writes go straight to the OS (no user-space buffer): a
// process crash loses nothing that Write returned for. fsync happens only on
// rotation, close and when the caller asks (durable).
type rotatingFile struct {
	path         string
	segmentBytes int64
	segments     int

	mu     sync.Mutex
	file   *os.File
	size   int64
	closed bool
}

func openRotatingFile(path string, segmentBytes int64, segments int) (*rotatingFile, error) {
	if segmentBytes < 1 || segments < 2 {
		return nil, errors.New("otel: file bounds are too small")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, fmt.Errorf("otel: %w", err)
	}
	f := &rotatingFile{path: path, segmentBytes: segmentBytes, segments: segments}
	if err := f.open(); err != nil {
		return nil, err
	}
	return f, nil
}

// open opens the active file for append. After an unclean exit the file can
// end in a partial line: a newline is written first so the next record starts
// on its own line and only the torn one is unreadable.
func (f *rotatingFile) open() error {
	file, err := os.OpenFile(f.path, os.O_CREATE|os.O_RDWR|os.O_APPEND, 0o644)
	if err != nil {
		return fmt.Errorf("otel: %w", err)
	}
	info, err := file.Stat()
	if err != nil {
		file.Close()
		return fmt.Errorf("otel: %w", err)
	}
	size := info.Size()
	if size > 0 {
		var last [1]byte
		if _, err := file.ReadAt(last[:], size-1); err == nil && last[0] != '\n' {
			if _, err := file.Write([]byte{'\n'}); err != nil {
				file.Close()
				return fmt.Errorf("otel: %w", err)
			}
			size++
		}
	}
	f.file, f.size = file, size
	return nil
}

func (f *rotatingFile) segmentPath(n int) string { return f.path + "." + strconv.Itoa(n) }

// write appends p (whole lines). A batch is never split across segments, so a
// segment can exceed segmentBytes by up to one batch.
func (f *rotatingFile) write(p []byte, durable bool) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.closed {
		return errors.New("otel: file sink closed")
	}
	if f.file == nil {
		if err := f.open(); err != nil {
			return err
		}
	}
	if f.size > 0 && f.size+int64(len(p)) > f.segmentBytes {
		if err := f.rotate(); err != nil {
			return err
		}
	}
	n, err := f.file.Write(p)
	f.size += int64(n)
	if err != nil {
		return fmt.Errorf("otel: %w", err)
	}
	if durable {
		if err := f.file.Sync(); err != nil {
			return fmt.Errorf("otel: %w", err)
		}
	}
	return nil
}

func (f *rotatingFile) rotate() error {
	_ = f.file.Sync()
	if err := f.file.Close(); err != nil {
		return fmt.Errorf("otel: %w", err)
	}
	f.file = nil
	_ = os.Remove(f.segmentPath(f.segments - 1))
	for n := f.segments - 2; n >= 1; n-- {
		if err := os.Rename(f.segmentPath(n), f.segmentPath(n+1)); err != nil && !errors.Is(err, os.ErrNotExist) {
			return fmt.Errorf("otel: %w", err)
		}
	}
	if err := os.Rename(f.path, f.segmentPath(1)); err != nil {
		return fmt.Errorf("otel: %w", err)
	}
	return f.open()
}

func (f *rotatingFile) close() error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.closed = true
	if f.file == nil {
		return nil
	}
	syncErr := f.file.Sync()
	err := f.file.Close()
	f.file, f.closed = nil, true
	if err != nil {
		return err
	}
	return syncErr
}
