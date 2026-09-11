"""Run the pinned official Go Protobuf generator and check its owned outputs."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
VERSION = "v1.36.11"
MODULE = "github.com/davidarcher/RimGovernor/go/internal/wire"


def run(protoc: Path, proto_root: Path, output: Path, check: bool, go: str) -> None:
    output.mkdir(parents=True, exist_ok=False)
    evidence: dict[str, object] = {"plugin_version": VERSION, "commands": []}
    env = dict(os.environ, GOTOOLCHAIN="local", GOWORK="off", GOBIN=str(output / "bin"),
               GOCACHE=str(output / "cache"), GOMODCACHE=str(output / "modcache"))

    def command(args: list[str], cwd: Path) -> str:
        result = subprocess.run(args, cwd=cwd, env=env, text=True, capture_output=True, timeout=600)
        evidence["commands"].append({"args": args, "cwd": str(cwd), "exit": result.returncode,
                                     "stdout": result.stdout, "stderr": result.stderr})
        if result.returncode:
            raise RuntimeError(f"Command failed ({result.returncode}): {args}\n{result.stderr}")
        return result.stdout.strip()

    try:
        evidence["protoc_sha256"] = hashlib.sha256(protoc.read_bytes()).hexdigest()
        if command([str(protoc), "--version"], output) != "libprotoc 30.0":
            raise ValueError("Expected official Grpc.Tools 2.72.0 protoc (libprotoc 30.0)")
        evidence["go_version"] = command([go, "version"], output)
        command([go, "install", "google.golang.org/protobuf/cmd/protoc-gen-go@" + VERSION], output)
        plugin = output / "bin" / ("protoc-gen-go.exe" if os.name == "nt" else "protoc-gen-go")
        if command([str(plugin), "--version"], output).split()[-1] != VERSION:
            raise ValueError("Unexpected protoc-gen-go version")
        evidence["plugin_sha256"] = hashlib.sha256(plugin.read_bytes()).hexdigest()
        schemas = sorted(proto_root.rglob("*.proto"))
        if not schemas:
            raise ValueError("No canonical proto inputs")
        inputs = output / "proto"
        inputs.mkdir()
        hashes = {}
        for source in schemas:
            relative = source.relative_to(proto_root)
            target = inputs / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            hashes[relative.as_posix()] = hashlib.sha256(target.read_bytes()).hexdigest()
        evidence["inputs"] = hashes
        generated = output / "generated"
        generated.mkdir()
        command([str(protoc), "--proto_path=" + str(inputs), "--plugin=protoc-gen-go=" + str(plugin),
                 "--go_out=" + str(generated), "--go_opt=module=" + MODULE,
                 *sorted(hashes)], output)
        destination = ROOT / "contracts/generated/protobuf/go"
        for name in ("go.mod", "go.sum"):
            shutil.copyfile(destination / name, generated / name)
        if command([go, "list", "-m", "-mod=readonly", "-f", "{{.Version}}", "google.golang.org/protobuf"], generated) != VERSION:
            raise ValueError("Generated wire runtime must match pinned protobuf version")
        command([go, "test", "-mod=readonly", "./..."], generated)
        command([go, "vet", "-mod=readonly", "./..."], generated)
        command([go, "mod", "verify"], generated)
        current = {p.relative_to(destination): p for p in destination.rglob("*.pb.go")}
        expected = {p.relative_to(generated): p for p in generated.rglob("*.pb.go")}
        changed = sorted(str(p) for p in set(current) ^ set(expected) | {
            p for p in set(current) & set(expected)
            if current[p].read_text(encoding="utf-8") != expected[p].read_text(encoding="utf-8")})
        evidence["outputs"] = {p.as_posix(): hashlib.sha256(source.read_bytes()).hexdigest() for p, source in expected.items()}
        evidence["differences"] = changed
        if check and changed:
            raise ValueError("Official generated Go drift: " + ", ".join(changed))
        if not check:
            for relative, source in expected.items():
                target = destination / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
            for relative in set(current) - set(expected):
                current[relative].unlink()
        print(f"Official protobuf Go {VERSION}: {len(expected)} files {'checked' if check else 'generated'}; {output}")
    finally:
        (output / "result.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--protoc", type=Path, required=True, help="Official Grpc.Tools 2.72.0 compiler for this OS/CPU")
    parser.add_argument("--proto-root", type=Path, default=ROOT / "contracts/proto")
    parser.add_argument("--output", type=Path, required=True, help="Fresh private artifact directory")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--go", default="go", help="Pinned repository Go executable")
    args = parser.parse_args()
    run(args.protoc.resolve(), args.proto_root.resolve(), args.output.resolve(), args.check, args.go)
