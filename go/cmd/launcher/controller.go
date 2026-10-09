package main

import (
	"fmt"
	"path"
	"strings"
)

// Record is the controller the launcher last started
// (.rimgovernor/launcher-controller.json).
type Record struct {
	PID  int    `json:"pid"`
	Exe  string `json:"exe"`
	Port int    `json:"port"`
}

// Owner is a process listening on the controller's port; Path is "" when
// Windows withholds it.
type Owner struct {
	PID  int
	Path string
}

// winPath folds a Windows path for comparison on any GOOS: the nightly race
// job runs on Linux, where filepath ignores backslashes.
func winPath(p string) string {
	return strings.ToLower(path.Clean(strings.ReplaceAll(p, `\`, "/")))
}

// PortOwners decides what Play does with the port's listeners: ours (an
// exe under repo/.rimgovernor, or the recorded pid) are stopped, anything
// else refuses. The .rimgovernor scope keeps a launcher in the main
// checkout off controllers from worktrees nested under .claude/worktrees.
func PortOwners(owners []Owner, repo string, recorded int) (stop []int, err error) {
	prefix := winPath(repo) + "/.rimgovernor/"
	for _, o := range owners {
		mine := o.PID == recorded && recorded != 0
		if o.Path != "" && strings.HasPrefix(winPath(o.Path), prefix) {
			mine = true
		}
		if !mine {
			path := o.Path
			if path == "" {
				path = "path unknown"
			}
			return nil, fmt.Errorf("the port is held by process %d (%s), which this launcher did not start; stop it or choose another port", o.PID, path)
		}
		stop = append(stop, o.PID)
	}
	return stop, nil
}
