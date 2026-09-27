package gamehost

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
)

var errNoProcess = errors.New("gamehost: no such process")

// processStartTime is /proc/<pid>/stat field 22 (clock ticks since boot);
// a missing or zombie pid is errNoProcess.
func processStartTime(pid int) (int64, error) {
	data, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		if os.IsNotExist(err) {
			return 0, errNoProcess
		}
		return 0, err
	}
	i := bytes.LastIndexByte(data, ')')
	if i < 0 {
		return 0, fmt.Errorf("malformed /proc/%d/stat", pid)
	}
	fields := strings.Fields(string(data[i+1:]))
	if len(fields) < 20 {
		return 0, fmt.Errorf("short /proc/%d/stat", pid)
	}
	if fields[0] == "Z" || fields[0] == "X" {
		return 0, errNoProcess
	}
	return strconv.ParseInt(fields[19], 10, 64)
}
