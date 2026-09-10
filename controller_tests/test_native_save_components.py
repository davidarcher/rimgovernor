from pathlib import Path
import re


def test_saved_game_components_are_available_before_bridge_extensions_load():
    source = Path(__file__).resolve().parents[1] / 'integrations/colony-bridge/src'
    late = [str(path.relative_to(source)) for path in source.rglob('*.cs')
            if 'identity' not in path.relative_to(source).parts
            and re.search(r'\bclass\s+\w+\s*:\s*GameComponent\b', path.read_text(encoding='utf-8'))]
    assert not late, f'Save components must be in the early-loaded identity assembly: {late}'
