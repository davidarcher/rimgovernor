//go:build !windows && !linux

package snapshotshm

// Open has no shared-memory reader on this platform.
func Open(string) (*Reader, error) { return nil, ErrUnavailable }
