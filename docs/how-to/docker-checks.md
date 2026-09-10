# Run Docker controller checks

[Documentation](../README.md)

Run Linux fixture tests without a game or model server. For a guided first run, use
[your first Docker test](../tutorials/first-docker-test.md).

Run commands from the repository root. Keep generated evidence outside commits and
preserve failed results.

## Check prerequisites

From the task worktree root, use host Python 3.12+ and a running Docker daemon in
Linux-container mode. The runner uses only the Python standard library on the host; it
installs project/test and dashboard dependencies in the image. The first build needs
access to base images and package registries. Check Docker first:

```powershell
python --version
docker info --format '{{.OSType}}'
```

The Docker result must be `linux`. Native runs additionally require `docker compose
version`. If `docker` is not on PATH, add Docker Desktop's `resources/bin` directory;
the Python runners also discover standard Windows Docker Desktop install locations.

If `python` is unavailable on PATH, substitute `py -3.12` or an absolute path to an
existing Python 3.12+ executable (for example the checkout's
`.venv\Scripts\python.exe`). A quoted executable path in PowerShell needs the call
operator: `& 'C:/path/to/python.exe' scripts/container_checks.py --help`.

## Run the suite

```powershell
python scripts/container_checks.py --workers 2 --image rimbot-checks:my-task --output .rimbot/docker-checks-01
Get-Content .rimbot/docker-checks-01/result.json
```

Do not create the output directory first: the runner creates it and refuses an existing
path. `--workers` accepts 1 through 8 (default 2). Each worker runs the **entire**
Python suite; this is isolation/repetition, not sharding. Use 1 for a single regression
run. `--timeout 600` is the default per-worker limit in seconds; the image build has a
separate 1,800-second limit. Dashboard typecheck, Vitest and build run in the image
build stage (which Docker may cache), not in each worker. Neither native DLL compilation
nor the separate schema-generation check runs here.

## Inspect results

Success requires process exit code 0 and `result.json` with `passed: true`, including
every worker's successful exit, JUnit presence and cleanup. Inspect:

| Artifact under the output directory | Purpose |
| --- | --- |
| `build.log` | Image/dependency/dashboard build output; absent with `--no-build`. |
| `result.json` | Immutable image ID, scope, timings and worker results. |
| `0/pytest.log`, `0/junit.xml` (and `1/`, etc.) | Test failures, counts and platform skips per worker. |
| `0/cleanup.log` (and peers) | Removal of only this invocation's named containers. |

A build or image-inspection failure can occur before `result.json` exists; inspect the
console and `build.log` rather than treating missing results as a pass. Preserve the
directory and choose a fresh name after fixing a failure.

## Repeat an unchanged image

To repeat an unchanged image, use `--image rimbot-checks:my-task --no-build --output
.rimbot/docker-checks-02`. Omit `--no-build` after source changes: source is copied into
the image, not mounted from the worktree. Keep image tags unique between concurrent
tasks.

## Run a focused test

For a focused test, the wrapper has no pytest-argument forwarding. Build the same test
target and invoke pytest directly instead:

```powershell
docker build -f containers/Dockerfile --target tests -t rimbot-checks:my-task .
docker run --rm --init rimbot-checks:my-task python -m pytest -q controller_tests/test_container_worker.py
```

This direct command reports to the terminal; it does not produce the wrapper's retained
result manifest or JUnit artifacts.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
