//go:build !windows && !linux

package videoshm

// Open has no shared-memory reader on this platform; callers fall back to
// the ReadFrame RPC.
func Open(name string) (Reader, error) { return nil, ErrUnavailable }
