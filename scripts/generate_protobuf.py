"""Generate canonical Protobuf C# with pinned official protoc; optionally prove net472.

This orchestrates NuGet, protoc and dotnet. It does not interpret schemas or emit
language source. Outputs and native runtime behavior belong to official Protobuf.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import platform
import shutil
import subprocess
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_PROTOC = "libprotoc 30.0"


def command(args: list[str], cwd: Path, *, capture: bool = False) -> str:
    result = subprocess.run(args, cwd=cwd, check=True, text=True,
                            capture_output=capture, timeout=600)
    return result.stdout.strip() if capture else ""


def package_version(project: Path, name: str) -> str:
    for item in ET.parse(project).getroot().findall(".//PackageReference"):
        if item.get("Include") == name:
            return item.attrib["Version"].strip("[]")
    raise ValueError(f"Pinned package missing: {name}")


def protoc_platform() -> str:
    machine = platform.machine().lower()
    if os.name == "nt" and machine in ("amd64", "x86_64"):
        return "windows_x64"
    if os.name == "nt" and machine in ("x86", "i386", "i686"):
        return "windows_x86"
    if platform.system() == "Darwin" and machine in ("amd64", "x86_64"):
        return "macosx_x64"
    if platform.system() == "Linux" and machine in ("aarch64", "arm64"):
        return "linux_arm64"
    if platform.system() == "Linux" and machine in ("amd64", "x86_64"):
        return "linux_x64"
    raise ValueError(f"Unsupported protoc platform: {platform.system()} {machine}")


def run(root: Path, dotnet: str, output: Path, *, check: bool, proof: bool, cross_language_inputs: Path | None = None) -> None:
    output.mkdir(parents=True, exist_ok=False)
    private = output / "source"
    project_relative = Path("tools/protobuf/ProtobufProof.csproj")
    for relative in (project_relative, Path("tools/protobuf/Program.cs"),
                     Path("tools/protobuf/packages.lock.json")):
        target = private / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / relative, target)
    project = private / project_relative
    # Restore/build state stays in this run's private tree; the committed lock
    # captures both direct versions and the runtime dependency closure.
    command([dotnet, "restore", str(project), "--locked-mode", "--source", "https://api.nuget.org/v3/index.json"], private)
    packages = Path(command([dotnet, "msbuild", str(project), "-getProperty:NuGetPackageRoot"], private, capture=True))
    grpc = packages / "grpc.tools" / package_version(project, "Grpc.Tools")
    compiler = grpc / "tools" / protoc_platform() / ("protoc.exe" if os.name == "nt" else "protoc")
    version = command([str(compiler), "--version"], private, capture=True)
    if version != EXPECTED_PROTOC:
        raise ValueError(f"Unexpected official compiler: {version}; expected {EXPECTED_PROTOC}")
    generated = private / "contracts/generated/protobuf/csharp"
    generated.mkdir(parents=True)
    proto = private / "contracts/proto"
    shutil.copytree(root / "contracts/proto", proto)
    schemas = sorted(path.relative_to(proto).as_posix() for path in proto.rglob("*.proto"))
    if not schemas:
        raise ValueError("No canonical .proto files")
    command([str(compiler), f"--proto_path={proto}",
             f"--proto_path={grpc / 'build/native/include'}", f"--csharp_out={generated}",
             f"--descriptor_set_out={output / 'contracts.pb'}", "--include_imports", *schemas], private)
    destination = root / "contracts/generated/protobuf/csharp"
    current = {path.name: path for path in destination.glob("*.cs")}
    expected = {path.name: path for path in generated.glob("*.cs")}
    differences = sorted(set(current) ^ set(expected) | {
        name for name in set(current) & set(expected)
        if current[name].read_text(encoding="utf8") != expected[name].read_text(encoding="utf8")})
    if check and differences:
        raise ValueError("Official generated C# drift: " + ", ".join(differences))
    if not check:
        destination.mkdir(parents=True, exist_ok=True)
        for name, path in expected.items():
            shutil.copyfile(path, destination / name)
        for name in set(current) - set(expected):
            current[name].unlink()
    if proof:
        command([dotnet, "build", str(project), "--no-restore", "-c", "Release"], private)
        executable = project.parent / "bin/Release/net472/ProtobufProof.exe"
        runner = [str(executable)] if os.name == "nt" else ["mono", str(executable)]
        command([*runner, str(output / "roundtrip"),
                 *([str(cross_language_inputs.resolve())] if cross_language_inputs else [])], private)
    print(f"{version}: {len(schemas)} schemas, {len(expected)} C# files; {'checked' if check else 'generated'}. Artifacts: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path, help="Fresh private restore/build artifact directory")
    parser.add_argument("--check", action="store_true", help="Fail on generated drift without changing checked-in files")
    parser.add_argument("--proof", action="store_true", help="Compile and run net472 round trips; requires .NET Framework on Windows or Mono elsewhere")
    parser.add_argument("--cross-language-inputs", type=Path, help="Directory containing go-request/go-reply .json and .bin proof artifacts")
    args = parser.parse_args()
    if args.cross_language_inputs and not args.proof:
        parser.error("--cross-language-inputs requires --proof")
    run(ROOT, args.dotnet, (args.output or ROOT / ".rimgovernor" / ("protobuf-" + uuid.uuid4().hex)).resolve(),
        check=args.check, proof=args.proof, cross_language_inputs=args.cross_language_inputs)
