//go:build windows

package main

import (
	"encoding/json"
	"os"
)

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
