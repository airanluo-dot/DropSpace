#!/usr/bin/env python3
"""Import the pinned offline catalog without editing Windows translations."""
from pathlib import Path
import json,xml.etree.ElementTree as ET,subprocess
root=Path(__file__).resolve().parents[2]
base='1cd4620dd58a67b0963b73487743137813b8864a'
langs=['en-US','zh-CN','zh-TW','ja-JP','ko-KR','de-DE','fr-FR','es-ES','pt-BR','ru-RU']
strings={}
for lang in langs:
    data=subprocess.check_output(['git','-C',str(root),'show',f'{base}:src/DropSpace.App/Strings/{lang}/Resources.resw'])
    for node in ET.fromstring(data).findall('data'):
        key=node.get('name'); value=node.findtext('value')
        strings.setdefault(key,{'localizations':{}})['localizations'][lang]={'stringUnit':{'state':'translated','value':value}}
# Explicit platform wording only; resource identity and the ten source languages
# stay fixed. Do not globally replace Windows in baseline/release descriptions.
for key in ['IslandAppearanceFollow']:
    for lang in langs:
        unit = strings[key]['localizations'][lang]['stringUnit']
        unit['value'] = unit['value'].replace('Windows', 'macOS')
# Reviewed native wording is kept outside the frozen Windows resources so a
# repeat import cannot restore Windows-only product claims in the Mac interface.
for key, translations in json.loads((root/'macos/localization/platform-overrides.json').read_text()).items():
    if key not in strings or set(translations) != set(langs) or any(not value.strip() for value in translations.values()):
        raise ValueError(f'Invalid native localization override: {key}')
    for lang, value in translations.items():
        strings[key]['localizations'][lang]['stringUnit']['value'] = value
native = json.loads((root/'macos/localization/native-additions.json').read_text())
for key, translations in native.items():
    if key in strings or not key.startswith('Mac.') or set(translations) != set(langs) or any(not value.strip() for value in translations.values()):
        raise ValueError(f'Invalid native localization addition: {key}')
    strings[key] = {'localizations': {lang: {'stringUnit': {'state': 'translated', 'value': value}} for lang, value in translations.items()}}
path=root/'macos/Sources/DropSpaceCore/Resources/Localizable.xcstrings'
path.write_text(json.dumps({'sourceLanguage':'en-US','strings':strings,'version':'1.0'},ensure_ascii=False,indent=2)+'\n')
print(f'Imported {len(strings) - len(native)} Windows resource identities and {len(native)} native additions in {len(langs)} languages. Platform-specific copy still requires review.')
