"""Extract Zed's partial-theme defaults from the pinned Rust source."""
import colorsys
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = (root / 'crates/theme/src/default_colors.rs').read_text()
schema = (root / 'crates/settings_content/src/theme.rs').read_text()
scales = {}
for name, body in re.findall(r'pub\(crate\) fn (\w+)\(\) -> ColorScaleSet \{(.*?)\n\}', source, re.S):
    scales[name] = {mode: re.findall(r'"(#[0-9a-f]+)"', values) for mode, values in re.findall(r'(light|dark|light_alpha|dark_alpha): \[(.*?)\]', body, re.S)}
scales['neutral'] = scales['sand']
renames = dict((field, name) for name, field in re.findall(r'#\[serde\(rename = "([^"]+)"\)\]\s+pub (\w+):', schema))
constants = {}
for name, body in re.findall(r'const (\w+): Hsla = Hsla \{(.*?)\};', source, re.S):
    values = {key: eval(value, {'__builtins__': {}}, {}) for key, value in re.findall(r'(\w): ([^,]+),', body)}
    constants[name] = [*colorsys.hls_to_rgb(values['h'], values['l'], values['s']), values['a']]

def color(expression):
    expression = expression.strip()
    if expression == 'None':
        return None
    modifier = re.search(r'\.(alpha|opacity)\(([\d.]+)\)$', expression)
    if modifier:
        expression = expression[:modifier.start()]
    match = re.fullmatch(r'(\w+)\(\)\.(\w+)\(\)\.step_(\d+)\(\)', expression)
    if match:
        value = scales[match[1]][match[2]][int(match[3])-1].lstrip('#')
        channels = [int(value[i:i+2], 16)/255 for i in range(0, 8, 2)]
    elif expression in ('system.transparent', 'gpui::transparent_black()'):
        channels = [0, 0, 0, 0]
    else:
        channels = constants[expression][:]
    if modifier:
        channels[3] = float(modifier[2]) * (channels[3] if modifier[1] == 'opacity' else 1)
    return '#' + ''.join(f'{int(channel*255+0.5):02x}' for channel in channels)

result = {}
status = (root / 'crates/theme/src/styles/status.rs').read_text()
players = (root / 'crates/theme/src/styles/players.rs').read_text()
for mode in ('light', 'dark'):
    style = {}
    for text in (source, status):
        body = re.search(r'pub fn '+mode+r'\(\) -> Self \{.*?Self \{(.*?)\n        \}', text, re.S)[1]
        for field, expression in re.findall(r'^\s*(\w+): (.*),$', body, re.M):
            value = color(expression)
            if value is not None:
                style[renames.get(field, field.replace('_', '.'))] = value
    body = re.search(r'pub fn '+mode+r'\(\) -> Self \{(.*?)\n    \}', players, re.S)[1]
    style['players'] = [{field: color(expression) for field, expression in re.findall(r'(\w+): ([^,]+),', player)} for player in re.findall(r'PlayerColor \{(.*?)\}', body, re.S)]
    style['syntax'] = {}
    result[mode] = style
output = root / 'zyber/src/Zyber/ThemeDefaults.json'
output.write_text(json.dumps(result, indent=2)+'\n')
print(f'Generated {output.name}: {len(result["dark"])} defaults per appearance')
