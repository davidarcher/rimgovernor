import hashlib
import importlib.util
import json
from pathlib import Path


def test_trial_uses_process_identity_without_executable_name_fallback(tmp_path, monkeypatch):
    script=Path(__file__).resolve().parents[1]/'scripts/prepare_bridge_trial.py'
    spec=importlib.util.spec_from_file_location('prepare_bridge_trial',script)
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    source=tmp_path/'normal';game=tmp_path/'game';root=tmp_path/'trial'
    (source/'Config').mkdir(parents=True)
    (source/'Saves').mkdir()
    (game/'Mods/RimBridgeServer/About').mkdir(parents=True)
    (game/'RimWorldWin64.exe').touch()
    (game/'Mods/RimBridgeServer/About/About.xml').touch()
    (source/'Config/ModsConfig.xml').write_text(
        '<ModsConfigData><activeMods><li>brrainz.harmony</li></activeMods></ModsConfigData>')
    (source/'Config/Prefs.xml').write_text('<Prefs/>')
    baseline=b'<savegame><meta><modIds/><modNames/></meta></savegame>'
    save=source/'Saves/RimBot-tribal8-baseline.rws'
    save.write_bytes(baseline)
    monkeypatch.setattr(module,'BASELINE_SHA256',hashlib.sha256(baseline).hexdigest())

    module.prepare(source,game,root)

    config=json.loads((root/'config/config.json').read_text())['games']['rimbot-trial']
    assert config['launchMode']=='DirectPath'
    assert config['target']==str(game/'RimWorldWin64.exe')
    assert 'stopProcessName' not in config
    assert config['args'][0]=='-savedatafolder='+str(root/'profile')
    assert save.read_bytes()==baseline
