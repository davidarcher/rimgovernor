//go:build !windows && !linux

package gamehost

import "errors"

var errNoProcess = errors.New("gamehost: no such process")

// processStartTime is unsupported here: the game runs on Windows or Linux.
func processStartTime(int) (int64, error) {
	return 0, errors.New("gamehost: process start time unsupported on this OS")
}
