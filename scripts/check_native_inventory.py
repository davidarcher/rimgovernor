"""Check repository-owned native exports against the shared domain inventory.

This is a source baseline, not SDK discovery or C# semantic analysis. Signatures
retain advertised variants; source hashes force review of implementation changes.
Installed RimBridgeServer/GABS exports require the separate discovery capture.
"""

from __future__ import annotations

import argparse
import copy
from dataclasses import asdict, dataclass
import hashlib
import json
from pathlib import Path
import re
import subprocess
from typing import Any
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
# Strings are single tokens so brackets and comments inside descriptions cannot
# terminate an attribute or parameter. Comments never contribute declarations.
TOKEN = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|[A-Za-z_]\w*|\S')


@dataclass(frozen=True)
class Token:
    value: str
    start: int
    end: int


@dataclass(frozen=True)
class Export:
    name: str
    path: str
    line: int
    declaration: str
    source_sha256: str


def tokens(text: str) -> list[Token]:
    return [Token(m[0], m.start(), m.end()) for m in TOKEN.finditer(text)
            if not m[0].startswith(("//", "/*"))]


def closing(stream: list[Token], start: int, left: str, right: str) -> int:
    depth = 0
    for index in range(start, len(stream)):
        value = stream[index].value
        depth += value == left
        depth -= value == right
        if depth == 0:
            return index
    raise ValueError(f"Unclosed {left} at {stream[start].start}")


def exports(text: str, path: str) -> list[Export]:
    stream = tokens(text)
    result: list[Export] = []
    constants = {stream[i + 2].value: json.loads(stream[i + 4].value)
                 for i in range(len(stream) - 4)
                 if [t.value for t in stream[i:i + 2]] == ["const", "string"]
                 and stream[i + 3].value == "=" and stream[i + 4].value.startswith('"')}
    for index in range(len(stream) - 3):
        if [t.value for t in stream[index:index + 3]] != ["[", "Tool", "("]:
            continue
        raw = stream[index + 3].value
        name = json.loads(raw) if raw.startswith('"') else constants.get(raw)
        if not isinstance(name, str) or not re.fullmatch(r"[a-z][a-z0-9_]*/[a-z][a-z0-9_]*", name):
            raise ValueError(f"{path}: unresolved tool name {raw}")
        after = closing(stream, index, "[", "]") + 1
        # ToolResponse attributes can follow Tool before the exported method.
        while stream[after].value == "[":
            after = closing(stream, after, "[", "]") + 1
        if stream[after].value != "public":
            raise ValueError(f"{path}: tool {name} is not a public method")
        opening = next(i for i in range(after, len(stream)) if stream[i].value == "(")
        end = closing(stream, opening, "(", ")")
        declaration = text[stream[index].start:stream[end].end]
        result.append(Export(name, path, text.count("\n", 0, stream[index].start) + 1,
                             declaration, hashlib.sha256(text.encode()).hexdigest()))
    return result


def tracked_sources(root: Path) -> list[str]:
    result = subprocess.run(["git", "ls-files", "-z", "--", "integrations", "scripts/fixtures"],
                            cwd=root, check=True, capture_output=True, text=True)
    return sorted(p for p in result.stdout.split("\0") if p.endswith((".cs", ".csproj")))


def source_baseline(root: Path = ROOT) -> dict[str, Any]:
    tracked = tracked_sources(root)
    sources = [p for p in tracked if p.endswith(".cs")]
    projects: list[dict[str, Any]] = []
    for relative in (p for p in tracked if p.endswith(".csproj")):
        project = root / relative
        tree = ET.parse(project).getroot()
        default = tree.findtext(".//EnableDefaultCompileItems") != "false"
        compiled = {p: "" for p in sources if default and (root / p).is_relative_to(project.parent)}
        exclusions: list[dict[str, Any]] = []
        for item in tree.findall(".//Compile"):
            pattern = item.get("Include") or item.get("Remove")
            if pattern is None:
                raise ValueError(f"Unsupported Compile entry in {relative}")
            matched = sorted(p.resolve().relative_to(root).as_posix()
                             for p in project.parent.glob(pattern) if p.is_file())
            matched = [p for p in matched if p in sources]
            condition = item.get("Condition", "")
            if item.get("Remove"):
                if condition:
                    raise ValueError(f"Conditional Compile Remove requires review: {relative}")
                exclusions.append({"pattern": pattern, "sources": matched})
                for path in matched:
                    compiled.pop(path, None)
            else:
                for path in matched:
                    compiled[path] = condition
        output = tree.findtext(".//OutputPath")
        references = [{"path": (project.parent / item.attrib["Include"]).resolve().relative_to(root).as_posix(),
                       "private": item.findtext("Private", "")}
                      for item in tree.findall(".//ProjectReference")]
        projects.append({"path": relative, "source_sha256": hashlib.sha256(project.read_text(encoding="utf-8-sig").encode()).hexdigest(),
                         "assembly": tree.findtext(".//AssemblyName"),
                         "target_framework": tree.findtext(".//TargetFramework"),
                         "output_directory": ((project.parent / output).resolve().relative_to(root).as_posix()
                                              if output else None),
                         "project_references": references,
                         "compiled_sources": [{"path": p, "condition": condition}
                                              for p, condition in sorted(compiled.items())],
                         "compile_exclusions": exclusions})
    found = [asdict(export) for p in sources
             for export in exports((root / p).read_text(encoding="utf-8-sig"), p)]
    names = [row["name"] for row in found]
    if len(names) != len(set(names)):
        raise ValueError("Duplicate repository tool names require explicit registration review")
    return {"projects": projects, "exports": sorted(found, key=lambda row: row["name"])}


def baseline_errors(saved: dict[str, Any], current: dict[str, Any]) -> list[str]:
    return [] if saved == current else ["Native source/compile baseline drift; inspect changes before refreshing"]


def runtime_index_errors(index: dict[str, Any], root: Path = ROOT) -> list[str]:
    """Keep the lexical navigation index attached to current, compiled sources."""
    errors: list[str] = []
    rows = index["rows"]
    paths = [row["source"] for row in rows]
    expected = {path for path in tracked_sources(root) if path.endswith(".cs")}
    if len(paths) != len(set(paths)) or set(paths) != expected:
        errors.append("Native runtime index must cover each tracked C# source exactly once")
    for row in rows:
        path = root / row["source"]
        if not path.is_file():
            errors.append(f"Native runtime index source missing: {row['source']}")
            continue
        text = path.read_text(encoding="utf-8-sig")
        if row["source_sha256"] != hashlib.sha256(text.encode()).hexdigest():
            errors.append(f"Native runtime index source changed: {row['source']}")
        role = ("fixture-only" if row["source"].startswith("scripts/fixtures/") else
                "production-runtime" if "/src/Runtime/" in row["source"] else "production-bridge")
        if row["compilation"] != role:
            errors.append(f"Native runtime index compilation differs: {row['source']}")
        for key in ("static_token_lines", "reflection_boundary_lines", "patch_registration_lines"):
            if any(type(line) is not int or not 1 <= line <= len(text.splitlines()) for line in row[key]):
                errors.append(f"Native runtime index invalid {key}: {row['source']}")
    return errors


def check(root: Path = ROOT) -> list[str]:
    manifest = json.loads((root / "contracts/domain-inventory.json").read_text(encoding="utf-8"))
    native = manifest["native_surface"]
    errors = baseline_errors(native["source_baseline"], source_baseline(root))
    errors.extend(runtime_index_errors(json.loads(
        (root / "contracts/native-runtime-source-index.json").read_text(encoding="utf-8")), root))
    if native["version"] != 1:
        errors.append("Unsupported native source baseline version")
    if not re.fullmatch(r"[0-9a-f]{40}", native["source_revision"]):
        errors.append("Native source revision must be a full commit ID")
    domain = {row["id"]: row for row in manifest["items"]}
    expected = {row["name"] for row in native["source_baseline"]["exports"]}
    rows = native["tools"]
    names = [row["name"] for row in rows]
    if len(names) != len(set(names)) or set(names) != expected:
        errors.append("Native ownership rows must cover each exported tool exactly once")
    declarations = {row["name"]: row for row in native["source_baseline"]["exports"]}
    compiled_paths = {source["path"] for project in native["source_baseline"]["projects"]
                      for source in project["compiled_sources"]}
    exclusions = {(project["path"], entry["pattern"])
                  for project in native["source_baseline"]["projects"]
                  for entry in project["compile_exclusions"]}
    notes = native["compile_exclusion_notes"]
    if len(notes) != len(exclusions) or {(n["project"], n["pattern"]) for n in notes} != exclusions:
        errors.append("Every compile exclusion requires exactly one ownership/rationale note")
    for note in notes:
        if not re.fullmatch(r"N01\.\d{2}", note["owner_chunk"]) or not note["reason"]:
            errors.append("Compile exclusion has no N01 owner/rationale")
    for row in rows:
        name = row["name"]
        reference = row["domain_item_id"]
        expected_reference = "native-tool:" + name
        if reference != (expected_reference if expected_reference in domain else None):
            errors.append(f"{name}: incorrect existing domain inventory link")
        if not re.fullmatch(r"G01\.\d{2}[a-z]?", row["schema_owner_chunk"]):
            errors.append(f"{name}: missing G01 schema owner")
        if not re.fullmatch(r"N01\.\d{2}", row["native_owner_chunk"]):
            errors.append(f"{name}: missing N01 owner")
        if row["status"] != "pending" or not row["uncovered_acceptance"]:
            errors.append(f"{name}: baseline must retain pending behavioral acceptance")
        if row["build_role"] not in {"production", "fixture", "unassigned-source"}:
            errors.append(f"{name}: invalid build role")
        if name in declarations:
            path = declarations[name]["path"]
            role = ("unassigned-source" if path not in compiled_paths else
                    "fixture" if path.startswith("scripts/fixtures/") else "production")
            if row["build_role"] != role:
                errors.append(f"{name}: build role differs from source/project membership")
        for key in ("fixtures", "native_scenarios"):
            if not isinstance(row[key], list):
                errors.append(f"{name}: explicit {key} list required")
            else:
                for path in row[key]:
                    if not (root / path).is_file():
                        errors.append(f"{name}: missing evidence candidate {path}")
    return errors


def self_test() -> None:
    sample = '''// [Tool("home/comment")]
public class Example {
const string ToolName = "home/example";
[Tool(ToolName, Description = "a ] ) string")]
[ToolResponse("result", "string", "text")]
public Task<object> Run([ToolParameter(Description = "a, b (c)")] string op = "a") => null;
}'''
    found = exports(sample, "example.cs")
    assert len(found) == 1 and found[0].name == "home/example"
    assert 'string op = "a")' in found[0].declaration
    assert found != exports(sample.replace('op = "a"', 'op = "b"'), "example.cs")
    try:
        exports(sample.replace("[Tool(ToolName,", "[Tool(Unknown,"), "example.cs")
    except ValueError:
        pass
    else:
        raise AssertionError("Unresolved discovery name accepted")
    baseline = source_baseline()
    missing_export = copy.deepcopy(baseline)
    missing_export["exports"].pop()
    assert baseline_errors(missing_export, baseline), "Missing export accepted"
    changed_signature = copy.deepcopy(baseline)
    changed_signature["exports"][0]["declaration"] += " changed"
    assert baseline_errors(changed_signature, baseline), "Changed signature accepted"
    changed_compile = copy.deepcopy(baseline)
    changed_compile["projects"][0]["compiled_sources"].pop()
    assert baseline_errors(changed_compile, baseline), "Missing compiled source accepted"
    changed_output = copy.deepcopy(baseline)
    changed_output["projects"][0]["output_directory"] += "/wrong"
    assert baseline_errors(changed_output, baseline), "Changed assembly destination accepted"
    missing_reference = copy.deepcopy(baseline)
    next(p for p in missing_reference["projects"] if p["project_references"])["project_references"].pop()
    assert baseline_errors(missing_reference, baseline), "Missing runtime assembly reference accepted"
    index = json.loads((ROOT / "contracts/native-runtime-source-index.json").read_text(encoding="utf-8"))
    missing_source = copy.deepcopy(index)
    missing_source["rows"].pop()
    assert runtime_index_errors(missing_source), "Missing indexed source accepted"
    changed_hash = copy.deepcopy(index)
    changed_hash["rows"][0]["source_sha256"] = "0" * 64
    assert runtime_index_errors(changed_hash), "Changed indexed source fingerprint accepted"
    wrong_role = copy.deepcopy(index)
    wrong_role["rows"][0]["compilation"] = "excluded"
    assert runtime_index_errors(wrong_role), "Obsolete indexed compilation role accepted"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check the baseline (default).")
    parser.add_argument("--self-test", action="store_true", help="Exercise declaration parsing refusals.")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    errors = check()
    for error in errors:
        print(error)
    print(f"N01 native surface: {len(errors)} errors")
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
