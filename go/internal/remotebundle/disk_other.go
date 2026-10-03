//go:build !windows

package remotebundle

import "errors"

// errFreeDiskUnsupported is a package variable, not an inline error, so the
// shared caller's err check is not a constant comparison on this platform.
var errFreeDiskUnsupported = errors.New("runner preflight requires Windows")

func freeDisk(string) (uint64, error) { return 0, errFreeDiskUnsupported }
