package main

import (
	"io"
	"path/filepath"
	"testing"
)

func TestServeRequiresExplicitReadOnlyLocalConfiguration(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--read-only", "--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	if _, err := parseServe(base, io.Discard); err != nil {
		t.Fatal(err)
	}
	for _, args := range [][]string{nil, base[1:], append(append([]string(nil), base...), "--listen", "0.0.0.0:8080"), append(append([]string(nil), base...), "--refresh", "0s"), append(append([]string(nil), base...), "--state", "relative.db"), append(append([]string(nil), base...), "unexpected")} {
		if _, err := parseServe(args, io.Discard); err == nil {
			t.Fatalf("unsafe/incomplete options accepted: %q", args)
		}
	}
}
