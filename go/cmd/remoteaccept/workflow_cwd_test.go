package main

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

// Run the actual job-loop body without bootstrap, credentials or a game. The
// probe inherits the same cwd as a native suite in the hosted sibling layout.
func TestWorkflowSuiteWorkingDirectory(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Windows workflow entry point")
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
	workspace := t.TempDir()
	repo := filepath.Join(workspace, "tested")
	write := func(path, contents string) {
		t.Helper()
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(contents), 0644); err != nil {
			t.Fatal(err)
		}
	}
	write(filepath.Join(workspace, "trusted", ".git"), "synthetic trusted checkout")
	write(filepath.Join(repo, ".git"), "synthetic tested checkout")
	for _, root := range inputs.NativeSourceRoots() {
		write(filepath.Join(repo, filepath.FromSlash(root)), "synthetic native input")
	}
	// scripts/fixtures is a directory in the real checkout.
	if err := os.Remove(filepath.Join(repo, "scripts", "fixtures")); err != nil {
		t.Fatal(err)
	}
	write(filepath.Join(repo, cases.CommittedSavesDir, "checkpoint.rws"), "synthetic save")
	write(filepath.Join(repo, "go", "go.mod"), "module github.com/davidarcher/RimGovernor/go\n\ngo 1.24\n")
	write(filepath.Join(repo, "go", "internal", "buildingruntime", "cmd", "buildingsmoke", "main.go"), "package main\nfunc main() {}\n")
	hash, err := na.SourceTreeHash(repo)
	if err != nil {
		t.Fatal(err)
	}
	manifest, err := json.Marshal(na.PackageManifest{SourceTree: hash})
	if err != nil {
		t.Fatal(err)
	}
	write(filepath.Join(workspace, "package", na.PackageManifestName), string(manifest))
	wrapper := filepath.Join(workspace, "suite.ps1")
	write(wrapper, `if ((Get-Location).Path -cne (Join-Path $env:RG_CWD_WORKSPACE 'tested' 'go')) { throw 'incorrect suite cwd' }
if (@(Get-ChildItem Env: | Where-Object Name -Match 'TOKEN|SECRET|PASSWORD|PRIVATE_KEY').Count) { throw 'suite inherited a credential' }
if ($env:RG_CWD_OUTCOME -eq '0') {
    & $env:RG_CWD_TEST_EXE '-test.run=^TestWorkflowCWDProbe$' '-test.v'
    if ($LASTEXITCODE) { throw 'cwd probe failed' }
}
if ($env:RG_CWD_OUTCOME -eq 'throw') { throw 'synthetic suite exception' }
$global:LASTEXITCODE = [int]$env:RG_CWD_OUTCOME
`)
	boot, err := json.Marshal(map[string]string{"acceptance": wrapper, "root": workspace, "controller": "unused"})
	if err != nil {
		t.Fatal(err)
	}
	write(filepath.Join(workspace, "bootstrap.json"), string(boot))
	harness := filepath.Join(workspace, "launch.ps1")
	write(harness, `param($Script, $Repo, $Workspace)
$ErrorActionPreference = 'Stop'
function Read-JSON($Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$scrub = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.PipelineAst] -and $node.Extent.Text.StartsWith('Get-ChildItem Env: | Where-Object Name -Match') }, $true)
if (-not $scrub) { throw 'suite credential scrub missing' }
$env:GH_TOKEN = 'synthetic-token'; $env:TEST_SECRET = 'synthetic-secret'; $env:TEST_PASSWORD = 'synthetic-password'; $env:TEST_PRIVATE_KEY = 'synthetic-key'
& ([scriptblock]::Create($scrub.Extent.Text))
$loop = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.ForEachStatementAst] -and $node.Variable.VariablePath.UserPath -eq 'job' }, $true)
if (-not $loop) { throw 'suite job loop missing' }
$body = $loop.Body.Extent.Text
$launch = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
$job = @{bootstrap=(Join-Path $Workspace 'bootstrap.json');role='fixture';output=$Workspace}
$rows = @(@{mod_role='fixture';name='synthetic/probe'})
$deadline = [DateTime]::UtcNow.AddMinutes(1)
foreach ($outcome in @('0', '7', 'throw')) {
$env:RG_CWD_OUTCOME = $outcome
$bad = $false
$before = (Get-Location).Path
$caught = $false
try { . $launch } catch {
    if ($_.Exception.Message -ne 'synthetic suite exception') { throw }
    $caught = $true
}
if ((Get-Location).Path -cne $before) { throw 'suite cwd was not restored' }
if ($caught -ne ($env:RG_CWD_OUTCOME -eq 'throw')) { throw 'incorrect exception result' }
if ($bad -ne ($env:RG_CWD_OUTCOME -eq '7')) { throw 'incorrect suite exit handling' }
}
`)
	// One PowerShell host covers all exit paths; the native input and helper
	// build probes only need to run once in the suite's working directory.
	cmd := exec.Command(pwsh, "-NoProfile", "-File", harness, script, repo, workspace)
	cmd.Dir = workspace
	cmd.Env = append(os.Environ(), "RG_CWD_TEST_EXE="+exe, "RG_CWD_WORKSPACE="+workspace, "GOWORK=off", na.AllowStaleModEnv+"=")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("hosted suite launch: %v\n%s", err, out)
	}
}

func TestWorkflowCWDProbe(t *testing.T) {
	workspace := os.Getenv("RG_CWD_WORKSPACE")
	if workspace == "" {
		t.Skip("subprocess probe only")
	}
	t.Run("SaveFrom", func(t *testing.T) {
		root := filepath.Join(workspace, "staged", os.Getenv("RG_CWD_OUTCOME"))
		if err := cases.StageSaves(cases.Save{Name: "checkpoint", From: cases.CommittedSaves()}, root); err != nil {
			t.Fatal(err)
		}
		got, err := os.ReadFile(filepath.Join(root, "profile", "Saves", "checkpoint.rws"))
		if err != nil || string(got) != "synthetic save" {
			t.Fatalf("staged save = %q, %v", got, err)
		}
	})
	t.Run("HelperBuild", func(t *testing.T) {
		cmd := exec.Command("go", "build", "-o", filepath.Join(workspace, "helper.exe"), "github.com/davidarcher/RimGovernor/go/internal/buildingruntime/cmd/buildingsmoke")
		if out, err := cmd.CombinedOutput(); err != nil {
			t.Fatalf("helper build: %v\n%s", err, out)
		}
	})
	t.Run("EnclosingCheckout", func(t *testing.T) {
		summary, err := na.RequireCurrentPackage(filepath.Join(workspace, "package"))
		if err != nil || summary["checked"] != true || summary["worktree"] != filepath.Join(workspace, "tested") {
			t.Fatalf("installed package = %#v, %v", summary, err)
		}
	})
}
