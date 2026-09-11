# First Docker check

[Testing](README.md)

Requires a checkout, Python 3.12+ and Linux Docker. The first build needs network
access to package and image registries. Game files and a model server are unnecessary.
Run from the task worktree root:

```powershell
python --version
docker info --format '{{.OSType}}'
python scripts/container_checks.py --workers 1 --image rimgovernor-checks:first-test --output .rimgovernor/first-docker-test
$LASTEXITCODE
Get-Content .rimgovernor/first-docker-test/result.json
Get-Content .rimgovernor/first-docker-test/0/pytest.log -Tail 20
```

Docker must report `linux`. Choose a new output directory; the runner creates it.
The image build checks the dashboard, then one worker runs the full controller suite.

Success means exit code `0` and `passed: true` in `result.json`. Inspect worker
exit status, JUnit presence, cleanup and platform skips. If no manifest exists,
inspect `build.log` and the command output. Retain failures and use a fresh directory
for the next run.

This checks controller fixtures and dashboard code. Native game outcomes need
[scenario assertions](native-scenarios.md). See [Docker checks](docker-checks.md)
for focused tests and [test selection](choose-tests.md) for other changes.
