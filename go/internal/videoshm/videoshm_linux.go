package videoshm

import (
	"errors"
	"fmt"
	"os"
	"time"

	"golang.org/x/sys/unix"
)

type linuxMapping struct {
	file *os.File
	view []byte
}

// Open maps the /dev/shm file the native driver created and uses the same
// flock the producer takes around each publish.
func Open(name string) (Reader, error) {
	file, err := os.OpenFile(name, os.O_RDONLY, 0)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	info, err := file.Stat()
	if err != nil || info.Size() < Capacity {
		_ = file.Close()
		return nil, fmt.Errorf("%w: buffer is not the expected size", ErrUnavailable)
	}
	view, err := unix.Mmap(int(file.Fd()), 0, Capacity, unix.PROT_READ, unix.MAP_SHARED)
	if err != nil {
		_ = file.Close()
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	return &reader{m: &linuxMapping{file: file, view: view}}, nil
}

func (m *linuxMapping) bytes() []byte { return m.view }

// lock takes the exclusive flock the producer contends for, retrying without
// blocking so a stuck reader can never stall the game's publish.
func (m *linuxMapping) lock() bool {
	deadline := time.Now().Add(lockWait)
	for {
		err := unix.Flock(int(m.file.Fd()), unix.LOCK_EX|unix.LOCK_NB)
		if err == nil {
			return true
		}
		if !errors.Is(err, unix.EWOULDBLOCK) || time.Now().After(deadline) {
			return false
		}
		time.Sleep(time.Millisecond)
	}
}

func (m *linuxMapping) unlock() { _ = unix.Flock(int(m.file.Fd()), unix.LOCK_UN) }

func (m *linuxMapping) close() error {
	var first error
	if m.view != nil {
		first = unix.Munmap(m.view)
		m.view = nil
	}
	if m.file != nil {
		if err := m.file.Close(); err != nil && first == nil {
			first = err
		}
		m.file = nil
	}
	return first
}
