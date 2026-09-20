package main

import (
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
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

func TestWorkflowProgressObserverCredentialBoundary(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Windows observer launcher")
	}
	pwsh, err := exec.LookPath("pwsh")
	if err != nil {
		t.Fatal(err)
	}
	script, err := filepath.Abs("../../../scripts/remote_workflow.ps1")
	if err != nil {
		t.Fatal(err)
	}
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	harness := filepath.Join(t.TempDir(), "observer.ps1")
	writeProgressFile(t, harness, `param($Script, $Executable)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-ProgressObserver' }, $true)
. ([scriptblock]::Create($fn.Extent.Text))
$env:RG_PROGRESS_PROBE = '1'
$env:GH_TOKEN = 'stdin-sentinel'
$env:TEST_SECRET = 'secret'; $env:TEST_PASSWORD = 'password'; $env:TEST_PRIVATE_KEY = 'private'
$env:REMOTE_BUNDLE_IDENTITY = 'identity'
$observer = New-ProgressObserver $Executable '-test.run=^TestWorkflowProgressProbe$' $env:GH_TOKEN
try {
    if (-not $observer.WaitForExit(10000)) { $observer.Kill(); throw 'observer timed out' }
    if ($observer.ExitCode) { throw 'observer credential probe failed' }
    if ($env:GH_TOKEN -ne 'stdin-sentinel') { throw 'observer launch scrubbed parent before bootstrap' }
} finally { $observer.Dispose() }
`)
	if output, err := exec.Command(pwsh, "-NoProfile", "-File", harness, script, exe).CombinedOutput(); err != nil {
		t.Fatalf("observer: %v\n%s", err, output)
	}
}

func TestWorkflowProgressProbe(t *testing.T) {
	if os.Getenv("RG_PROGRESS_PROBE") != "1" {
		t.Skip("subprocess only")
	}
	for _, entry := range os.Environ() {
		key, _, _ := strings.Cut(entry, "=")
		for _, secret := range []string{"TOKEN", "SECRET", "PASSWORD", "PRIVATE_KEY", "REMOTE_BUNDLE_IDENTITY"} {
			if strings.Contains(strings.ToUpper(key), secret) {
				t.Errorf("observer inherited credential variable %s", key)
			}
		}
	}
	b, err := io.ReadAll(os.Stdin)
	if err != nil || strings.TrimSpace(string(b)) != "stdin-sentinel" {
		t.Fatal("observer did not receive stdin credential")
	}
}

func TestWorkflowProgressOrderingAndFailureIsolation(t *testing.T) {
	b, err := os.ReadFile("../../../scripts/remote_workflow.ps1")
	if err != nil {
		t.Fatal(err)
	}
	script := string(b)
	start := strings.Index(script, "$observer = Start-Progress $Evidence $Shard")
	scrub := strings.Index(script, "Get-ChildItem Env: | Where-Object Name -Match 'TOKEN|SECRET|PASSWORD|PRIVATE_KEY'")
	suite := strings.Index(script, "& $boot.acceptance suite")
	if start < 0 || start >= scrub || scrub >= suite || !strings.Contains(script, "Stop-Progress $observer") || !strings.Contains(script, "WaitForExit(30000)") {
		t.Fatal("observer/suite lifecycle ordering changed")
	}
	b, err = os.ReadFile("../../../.github/workflows/remote-acceptance.yml")
	if err != nil {
		t.Fatal(err)
	}
	workflow := string(b)
	if strings.Count(workflow, "checks: write") != 2 || !strings.Contains(workflow, "$env:GH_TOKEN | & C:\\rg\\remoteaccept.exe progress -finish") || !strings.Contains(workflow, "$env:GH_TOKEN | go run ./cmd/remoteaccept progress -sweep") {
		t.Fatal("progress permissions or stdin wiring missing")
	}
}
