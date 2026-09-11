"""Fixture checks for native capture refusals; no native outcome claims."""
import importlib.util
import json
from pathlib import Path
import sys
from unittest.mock import AsyncMock

from mcp.types import CallToolResult, TextContent
import pytest

from rimgovernor.bridge import BridgeError


SPEC = importlib.util.spec_from_file_location("native_compatibility_acceptance", Path(__file__).parents[1] / "scripts/native_compatibility_acceptance.py")
capture = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = capture
SPEC.loader.exec_module(capture)


def result(data, *, error=False):
    return CallToolResult(content=[], structuredContent=data, isError=error)


def test_missing_duplicate_and_fixture_leak_refuse():
    production, fixtures = {"home/a", "home/b"}, {"test/setup"}
    capture.validate_discovery(["home/a", "home/b", "rimworld/load_game_ready"], production, fixtures, set())
    for names, message in [(["home/a"], "Missing production"),
                           (["home/a", "home/b", "home/a"], "Duplicate"),
                           (["home/a", "home/b", "test/setup"], "Unexpected fixture"),
                           (["home/a", "home/b", "test/unlisted"], "Unexpected fixture"),
                           (["home/a", "home/b", "home/debug_fixture"], "Unexpected fixture")]:
        with pytest.raises(AssertionError, match=message):
            capture.validate_discovery(names, production, fixtures, set())
    capture.validate_discovery(["home/a", "home/b", "test/setup"], production, fixtures, {"test/setup"})
    with pytest.raises(AssertionError, match="Missing expected"):
        capture.validate_discovery(["home/a", "home/b"], production, fixtures, {"test/setup"})


@pytest.mark.parametrize("page", [{}, {"tools": ["home/a"]}, {"tools": [{"name": "home/a"}]},
                                  {"tools": [], "nextCursor": 7}])
def test_malformed_discovery_refuses(page):
    with pytest.raises(AssertionError):
        capture.page_names(page)


async def test_pagination_records_every_page_and_detects_duplicate_across_pages(tmp_path):
    bridge = AsyncMock()
    bridge.names.side_effect = [result({"tools": [{"gabpName": "home/a"}], "nextCursor": "next"}),
                                result({"tools": [{"gabpName": "home/a"}], "nextCursor": None})]
    names = await capture.discovery(bridge, capture.Evidence(tmp_path))
    assert names == ["home/a", "home/a"]
    assert bridge.names.call_args_list[1].kwargs == {"cursor": "next"}
    assert len(list(tmp_path.glob("*.json"))) == 2
    with pytest.raises(AssertionError, match="Duplicate"):
        capture.validate_discovery(names, {"home/a"}, set(), set())


async def test_repeated_cursor_and_page_bound_refuse(tmp_path):
    bridge = AsyncMock()
    bridge.names.return_value = result({"tools": [], "nextCursor": "same"})
    with pytest.raises(AssertionError, match="repeated"):
        await capture.discovery(bridge, capture.Evidence(tmp_path))
    assert bridge.names.await_count == 2
    with pytest.raises(AssertionError, match="page limit"):
        await capture.discovery(bridge, capture.Evidence(tmp_path), max_pages=1)


async def test_failed_sdk_reply_is_retained_before_raise(tmp_path):
    rejected = result({"success": False, "error": "native refusal"}, error=True)
    operation = AsyncMock(side_effect=BridgeError("games_call_tool", rejected))
    with pytest.raises(BridgeError):
        await capture.Evidence(tmp_path).record("failure", {"tool": "home/a"}, operation())
    saved = json.loads(next(tmp_path.glob("*.json")).read_text())
    assert saved["result"]["structuredContent"]["error"] == "native refusal"
    assert "BridgeError" in saved["error"]


async def test_assertion_failure_is_retained(tmp_path):
    operation = AsyncMock(side_effect=AssertionError("deliberate native capture failure"))
    with pytest.raises(AssertionError, match="deliberate"):
        await capture.Evidence(tmp_path).record("failure", {}, operation())
    assert "deliberate native capture failure" in json.loads(next(tmp_path.glob("*.json")).read_text())["error"]


def test_legacy_unknown_key_is_not_mistaken_for_validation():
    observed = capture.binder_observation(result({"success": True, "unknownArguments": []}))
    assert observed["strict_unknown_key_validation_proven"] is False
    assert "may-drop" in observed["outcome"]
    reported = capture.binder_observation(result({"success": True, "unknownArguments": [capture.UNKNOWN_KEY]}))
    assert reported["outcome"] == "unknown-key-reported"
    assert reported["strict_unknown_key_validation_proven"] is False
    rejected = capture.binder_observation(CallToolResult(content=[TextContent(type="text", text="Unknown argument")], isError=True))
    assert rejected["outcome"] == "request-refused-inspect-raw-error"
    assert rejected["strict_unknown_key_validation_proven"] is False


def test_reload_checks_identity_token_tick_and_pause():
    before = {"colonyId": "colony", "loadToken": "old", "mapId": 0, "tick": 100}
    after = dict(before, loadToken="new")
    clock = {"ticksGame": 100, "paused": True}
    capture.verify_reload(before, after, clock)
    capture.verify_reload(before, dict(after, tick=101), dict(clock, ticksGame=101))
    for changed, changed_clock in [(dict(after, colonyId="other"), clock),
                                    (before, clock), (dict(after, loadToken=None), clock),
                                    (dict(after, tick=102), dict(clock, ticksGame=102)),
                                    (after, dict(clock, paused=False)),
                                    (dict(after, mapId=1), clock), (after, dict(clock, ticksGame=101))]:
        with pytest.raises(AssertionError):
            capture.verify_reload(before, changed, changed_clock)


def save_xml(path, *, duplicate_game=False, duplicate_map=False, missing=False):
    games = sorted(capture.GAME_COMPONENTS)
    if duplicate_game:
        games.append(games[0])
    if missing:
        games.pop()
    game = "".join(f'<li Class="{name}" />' for name in games)
    maps = f'<li><components><li Class="{capture.MAP_COMPONENT}" /></components></li>'
    if duplicate_map:
        maps = f'<li><components><li Class="{capture.MAP_COMPONENT}" /><li Class="{capture.MAP_COMPONENT}" /></components></li>'
    path.write_text(f'<savegame><game><components>{game}</components><maps>{maps}{maps}</maps></game></savegame>')


def test_component_census_scopes_maps_and_refuses_missing_or_duplicate_native_components(tmp_path):
    path = tmp_path / "game.rws"
    save_xml(path)
    census = capture.component_census(path)
    assert len(census["game"]) == 8
    assert census["map-0"] == census["map-1"] == {capture.MAP_COMPONENT: 1}
    for options in ({"duplicate_game": True}, {"duplicate_map": True}, {"missing": True}):
        save_xml(path, **options)
        with pytest.raises(AssertionError):
            capture.component_census(path)
