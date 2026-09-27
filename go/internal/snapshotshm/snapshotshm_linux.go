package snapshotshm

import (
	"fmt"
	"os"
	"time"

	"golang.org/x/sys/unix"
)

type linuxMapping struct {
	file *os.File
	view []byte
}

// Open maps the /dev/shm file the native stream created. Linux has no
// ready event; Wait polls the head.
func Open(name string) (*Reader, error) {
	file, err := os.OpenFile(name, os.O_RDONLY, 0)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	info, err := file.Stat()
	if err != nil || info.Size() < headerBytes {
		_ = file.Close()
		return nil, fmt.Errorf("%w: ring below its header", ErrUnavailable)
	}
	view, err := unix.Mmap(int(file.Fd()), 0, int(info.Size()), unix.PROT_READ, unix.MAP_SHARED)
	if err != nil {
		_ = file.Close()
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	return newReader(&linuxMapping{file: file, view: view})
}

func (m *linuxMapping) bytes() []byte           { return m.view }
func (m *linuxMapping) wait(time.Duration) bool { return false }

func (m *linuxMapping) close() error {
	first := unix.Munmap(m.view)
	m.view = nil
	if err := m.file.Close(); err != nil && first == nil {
		first = err
	}
	return first
}
