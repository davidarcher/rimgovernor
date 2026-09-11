"""Exercise package staging without licensed assemblies or a C# compiler."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import subprocess

import pytest


ROOT = Path(__file__).resolve().parents[1]
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")
pytestmark = pytest.mark.skipif(POWERSHELL is None, reason="PowerShell is required for the Windows package builder")


@pytest.fixture
def build_tree(tmp_path: Path) -> Path:
    script = tmp_path / "scripts/build_native_mod.ps1"
    script.parent.mkdir()
    shutil.copyfile(ROOT / "scripts/build_native_mod.ps1", script)
    native = tmp_path / "integrations/rimgovernor-native"
    files = {
        "src/Bridge/RimGovernor.Bridge.csproj": "<Project />",
        "src/Runtime/Example.cs": "class Example {}",
        "About/About.xml": "<ModMetaData />",
        "Notices/headless/LICENSE": "GPL fixture",
        "Notices/headless/PROVENANCE.md": "headless provenance",
        "Notices/companion/PROVENANCE.md": "companion provenance",
        "README.md": "Build instructions",
        "src/Runtime/obj/old.dll": "stale build",
    }
    for relative, content in files.items():
        path = native / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    fixtures = tmp_path / "scripts/fixtures"
    fixtures.mkdir()
    (fixtures / "InstallFixture.cs").write_text("fixture source", encoding="utf-8")
    (fixtures / "GuardedConstructionFixture.cs").write_text("fixture source", encoding="utf-8")
    for relative in ("contracts/proto/example.proto", "contracts/generated/protobuf/csharp/Example.cs",
                     "tools/protobuf/README.md", "scripts/generate_protobuf.py"):
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("generator input", encoding="utf-8")
    (fixtures / "stale.dll").write_text("must not distribute", encoding="utf-8")
    (tmp_path / "THIRD_PARTY.md").write_text("third-party notices", encoding="utf-8")
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    for name in ("Assembly-CSharp.dll", "0Harmony.dll", "RimBridgeServer.Sdk.dll", "Newtonsoft.Json.dll"):
        (inputs / name).write_bytes(b"external input")
    # Only compiler emission is simulated. The production builder owns copying,
    # feature forwarding, manifests, fresh-output enforcement and source isolation.
    (tmp_path / "compiler.ps1").write_text(
        "if ($args[0] -eq '--version') { 'test-sdk'; exit 0 }\n"
        "$output = ($args | Where-Object { $_ -like '-p:OutputPath=*' }).Substring(14)\n"
        "New-Item -ItemType Directory -Path $output -Force | Out-Null\n"
        "[IO.File]::WriteAllText((Join-Path $output 'RimGovernor.Runtime.dll'), 'runtime')\n"
        "[IO.File]::WriteAllText((Join-Path $output 'RimGovernor.Bridge.dll'), ($args -join '\n'))\n"
        "[IO.File]::WriteAllText((Join-Path $output 'runtime-dependencies.tsv'), '')\n"
        "exit 0\n", encoding="utf-8")
    subprocess.run(["git", "-C", str(tmp_path), "init"], check=True, capture_output=True)
    return tmp_path


def build(tree: Path, output: str, fixture: str | None = None) -> subprocess.CompletedProcess[str]:
    assert POWERSHELL is not None
    args = [POWERSHELL, "-NoProfile", "-File", str(tree / "scripts/build_native_mod.ps1"),
            "-RimWorldManagedDir", str(tree / "inputs"),
            "-HarmonyAssembly", str(tree / "inputs/0Harmony.dll"),
            "-RimBridgeSdkDir", str(tree / "inputs"),
            "-DotNet", str(tree / "compiler.ps1"), "-OutputRoot", str(tree / output)]
    if fixture:
        args += ["-Fixture", fixture]
    return subprocess.run(args, text=True, capture_output=True, check=False)


@pytest.mark.parametrize("fixture_name", ["InstallFixture", "GuardedConstructionFixture"])
def test_production_and_fixture_have_separate_complete_packages(build_tree: Path, fixture_name: str) -> None:
    for folder, fixture in (("production", None), ("fixture", fixture_name)):
        result = build(build_tree, folder, fixture)
        assert result.returncode == 0, result.stdout + result.stderr
        package = build_tree / folder / "RimGovernor"
        manifest = json.loads((package / "native-manifest.json").read_text(encoding="utf-8-sig"))
        assert manifest["role"] == folder
        assert manifest["fixtures"] == ([fixture_name] if fixture else [])
        for row in manifest["files"]:
            assert hashlib.sha256((package / row["path"]).read_bytes()).hexdigest() == row["sha256"]
        assert (package / "Notices/headless/LICENSE").is_file()
        assert (package / "Source/scripts/build_native_mod.ps1").is_file()
        assert (package / "Source/integrations/rimgovernor-native/src/Runtime/Example.cs").is_file()
        assert not list((package / "Source").rglob("*.dll"))
        assert not list((package / "Source").rglob("obj"))
        assert sorted(path.name for path in package.rglob("*.dll")) == ["RimGovernor.Bridge.dll", "RimGovernor.Runtime.dll"]
        command = (package / "BridgeTools/RimGovernor/RimGovernor.Bridge.dll").read_text()
        assert (f"-p:{fixture_name}=true" in command) is bool(fixture)
        assert (package / "Source/contracts/proto/example.proto").is_file()


def test_existing_output_and_missing_notice_refuse(build_tree: Path) -> None:
    output = build_tree / "occupied"
    output.mkdir()
    sentinel = output / "keep.txt"
    sentinel.write_text("preserved")
    assert build(build_tree, "occupied").returncode != 0
    assert sentinel.read_text() == "preserved"
    (build_tree / "integrations/rimgovernor-native/Notices/headless/LICENSE").unlink()
    assert build(build_tree, "missing").returncode != 0
    assert not (build_tree / "missing").exists()
