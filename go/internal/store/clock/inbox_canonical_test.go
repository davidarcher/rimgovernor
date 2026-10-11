package clock

import (
	"os"
	"path/filepath"
	"testing"
)

func TestCanonicalClockProfileResolvesOnceAndRechecksTheDirectory(t *testing.T) {
	dir := t.TempDir()
	first, err := canonicalClockProfile(dir)
	if err != nil {
		t.Fatal(err)
	}
	if held, ok := canonicalProfiles.Load(dir); !ok || held.(string) != first {
		t.Fatalf("resolved profile not remembered: %v %v", held, ok)
	}
	again, err := canonicalClockProfile(dir)
	if err != nil || again != first {
		t.Fatalf("second read = %q, %v; want %q", again, err, first)
	}
	if err := os.Remove(dir); err != nil {
		t.Fatal(err)
	}
	if _, err := canonicalClockProfile(dir); err == nil {
		t.Fatal("a removed profile directory must fail")
	}
	if _, ok := canonicalProfiles.Load(dir); ok {
		t.Fatal("a failed check must drop the remembered path")
	}
	file := filepath.Join(t.TempDir(), "f")
	if err := os.WriteFile(file, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := canonicalClockProfile(file); err == nil {
		t.Fatal("a file is not a profile directory")
	}
}
