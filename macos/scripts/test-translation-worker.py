#!/usr/bin/env python3
"""Real pinned-model inference probe. Own short text, bounded time/RSS, confirmed exit.

This validates the native worker, not the unfinished app translation pipeline.
"""
import argparse
import json
from pathlib import Path
import selectors
import subprocess
import time
import uuid

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--backend', choices=['cpu', 'metal'], required=True)
parser.add_argument('--profile', choices=['hy-mt2-18-q8-plain-beta', 'hy-mt2-7b-q8-plain-beta'], default='hy-mt2-18-q8-plain-beta')
parser.add_argument('--worker', type=Path, help='Exact built/bundled worker to validate')
parser.add_argument('--text', default='月光照亮了安静的河流。', help='Authored validation text, never user lyrics')
parser.add_argument('--receipt-prefix', default='translation', help='Evidence basename inside build/')
args = parser.parse_args()
if not args.receipt_prefix or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789-' for c in args.receipt_prefix):
    parser.error('receipt-prefix must contain only lowercase letters, digits and hyphens')
if not args.text or len(args.text.encode('utf-8')) > 4096:
    parser.error('authored text must be nonempty and at most 4096 UTF-8 bytes')
manifest = json.loads((root.parent / 'src/DropSpace.Infrastructure/Lyrics/Manifests/models-hy-mt2-q8-v1.json').read_text())
model = next(x for x in manifest['models'] if x['modelId'] == args.profile)
path = root / 'build/ModelValidation' / (model['fullsha'] + '.gguf')
worker = args.worker.resolve() if args.worker else root / 'build/workers/bin/plain-lyrics-worker'
profile = 'hy-mt2-7b-q8' if '7b' in args.profile else 'hy-mt2-1.8b-q8'
capabilities = json.loads(subprocess.check_output([str(worker), '--capabilities'], text=True))
maximum = (12 if '7b' in args.profile else 3) * 1024**3
if capabilities['reclaimableHostBytesEstimate'] < maximum + 1024**3:
    raise SystemExit('Insufficient estimated reclaimable memory for bounded validation; no worker started.')
source = args.text
prompt = '将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n' + source
identifier = uuid.uuid4().hex
request = {'protocol': 1, 'id': identifier, 'prompt': prompt}
receipt = {'backend': args.backend, 'profile': args.profile, 'worker': str(worker), 'modelSHA256': model['fullsha'], 'source': source,
           'capabilities': capabilities, 'ready': None, 'response': None, 'maximumObservedRSS': 0, 'passed': False}
started = time.monotonic()
with (root / ('build/' + args.receipt_prefix + '-' + args.backend + '-stderr.log')).open('wb') as errors:
    child = subprocess.Popen([str(worker), '--model', str(path), '--mode', args.backend, '--model-profile', profile, '--timings'],
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
    selector = selectors.DefaultSelector(); selector.register(child.stdout, selectors.EVENT_READ)
    buffered = b''
    try:
        while receipt['response'] is None:
            if time.monotonic() - started > 90: raise TimeoutError('Bounded worker probe timed out')
            if child.poll() is not None: raise RuntimeError('Worker exited before completing response: ' + str(child.returncode))
            value = subprocess.run(['/bin/ps', '-o', 'rss=', '-p', str(child.pid)], capture_output=True, text=True)
            rss = int(value.stdout.strip() or '0') * 1024
            receipt['maximumObservedRSS'] = max(receipt['maximumObservedRSS'], rss)
            if rss > maximum: raise RuntimeError('Native worker exceeded validation resident-memory budget')
            for key, _ in selector.select(.1):
                chunk = key.fileobj.read1(65536)
                if not chunk: raise RuntimeError('Unexpected worker output EOF')
                buffered += chunk
                if len(buffered) > 65536: raise RuntimeError('Output frame exceeds bound')
                while b'\n' in buffered:
                    line, buffered = buffered.split(b'\n', 1)
                    message = json.loads(line)
                    if receipt['ready'] is None:
                        assert message['ready'] is True and message['protocol'] == 1
                        assert message['backend'] == args.backend and message['modelProfile'] == profile
                        receipt['ready'] = message
                        child.stdin.write((json.dumps(request, ensure_ascii=False) + '\n').encode())
                        child.stdin.flush()
                    else:
                        assert message['id'] == identifier and message['protocol'] == 1 and message['complete'] is True
                        text = message['text'].strip()
                        assert text and len(text.encode()) <= 16384 and len(text.encode('utf-16-le')) // 2 <= 4096
                        assert '\ufffd' not in text and '```' not in text and not any(ord(x) < 32 and x != '\t' for x in text)
                        receipt['response'] = message
        child.stdin.close()
        assert child.wait(timeout=5) == 0
        receipt['passed'] = True
    except Exception as error:
        receipt['failure'] = str(error)
        raise
    finally:
        selector.close()
        if child.poll() is None:
            child.terminate()
            try: child.wait(timeout=2)
            except subprocess.TimeoutExpired: child.kill(); child.wait(timeout=5)
        receipt['exitCode'] = child.returncode; receipt['elapsedSeconds'] = time.monotonic() - started
        (root / ('build/' + args.receipt_prefix + '-' + args.backend + '-receipt.json')).write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n')
print(json.dumps(receipt, ensure_ascii=False, indent=2))
