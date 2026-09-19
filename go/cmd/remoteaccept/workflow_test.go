package main

import (
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
)

func TestWorkflowAuthorizationGates(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Windows workflow entry point")
	}
	p, err := exec.LookPath("pwsh")
	if err != nil {
		t.Fatal("PowerShell 7 is required to validate the Windows workflow")
	}
	script, err := filepath.Abs("../../../scripts/test_remote_workflow.ps1")
	if err != nil {
		t.Fatal(err)
	}
	if output, err := exec.Command(p, "-NoProfile", "-File", script, "-TestRoot", t.TempDir()).CombinedOutput(); err != nil {
		t.Fatalf("workflow gates: %v\n%s", err, output)
	}
}
