"""Stage tiny CUDA build metadata from the recorded real producer artifacts.

No runtime binary/model downloads, compilation, executable loading or tests.
The real separate ZIP was already produced and fingerprinted by the local producer.
"""
import argparse, hashlib, json, os, pathlib, re, subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--app-commit', required=True)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
assert re.fullmatch('[0-9a-f]{40}', args.app_commit), 'Exact final App source SHA required'
head = subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD'],text=True).strip()
assert head == args.app_commit, 'Build metadata must match the actual checkout'
tag = (root/'RELEASE_VERSION').read_text(encoding='utf-8').strip()
assert re.fullmatch(r'v\d+\.\d+\.\d+(?:-beta\.\d+)?', tag)
evidence = root/'docs/dev/evidence/beta11-local-cuda'
report = json.loads((evidence/'cuda13-producer-report.json').read_text(encoding='utf-8'))
raw = (evidence/'cuda13-runtime-manifest.json').read_bytes()
manifest_identity = report['manifest']
# Git normalizes this text evidence on non-Windows checkouts. Restore the
# producer's exact CRLF bytes before checking its recorded binary identity.
if hashlib.sha256(raw).hexdigest() != manifest_identity['sha256']:
    raw = raw.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
assert len(raw) == manifest_identity['bytes']
assert hashlib.sha256(raw).hexdigest() == manifest_identity['sha256']
inner = json.loads(raw)
assert inner['runtimeId'] == report['runtimeId'] == 'llama-cpp-v0.5.0-cuda13-win-x64-v1'
assert inner['backend'] == 'cuda' and inner['protocol'] == 1
assert inner['sourceCommit'] == report['engineSourceCommit'] == '7fe450e19305b828c199d602c23a8337aaa1f03b'
assert inner['workerSourceSha256'] == report['workerSourceSha256']
assert inner['files'] == report['files']
archive = dict(report['archive'])
assert tag in ('v0.3.1-beta.11', 'v0.3.1-beta.12', 'v0.3.1-beta.13', 'v0.3.1-beta.14', 'v0.3.1-beta.15'), 'Exact approved component reuse releases only'
assert archive['name'] == 'DropSpace-CUDA-win-x64-v0.3.1-beta.11.zip'
# Corrective Betas reuse the byte-identical Beta11 component. Its manifest/cache identity stays
# stable, while its new asset name and descriptor bind to the actual App build.
archive['name'] = 'DropSpace-CUDA-win-x64-' + tag + '.zip'
assert archive['bytes'] == 540873572
assert archive['sha256'] == '79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b'
archive['url'] = 'https://github.com/airanluo-dot/DropSpace/releases/download/'+tag+'/'+archive['name']
outer = {
    'schemaVersion':1, 'repository':'airanluo-dot/DropSpace',
    'appRelease':{'tag':tag,'sourceCommit':head},
    'componentSourceCommit':report['componentSourceCommit'],
    'runtimeId':inner['runtimeId'],'backend':'cuda','platform':'win-x64',
    'protocol':inner['protocol'],'profile':inner['profile'],
    'engineSourceCommit':inner['sourceCommit'],'workerSourceSha256':inner['workerSourceSha256'],
    'download':archive,'manifest':manifest_identity,'files':inner['files'],'notices':report['notices']}
data = (json.dumps(outer,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
assert len(data) <= 16384
destination = root/'artifacts/cuda-runtime/win-x64'
assert not os.path.lexists(destination), 'Metadata destination must be fresh; existing files are preserved'
destination.mkdir(parents=True)
(destination/'cuda-runtime-manifest.json').write_bytes(raw)
(destination/'cuda-runtime-download.json').write_bytes(data)
print(json.dumps({'appRelease':outer['appRelease'],'archive':archive,
                  'metadataDirectory':str(destination),'nativeRebuilt':False,'testsRun':False},indent=2))

