"""Prepare an isolated eight-tribal profile after installing RimBridgeServer.

Does not edit the normal game profile, download software, or install mods.
"""
import argparse
import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path

BASELINE_SHA256 = "e9402197cb5c7213367c7f6a2a6121294eb419d964f46ded256dc7a1da65388c"


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
    if hashlib.sha256(original).hexdigest() != BASELINE_SHA256:
        raise ValueError("Baseline differs from the checkpointed eight-tribal fixture")
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
        if not (game / "Mods/RimGovernorObservations/About/About.xml").is_file():
            raise ValueError("Install the built observation companion before enabling it")
        ET.SubElement(active, "li").text = "davidarcher.rimgovernor.observations"
    mods.write(profile / "Config/ModsConfig.xml", encoding="utf8", xml_declaration=True)
    prefs = ET.parse(source / "Config/Prefs.xml")
    for name, value in [("runInBackground", "True"), ("devMode", "False")]:
        element = prefs.getroot().find(name)
        if element is None:
            element = ET.SubElement(prefs.getroot(), name)
        element.text = value
    prefs.write(profile / "Config/Prefs.xml", encoding="utf8", xml_declaration=True)
    tree = ET.fromstring(original)
    removed = []
    for parent in tree.iter():
        for child in list(parent):
            if child.get("Class") == "RIMAPI.RIMAPI_GameComponent":
                parent.remove(child)
                removed.append("RIMAPI.RIMAPI_GameComponent")
    for tag in ["modIds", "modNames"]:
        parent = tree.find("meta/" + tag)
        if parent is not None:
            for child in list(parent):
                if "rimapi" in (child.text or "").lower():
                    parent.remove(child)
    ET.ElementTree(tree).write(profile / "Saves" / save.name, encoding="utf8", xml_declaration=True)
    config = {"version": "1.0", "games": {"rimgovernor-trial": {
        "id": "rimgovernor-trial", "name": "RimGovernor bridge trial", "launchMode": "DirectPath",
        "target": str(executable), "workingDir": str(game),
        "args": ["-savedatafolder=" + str(profile), "-logFile", str(root / "Player.log")]}}}
    (root / "config/config.json").write_text(json.dumps(config, indent=2), encoding="utf8")
    (root / "fixture.json").write_text(json.dumps({"source_sha256": BASELINE_SHA256,
        "removed_components": removed, "profile": str(profile)}, indent=2), encoding="utf8")
    print(f"Prepared {profile}; normal profile untouched.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-profile", type=Path, required=True)
    parser.add_argument("--rimworld", type=Path, required=True)
    parser.add_argument("--root", type=Path, default=Path(".rimgovernor/bridge"))
    parser.add_argument("--observations", action="store_true")
    args = parser.parse_args()
    prepare(args.source_profile, args.rimworld, args.root, args.observations)
