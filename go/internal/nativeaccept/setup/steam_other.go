//go:build !windows

package setup

// steamInstallPath has no registry to read off Windows; discovery falls
// back to the environment overrides.
func steamInstallPath() (string, error) { return "", nil }
