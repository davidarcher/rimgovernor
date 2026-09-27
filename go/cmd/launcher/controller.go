package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Record is the controller the launcher last started
// (.rimgovernor/launcher-controller.json).
type Record struct {
	PID  int    `json:"pid"`
	Exe  string `json:"exe"`
	Port int    `json:"port"`
}

func readRecord(path string) (Record, bool) {
	var r Record
	data, err := os.ReadFile(path)
	if err != nil || json.Unmarshal(data, &r) != nil || r.PID == 0 {
		return Record{}, false
	}
	return r, true
}

func writeRecord(path string, r Record) error {
	data, err := json.Marshal(r)
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0644)
}

// Owner is a process listening on the controller's port; Path is "" when
// Windows withholds it.
type Owner struct {
	PID  int
	Path string
}

// PortOwners decides what Play does with the port's listeners: ours (an
// exe under repo/.rimgovernor, or the recorded pid) are stopped, anything
// else refuses. The .rimgovernor scope keeps a launcher in the main
// checkout off controllers from worktrees nested under .claude/worktrees.
func PortOwners(owners []Owner, repo string, recorded int) (stop []int, err error) {
	prefix := strings.ToLower(filepath.Join(filepath.Clean(repo), ".rimgovernor")) + string(filepath.Separator)
	for _, o := range owners {
		mine := o.PID == recorded && recorded != 0
		if o.Path != "" && strings.HasPrefix(strings.ToLower(filepath.Clean(o.Path)), prefix) {
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
