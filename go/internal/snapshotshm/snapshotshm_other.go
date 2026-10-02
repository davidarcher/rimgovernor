//go:build !windows && !linux

package snapshotshm

// openMapping has no shared memory on this platform.
func openMapping(string, bool) (mapping, error) { return nil, ErrUnavailable }
