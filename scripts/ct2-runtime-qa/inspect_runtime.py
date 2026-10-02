"""Offline evidence only: inputs/licenses/closure/PE imports and a native-wheel import probe."""
import argparse, email.parser, hashlib, importlib.metadata, json, pathlib, sys, zipfile

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def dump(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('inputs', type=pathlib.Path)
    parser.add_argument('runtime', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    lock = json.loads((args.inputs / 'dependencies.json').read_text(encoding='utf-8'))
    source = json.loads((args.inputs / 'provenance.json').read_text(encoding='utf-8'))
    # Only after the reviewed builder has validated and installed the exact wheels.
    from packaging.markers import default_environment
    from packaging.requirements import Requirement
    from packaging.utils import canonicalize_name
    from packaging.version import Version
    import pefile
    selected = {p['name']: p['version'] for p in lock['packages']}
    environment = default_environment() | {'sys_platform': 'win32', 'os_name': 'nt', 'platform_system': 'Windows',
        'platform_machine': 'AMD64', 'python_version': '3.12', 'python_full_version': lock['python'], 'extra': ''}
    requirements = []
    licenses = []
    for package in lock['packages']:
        path = args.inputs / 'wheelhouse' / package['file']
        if path.stat().st_size != package['bytes'] or digest(path) != package['sha256']:
            raise ValueError('Wheel input changed after validation')
        with zipfile.ZipFile(path) as archive:
            metadata = email.parser.BytesParser().parsebytes(archive.read(next(n for n in archive.namelist() if n.count('/') == 1 and n.endswith('.dist-info/METADATA'))))
            for raw in metadata.get_all('Requires-Dist', []):
                requirement = Requirement(raw)
                if requirement.marker is None or requirement.marker.evaluate(environment):
                    name = canonicalize_name(requirement.name)
                    if requirement.url or requirement.extras or name not in selected or Version(selected[name]) not in requirement.specifier:
                        raise ValueError('Dependency closure missing or inconsistent: ' + raw)
                    requirements.append({'from': package['name'], 'requirement': raw, 'selected': selected[name]})
            record = next(p for p in source['packages'] if p['name'] == package['name'])
            for item in record['wheelLicenses']:
                # Builder already rejects unsafe raw ZIP names. Do not call extract/extractall.
                data = archive.read(item['path'])
                if len(data) != item['bytes'] or hashlib.sha256(data).hexdigest() != item['sha256']:
                    raise ValueError('License evidence changed')
                filename = package['name'] + '-' + hashlib.sha256(item['path'].encode()).hexdigest()[:16] + '.txt'
                (args.output / 'licenses' / filename).write_bytes(data)
                licenses.append(item | {'package': package['name'], 'evidenceFile': 'licenses/' + filename})
    dump(args.output / 'dependency-closure.json', {'target': environment, 'requirements': requirements, 'distributions': selected})
    dump(args.output / 'wheel-licenses.json', {'licenses': licenses, 'redistributionStatus': source['redistributionStatus']})
    inventory = json.loads((args.runtime / 'engine-files.json').read_text())['files']
    imports = []
    for item in inventory:
        path = args.runtime / 'engine' / item['path']
        if digest(path) != item['sha256'] or path.stat().st_size != item['bytes']:
            raise ValueError('Runtime inventory mismatch')
        if path.suffix.lower() not in ('.exe', '.dll', '.pyd'):
            continue
        pe = pefile.PE(str(path), fast_load=True)
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_IMPORT'], pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT']])
        imports.append({'path': item['path'], 'machine': hex(pe.FILE_HEADER.Machine), 'subsystem': pe.OPTIONAL_HEADER.Subsystem,
            'imports': sorted({entry.dll.decode('ascii') for key in ('DIRECTORY_ENTRY_IMPORT','DIRECTORY_ENTRY_DELAY_IMPORT') for entry in getattr(pe, key, [])})})
        pe.close()
    dump(args.output / 'pe-imports.json', {'files': imports, 'scope': 'Static imports; system DLL availability is runner-specific. No GPU execution.'})
    import ctranslate2, sentencepiece, numpy, yaml
    if not callable(getattr(ctranslate2, 'Translator', None)) or 'int8' not in ctranslate2.get_supported_compute_types('cpu'):
        raise ValueError('CT2 native CPU INT8 runtime unavailable')
    dump(args.output / 'wheel-native-imports.json', {'scope': 'Isolated verified-wheel environment, not frozen-helper success or translation quality',
        'versions': {name: importlib.metadata.version(name) for name in ('ctranslate2','sentencepiece','numpy','pyyaml')},
        'cpuComputeTypes': sorted(ctranslate2.get_supported_compute_types('cpu'))})
    # Synthetic tokenizer only; no downloaded model and no quality claim. This lets the frozen
    # helper load/encode before the intentionally absent CT2 model is rejected.
    fixture = args.output / 'synthetic'; fixture.mkdir()
    sentencepiece.SentencePieceTrainer.train(sentence_iterator=iter(['hello 世界 こんにちは 안녕'] * 20),
        model_prefix=str(fixture / 'smoke'), vocab_size=40, hard_vocab_limit=False, minloglevel=2)
    request = {'version': 1, 'source': 'ja', 'target': 'en', 'modelDirectory': str(fixture),
        'sourceTokenizer': str(fixture / 'smoke.model'), 'targetTokenizer': str(fixture / 'smoke.model'),
        'targetPrefix': None, 'tokenizerProtocol': 'ArgosSentencePiece', 'decoderProtocol': 'Argos',
        'lines': [{'id': i, 'text': 'hello'} for i in range(2048)]}
    (args.output / 'synthetic-request.jsonl').write_bytes(json.dumps(request, ensure_ascii=False, separators=(',',':')).encode() + b'\n')

if __name__ == '__main__':
    main()
