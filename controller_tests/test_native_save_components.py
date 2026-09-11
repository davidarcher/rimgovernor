from pathlib import Path
import re


def test_saved_game_components_are_available_before_bridge_extensions_load():
    source = Path(__file__).resolve().parents[1] / 'integrations/rimgovernor-native/src'
    component = re.compile(r'\bclass\s+\w+\s*:\s*(?:Game|Map|World)Component\b')
    early = [path for path in (source / 'Runtime/Persistence').glob('*.cs')
             if component.search(path.read_text(encoding='utf-8'))]
    assert early, 'Early-loaded native persistence sources must be present'
    bridge = source / 'Bridge'
    assert bridge.is_dir(), 'SDK-discovered Bridge sources must be present'
    late = [str(path.relative_to(source)) for path in bridge.rglob('*.cs')
            if component.search(path.read_text(encoding='utf-8'))]
    assert not late, f'Save components must be in the early-loaded Runtime assembly: {late}'
