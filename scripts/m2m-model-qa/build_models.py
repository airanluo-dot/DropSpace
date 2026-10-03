"""QA-only pinned download and safe conversion. Never used by the application."""
import argparse, gc, hashlib, importlib.metadata, json, os, platform, sys, time, urllib.request, zipfile
from pathlib import Path


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def download(item, destination):
    if destination.exists():
        if destination.stat().st_size == item['bytes'] and sha(destination) == item['sha256']:
            return
        raise ValueError(f'Existing source does not match pinned manifest: {destination.name}')
    partial = destination.with_suffix(destination.suffix + '.partial')
    with urllib.request.urlopen(item['url'], timeout=60) as response, partial.open('wb') as output:
        while chunk := response.read(1024 * 1024):
            output.write(chunk)
    if partial.stat().st_size != item['bytes'] or sha(partial) != item['sha256']:
        raise ValueError(f'Pinned source byte count/hash mismatch: {destination.name}')
    partial.replace(destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--site-packages', required=True)
    parser.add_argument('--phase', required=True, choices=['sanitize','convert'])
    parser.add_argument('--root', required=True)
    parser.add_argument('--evidence', required=True)
    args = parser.parse_args()
    packages = Path(args.site_packages)
    if not packages.is_absolute() or not packages.is_dir(): raise ValueError('Missing controlled packages')
    sys.path.insert(0,str(packages.resolve()))
    root, evidence = Path(args.root).resolve(), Path(args.evidence).resolve()
    root.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    manifest_path = Path(args.manifest).resolve()
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1', OMP_NUM_THREADS='4', MKL_NUM_THREADS='4', HF_ENABLE_PARALLEL_LOADING='false')
    import torch
    from transformers import M2M100ForConditionalGeneration, M2M100Tokenizer
    from ctranslate2.converters import TransformersConverter
    class SafeConverter(TransformersConverter):
        def load_model(self, model_class, model_name_or_path, **kwargs):
            kwargs.update(local_files_only=True, trust_remote_code=False, use_safetensors=True, weights_only=True)
            return model_class.from_pretrained(model_name_or_path, **kwargs)
        def load_tokenizer(self, tokenizer_class, model_name_or_path, **kwargs):
            kwargs.update(local_files_only=True, trust_remote_code=False)
            return tokenizer_class.from_pretrained(model_name_or_path, **kwargs)
    torch.set_num_threads(4)
    torch.set_num_interop_threads(1)
    record = {'phase': 'build-conversion-only', 'inferenceMeasurement': False,
              'python': sys.version, 'platform': platform.platform(), 'sourceManifestSha256': sha(manifest_path),
              'versions': {name: importlib.metadata.version(name) for name in ['torch', 'transformers', 'ctranslate2', 'sentencepiece', 'sacremoses']},
              'safeLoading': {'weights_only': True, 'trust_remote_code': False, 'local_files_only': True, 'converterInput': 'safetensors'},
              'quantization': 'int8', 'models': {}}
    for model in manifest['models']:
        direction = model['direction']
        if direction != 'multilingual':
            raise ValueError('Unexpected route')
        clock = time.monotonic()
        source, safe, converted = (root / kind / direction for kind in ('source', 'sanitized', 'converted'))
        source.mkdir(parents=True, exist_ok=True)
        for item in model['files']:
            if Path(item['path']).name != item['path'] or not item['url'].startswith(f"https://huggingface.co/{model['repo']}/resolve/{model['revision']}/"):
                raise ValueError('Unexpected pinned source path')
            download(item, source / item['path'])
        if args.phase == 'sanitize':
            if not zipfile.is_zipfile(source / 'pytorch_model.bin'):
                raise ValueError('Expected ZIP torch checkpoint for memory-mapped safe load; refusing unbounded fallback')
            tokenizer = M2M100Tokenizer.from_pretrained(source, local_files_only=True)
            network = M2M100ForConditionalGeneration.from_pretrained(source, local_files_only=True,
                trust_remote_code=False, use_safetensors=False, weights_only=True, dtype=torch.float32)
            network.save_pretrained(safe, safe_serialization=True, max_shard_size='1GB')
            tokenizer.save_pretrained(safe)
            files = [{'path': p.name, 'bytes': p.stat().st_size, 'sha256': sha(p)} for p in sorted(safe.iterdir()) if p.is_file()]
            (evidence / 'sanitization-manifest.json').write_text(json.dumps({'safeLoading':record['safeLoading'], 'checkpointZip':True, 'transformersVersion':importlib.metadata.version('transformers'), 'mmapPolicy':'Pinned Transformers uses torch.load(weights_only=True,mmap=True) for ZIP checkpoints', 'files':files}, indent=2), encoding='utf-8')
            print('Safetensors sanitization complete; process exits before CT2 conversion', flush=True)
            return
        # This is a fresh process. No initial HF model coexists with CT2 conversion.
        sanitized = json.loads((evidence / 'sanitization-manifest.json').read_text(encoding='utf-8'))
        for item in sanitized['files']:
            path = safe / item['path']
            if path.stat().st_size != item['bytes'] or sha(path) != item['sha256']:
                raise ValueError('Sanitized input hash mismatch')
        if list(safe.glob('*.bin')) or not list(safe.glob('*.safetensors')):
            raise ValueError('Converter input must contain safetensors only')
        converter = SafeConverter(str(safe), trust_remote_code=False)
        converter.convert(str(converted), quantization='int8', force=False)
        del converter
        gc.collect()
        files = [{'path': p.relative_to(converted).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha(p)}
                 for p in sorted(converted.rglob('*')) if p.is_file()]
        record['models'][direction] = {'repo': model['repo'], 'revision': model['revision'], 'license': model['license'],
                                       'source': str(source), 'converted': str(converted), 'files': files,
                                       'tokenizerFiles': [item for item in model['files'] if item['path'] != 'pytorch_model.bin'],
                                       'buildSeconds': time.monotonic() - clock}
        (evidence / 'conversion-manifest.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
        print(f'{direction}: source verified and int8 conversion completed', flush=True)
    (evidence / 'source-models.json').write_bytes(manifest_path.read_bytes())


if __name__ == '__main__':
    main()
