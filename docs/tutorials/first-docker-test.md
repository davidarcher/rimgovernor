# Your first Docker test

[Documentation](../README.md) · Tutorial

In this exercise you will run one complete Linux controller test suite and inspect its
retained result. You do not need RimWorld, GABS, LM Studio or a local project
environment. The image build also checks and builds the dashboard.

## Before you start

Use a checkout of RimGovernor, Python 3.12+ and a running Docker Engine or Docker Desktop
with Linux containers. The first build needs network access to image and package
registries. Open PowerShell in the repository root. If other work is active, use your
task's separate worktree.

## 1. Check the two host tools

```powershell
python --version
docker info --format '{{.OSType}}'
```

The first command should report Python 3.12 or newer. The second should print `linux`.
Resolve a missing executable or unavailable daemon before continuing; the [Docker
how-to](../how-to/docker-checks.md) covers executable-path alternatives.

## 2. Run one worker

Choose a new output directory. For this exercise, use the following name only if it does
not already exist; the runner creates it for you.

```powershell
python scripts/container_checks.py --workers 1 --image rimgovernor-checks:first-test --output .rimgovernor/first-docker-test
$LASTEXITCODE
```

The runner builds the image and runs the full Python suite in a private container. The
build may take several minutes. Its output goes to a build log; the runner prints the
result manifest after the worker finishes. A successful run returns exit code `0`.

## 3. Read the evidence

```powershell
Get-Content .rimgovernor/first-docker-test/result.json
Get-Content .rimgovernor/first-docker-test/0/pytest.log -Tail 20
```

In `result.json`, find `passed: true`, the immutable `image` ID and the single worker's
`exit_code`, `junit_present` and `cleanup_ok`. The pytest log gives the test summary,
including platform skips. Windows-only behavior is not tested by this Linux run.

If the image failed to build, there may be no result manifest. Read
`.rimgovernor/first-docker-test/build.log` and keep the failed output directory. After fixing
the reported cause, use a new directory for the next attempt.

## 4. Locate the retained artifacts

```powershell
Get-ChildItem .rimgovernor/first-docker-test/0
```

The directory contains `pytest.log`, `junit.xml` and `cleanup.log`. The runner removes
its test container; these host files remain for review. Keep generated evidence outside
commits.

You have now exercised the isolated test workflow and inspected what it recorded.
Continue with [testing and evidence](../explanation/testing.md) to understand its scope,
or use [native Docker acceptance](../how-to/docker-native.md) when you have licensed
Linux game inputs and want to exercise RimWorld too.
