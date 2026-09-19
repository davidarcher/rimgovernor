package remotebundle

import (
	"fmt"
	"os"
	"strings"

	"golang.org/x/sys/windows"
)

// Resolve by the opened handle: EvalSymlinks cannot resolve some NTFS junction
// chains even when Windows can open their target. The final path also detects
// cycles reached through different junction names.
func resolveSource(name string) (string, error) {
	f, err := os.Open(name)
	if err != nil {
		return "", err
	}
	defer f.Close()
	buf := make([]uint16, 32768)
	n, err := windows.GetFinalPathNameByHandle(windows.Handle(f.Fd()), &buf[0], uint32(len(buf)), 0)
	if err != nil {
		return "", err
	}
	if n >= uint32(len(buf)) {
		return "", fmt.Errorf("resolved source path exceeds Windows path buffer")
	}
	p := windows.UTF16ToString(buf[:n])
	if strings.HasPrefix(p, `\\?\UNC\`) {
		return `\\` + strings.TrimPrefix(p, `\\?\UNC\`), nil
	}
	return strings.TrimPrefix(p, `\\?\`), nil
}
