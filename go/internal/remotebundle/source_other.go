//go:build !windows

package remotebundle

import "path/filepath"

func resolveSource(name string) (string, error) { return filepath.EvalSymlinks(name) }
