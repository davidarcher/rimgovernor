"""Package acceptance refusal checks; actual startup is exercised in the game."""
import importlib.util
from pathlib import Path
import sys

import pytest
from mcp.types import CallToolResult


SCRIPTS = Path(__file__).parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("native_package_acceptance", SCRIPTS / "native_package_acceptance.py")
    acceptance = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(acceptance)
finally:
    sys.path.pop(0)


def test_startup_requires_batch_patches_and_preserves_graphical_mode():
    batch = "[HeadlessRim] Bootstrap armed.\n[HeadlessRim] Headless mode active."
    acceptance.check_startup_log(batch, headless=True)
    acceptance.check_startup_log("Normal game startup", headless=False)
    for text, headless in ((batch, False), ("", True),
                           (batch + "[HeadlessRim] Bootstrap Error: missing target", True),
                           (batch + "[HeadlessRim] Post-Init Error: missing target", True)):
        with pytest.raises(AssertionError):
            acceptance.check_startup_log(text, headless=headless)


def test_package_requires_both_loader_assemblies_and_rejects_mixed_install(tmp_path):
    mod = tmp_path / "Mods/RimGovernor"
    for relative in ("About/About.xml", "Assemblies/RimGovernor.Runtime.dll",
                     "BridgeTools/RimGovernor/RimGovernor.Bridge.dll"):
        path = mod / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"<ModMetaData><packageId>{acceptance.PACKAGE_ID}</packageId></ModMetaData>" if relative.endswith("xml") else "test assembly", encoding="utf8")
    assert len(acceptance.package_files(tmp_path)) == 3
    (tmp_path / "Mods/RimGovernorHeadless").mkdir()
    with pytest.raises(AssertionError, match="Mixed package"):
        acceptance.package_files(tmp_path)


def test_protobuf_smoke_requires_successful_fixed_outcome():
    good = CallToolResult(content=[], structuredContent={"payload": '{"batch":{"results":[]}}'})
    assert acceptance.protobuf_outcome(good, "batch") == {"results": []}
    for body in ({"payload": '{"failure":{"code":"FAILURE_CODE_UNAVAILABLE"}}'},
                 {"payload": '{"batch":{},"failure":{}}'}, {"batch": {}},
                 {"payload": {"batch": {}}}, {"payload": '[]'}):
        with pytest.raises(AssertionError):
            acceptance.protobuf_outcome(CallToolResult(content=[], structuredContent=body), "batch")
    with pytest.raises(AssertionError, match="SDK refused"):
        acceptance.protobuf_outcome(CallToolResult(content=[], isError=True,
            structuredContent={"payload": '{"batch":{}}'}), "batch")
