package buildingruntime

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestOwnershipAndRelease(t *testing.T) {
	dir := t.TempDir()
	owner, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	defer owner.Close()
	if second, err := AcquireProfile(context.Background(), filepath.Join(dir, ".")); !errors.Is(err, ErrProfileOwned) {
		if second != nil {
			second.Close()
		}
		t.Fatalf("second owner: %v", err)
	}
	if err = owner.Close(); err != nil {
		t.Fatal(err)
	}
	if err = owner.Close(); err != nil {
		t.Fatal(err)
	}
	next, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	next.Close()
	if _, err = os.Stat(filepath.Join(dir, "rimgovernor-controller.lock")); err != nil {
		t.Fatal("lock file was removed", err)
	}
	cancelled, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err = AcquireProfile(cancelled, dir); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

func TestProcessCrashReleasesOwnership(t *testing.T) {
	dir := t.TempDir()
	cmd := exec.Command(os.Args[0], "-test.run=^TestOwnerProcessHelper$")
	cmd.Env = append(os.Environ(), "RIMGOVERNOR_OWNER_TEST_PROFILE="+dir)
	output, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	input, err := cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	defer input.Close()
	if err = cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { cmd.Process.Kill(); cmd.Wait() }()
	line, err := bufio.NewReader(output).ReadString('\n')
	if err != nil || line != "owned\n" {
		t.Fatalf("child ownership: %q %v", line, err)
	}
	if owner, err := AcquireProfile(context.Background(), dir); !errors.Is(err, ErrProfileOwned) {
		if owner != nil {
			owner.Close()
		}
		t.Fatalf("cross-process lock failed: %v", err)
	}
	if err = cmd.Process.Kill(); err != nil {
		t.Fatal(err)
	}
	_ = cmd.Wait()
	owner, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	owner.Close()
}

func TestOwnerProcessHelper(t *testing.T) {
	dir := os.Getenv("RIMGOVERNOR_OWNER_TEST_PROFILE")
	if dir == "" {
		return
	}
	owner, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	defer owner.Close()
	fmt.Println("owned")
	var one [1]byte
	_, _ = os.Stdin.Read(one[:])
}
