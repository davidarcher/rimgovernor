package main

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// pruneControllerLogs deletes all but the newest keep starts' controller
// logs in dir. The stamp in the name (controller-YYYYMMDD-HHMMSS) sorts
// chronologically, so the name is the age.
func pruneControllerLogs(dir string, keep int) {
	stamps := map[string]bool{}
	for _, suffix := range []string{".out.log", ".err.log"} {
		matches, _ := filepath.Glob(filepath.Join(dir, "controller-*"+suffix))
		for _, m := range matches {
			stamps[strings.TrimSuffix(filepath.Base(m), suffix)] = true
		}
	}
	names := make([]string, 0, len(stamps))
	for stamp := range stamps {
		names = append(names, stamp)
	}
	sort.Strings(names)
	if len(names) <= keep {
		return
	}
	for _, stamp := range names[:len(names)-keep] {
		for _, suffix := range []string{".out.log", ".err.log"} {
			os.Remove(filepath.Join(dir, stamp+suffix))
		}
	}
}
