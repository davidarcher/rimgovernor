"""Prepare an isolated eight-tribal profile after installing RimBridgeServer.

Does not edit the normal game profile, download software, or install mods.
"""
import argparse
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path
from rimgovernor.headless import NATIVE_PACKAGE, require_native_package

def prepare(source: Path, game: Path, root: Path, observations: bool = False):
    source, game, root = source.resolve(), game.resolve(), root.resolve()
    profile = root / "profile"
    if source == profile or source in profile.parents or profile in source.parents:
        raise ValueError("Trial must be separate from the normal profile")
    executable = game / "RimWorldWin64.exe"
    if not executable.is_file() or not (game / "Mods/RimBridgeServer/About/About.xml").is_file():
        raise ValueError("Install RimBridgeServer in the specified RimWorld Mods folder first")
    save = source / "Saves/RimGovernor-tribal8-baseline.rws"
    original = save.read_bytes()
    for directory in [profile / "Config", profile / "Saves", root / "config"]:
        directory.mkdir(parents=True, exist_ok=True)
    mods = ET.parse(source / "Config/ModsConfig.xml")
    active = mods.getroot().find("activeMods")
    # Isolate the replacement from every controller mod and optional workshop mod.
    for item in list(active):
        if item.text != "brrainz.harmony" and not (item.text or "").startswith("ludeon.rimworld"):
            active.remove(item)
    ET.SubElement(active, "li").text = "brrainz.rimbridgeserver"
    if observations:
        require_native_package(game/'Mods')
        ET.SubElement(active, "li").text = NATIVE_PACKAGE
    mods.write(profile / "Config/ModsConfig.xml", encoding="utf8", xml_declaration=True)
    prefs = ET.parse(source / "Config/Prefs.xml")
    for name, value in [("runInBackground", "True"), ("devMode", "False")]:
        element = prefs.getroot().find(name)
        if element is None:
            element = ET.SubElement(prefs.getroot(), name)
        element.text = value
    prefs.write(profile / "Config/Prefs.xml", encoding="utf8", xml_declaration=True)
    shutil.copy2(save, profile / "Saves" / save.name)
    config = {"version": "1.0", "games": {"rimgovernor-trial": {
        "id": "rimgovernor-trial", "name": "RimGovernor bridge trial", "launchMode": "DirectPath",
        "target": str(executable), "workingDir": str(game),
        "args": ["-savedatafolder=" + str(profile), "-logFile", str(root / "Player.log")]}}}
    (root / "config/config.json").write_text(json.dumps(config, indent=2), encoding="utf8")
    (root / "fixture.json").write_text(json.dumps({"source_sha256": hashlib.sha256(original).hexdigest(), "profile": str(profile)}, indent=2), encoding="utf8")
    print(f"Prepared {profile}; normal profile untouched.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-profile", type=Path, required=True)
    parser.add_argument("--rimworld", type=Path, required=True)
    parser.add_argument("--root", type=Path, default=Path(".rimgovernor/bridge"))
    parser.add_argument("--observations", action="store_true")
    args = parser.parse_args()
    prepare(args.source_profile, args.rimworld, args.root, args.observations)
