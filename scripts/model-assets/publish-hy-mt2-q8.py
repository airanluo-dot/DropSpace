"""Transport-only mirror of pinned Tencent GGUF bytes; no inference or App build."""
import base64
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request

REPO = 'airanluo-dot/DropSpace'
TAG = 'models-hy-mt2-q8-v1'
RELEASE_ID = 404079851
ROOT = Path(os.environ['RUNNER_TEMP']) / TAG
ROOT.mkdir(exist_ok=True)
BASE = f'https://github.com/{REPO}/releases/download/{TAG}/'

def api(route, method='GET', payload=None):
    args = ['gh', 'api', route, '--method', method]
    if payload is not None:
        args += ['--input', '-']
    return json.loads(subprocess.check_output(args, input=None if payload is None else json.dumps(payload).encode()))

def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for data in iter(lambda: f.read(8 * 1024 * 1024), b''):
            h.update(data)
    return h.hexdigest()

release = api(f'repos/{REPO}/releases/{RELEASE_ID}')
assert release['tag_name'] == TAG and release['prerelease']
assert release['target_commitish'] == '125df115c4b68764281b673fe6048b1b60454855'
models = []
source = Path('src/DropSpace.Core/Lyrics/AiLyricsModelCatalog.cs').read_text()
for mid, name, url, size, digest in re.findall(r'"([^"]+)", "([^"]+)",\s*new Uri\("([^"]+)"\),\s*([\d_]+),\s*"([a-f0-9]{64})"', source):
    if 'Q8_0' not in name:
        continue
    repo, revision, filename = re.search(r'huggingface.co/(.+)/resolve/([^/]+)/(.+)', url).groups()
    with urllib.request.urlopen(f'https://huggingface.co/api/models/{repo}/revision/{revision}?blobs=true', timeout=60) as r:
        upstream = json.load(r)
    item = next(x for x in upstream['siblings'] if x['rfilename'] == filename)
    size = int(size.replace('_', ''))
    assert upstream['sha'] == revision and item['size'] == size and item['lfs']['sha256'] == digest
    licenses = []
    for item in upstream['siblings']:
        if re.search(r'(^|/)(LICENSE|NOTICE)(\..*)?$', item['rfilename'], re.I):
            dest = f"{repo.split('/')[-1]}-{item['rfilename'].replace('/', '-')}"
            with urllib.request.urlopen(f"https://huggingface.co/{repo}/resolve/{revision}/{item['rfilename']}", timeout=60) as r:
                (ROOT / dest).write_bytes(r.read())
            licenses.append(dest)
    assert any('LICENSE' in f.upper() for f in licenses)
    partsizes = [size] if size < 2_147_483_648 else [2_000_000_000] * 3 + [1_981_928_896]
    parts = []
    for i, partsize in enumerate(partsizes):
        parts.append(dict(order=i + 1, name=filename if len(partsizes) == 1 else f'{filename}.part{i+1:03}', bytes=partsize, start=sum(partsizes[:i])))
    models.append(dict(modelId=mid, name=name, upstream=dict(repo=repo, revision=revision, url=url), fullsize=size, fullsha=digest, parts=parts, licenseFiles=licenses, noticePresent=any('NOTICE' in f.upper() for f in licenses), assembly=dict(method='concatenate parts in order, without separators', outputFilename=filename, byteIdenticalToUpstream=True)))
assert [(m['fullsize'], m['fullsha']) for m in models] == [(1908528192, '5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4'), (7981928896, '58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0')]

def download(task):
    model, part = task
    p = ROOT / part['name']
    for attempt in range(6):
        current = p.stat().st_size if p.exists() else 0
        if current == part['bytes']:
            break
        assert current < part['bytes']
        start = part['start'] + current
        end = part['start'] + part['bytes'] - 1
        req = urllib.request.Request(model['upstream']['url'] + f'?download=true&part={part["order"]}&offset={current}', headers={'Range': f'bytes={start}-{end}'})
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                assert r.status == 206 and r.headers['Content-Range'].startswith(f'bytes {start}-{end}/')
                with p.open('ab') as f:
                    for data in iter(lambda: r.read(4 * 1024 * 1024), b''):
                        f.write(data)
            assert p.stat().st_size == part['bytes']
            break
        except Exception as e:
            print('Transfer retry', part['name'], attempt + 1, type(e).__name__, flush=True)
            if attempt == 5:
                raise RuntimeError('Transfer failed: ' + part['name']) from None
            time.sleep(3)
    part['sha256'] = sha(p)
    part['url'] = BASE + part['name']
    print('Downloaded and hashed', part['name'], part['bytes'], part['sha256'], flush=True)

with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
    list(pool.map(download, [(m, p) for m in models for p in m['parts']]))
for model in models:
    h = hashlib.sha256()
    for part in model['parts']:
        assert part['bytes'] < 2_147_483_648
        with (ROOT / part['name']).open('rb') as f:
            for data in iter(lambda: f.read(8 * 1024 * 1024), b''):
                h.update(data)
        del part['start']
    assert h.hexdigest() == model['fullsha'], 'Whole model SHA256 mismatch'
    print('Whole model bytes verified', model['modelId'], h.hexdigest(), flush=True)
    model['licenseUrls'] = [BASE + f for f in model['licenseFiles']]

(ROOT / 'README-model-assets.md').write_bytes(Path('scripts/model-assets/README-model-assets.md').read_bytes())
small_names = [f for m in models for f in m['licenseFiles']] + ['README-model-assets.md']
manifest = dict(schemaVersion=1, releaseTag=TAG, releaseUrl=f'https://github.com/{REPO}/releases/tag/{TAG}', manifestUrl=BASE+'manifest.json', kind='official-model-byte-mirror', transportOnly=True, models=models, supportingAssets=[dict(name=n, bytes=(ROOT/n).stat().st_size, sha256=sha(ROOT/n), url=BASE+n) for n in small_names])
(ROOT / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
names = [p['name'] for m in models for p in m['parts']] + small_names + ['manifest.json']
expected = {p['name']: dict(bytes=p['bytes'], sha256=p['sha256']) for m in models for p in m['parts']}
expected.update({n: dict(bytes=(ROOT/n).stat().st_size, sha256=sha(ROOT/n)) for n in small_names+['manifest.json']})
(ROOT/'SHA256SUMS.txt').write_text(''.join(expected[n]['sha256']+'  '+n+'\n' for n in names))
names.append('SHA256SUMS.txt')
expected['SHA256SUMS.txt'] = dict(bytes=(ROOT/'SHA256SUMS.txt').stat().st_size, sha256=sha(ROOT/'SHA256SUMS.txt'))
existing = {a['name']: a for a in api(f'repos/{REPO}/releases/{RELEASE_ID}')['assets']}
assert set(existing) <= set(names), 'Unexpected asset on dedicated resource release'

def verify(asset, n):
    assert asset['state'] == 'uploaded' and asset['size'] == expected[n]['bytes'], 'Asset size mismatch: '+n
    if asset.get('digest'):
        assert asset['digest'] == 'sha256:'+expected[n]['sha256'], 'GitHub digest mismatch: '+n

def upload(n):
    if n in existing:
        verify(existing[n], n)
        assert existing[n].get('digest'), 'Existing asset lacks verifiable digest; do not overwrite'
        print('Reuse verified existing asset', n, flush=True)
        return
    subprocess.run(['gh','release','upload',TAG,str(ROOT/n),'--repo',REPO], check=True)
    print('Uploaded', n, flush=True)

with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    list(pool.map(upload, names))
assets = {a['name']: a for a in api(f'repos/{REPO}/releases/{RELEASE_ID}')['assets']}
assert set(assets) == set(names)
for n in names:
    verify(assets[n], n)

# Publishing is allowed only after the website excludes this model resource tag.
for attempt in range(60):
    content = api(f'repos/{REPO}/contents/website/_source/scripts/release-contract.mjs?ref=main')
    if 'isModelResourceRelease' in base64.b64decode(content['content']).decode():
        break
    time.sleep(10)
else:
    raise RuntimeError('Website resource tag exclusion is not merged; keep fully uploaded release draft')
result = api(f'repos/{REPO}/releases/{RELEASE_ID}', 'PATCH', dict(draft=False, prerelease=True, make_latest='false'))
assert not result['draft'] and result['prerelease'] and result['tag_name'] == TAG
print('Published model resources', result['html_url'], flush=True)
receipt = dict(releaseUrl=result['html_url'], releaseId=RELEASE_ID, assets=[dict(name=n, bytes=expected[n]['bytes'], sha256=expected[n]['sha256'], githubDigest=assets[n].get('digest'), url=assets[n]['browser_download_url']) for n in names])
(ROOT/'publication-receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
with open(os.environ['GITHUB_STEP_SUMMARY'],'a') as f:
    f.write('## Model asset publication\n'+result['html_url']+'\n\nBoth official model hashes and ordered 7B assembly verified. No App build, tests or inference.\n')
    for n in names:
        f.write(f'- [{n}]({assets[n]["browser_download_url"]}): {expected[n]["bytes"]} bytes, SHA256 `{expected[n]["sha256"]}`\n')
