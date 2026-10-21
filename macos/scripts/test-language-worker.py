#!/usr/bin/env python3
"""Two real CPU predictions with the unchanged, verified baseline language model."""
import json
from pathlib import Path
import subprocess
import time

root = Path(__file__).resolve().parents[1]
requests = [
    {'type': 'predict', 'id': 'own-en', 'text': 'This is a quiet native application test with complete English sentences and no music playing.'},
    {'type': 'predict', 'id': 'own-zh', 'text': '这是一段用于测试本地语言识别的中文句子，验证程序不会把语言判断替换成未经证实的猜测。'},
    {'type': 'shutdown'},
]
started = time.monotonic()
result = subprocess.run([str(root / 'build/language-worker/bin/language-worker'), '--model', str(root / 'build/ValidationModels/lid.176.bin')],
                        input=''.join(json.dumps(request, ensure_ascii=False) + '\n' for request in requests), capture_output=True, text=True, timeout=15)
responses = [json.loads(row) for row in result.stdout.splitlines()]
receipt = {'elapsedSeconds': time.monotonic() - started, 'exitCode': result.returncode, 'responses': responses, 'stderr': result.stderr}
(root / 'build/fasttext-real-prediction.json').write_text(json.dumps(receipt, indent=2) + '\n')
assert result.returncode == 0 and len(responses) == 3, receipt
assert responses[0]['type'] == 'ready' and responses[0]['protocol'] == 1
assert responses[0]['engineCommit'] == '1142dc4c4ecbc19cc16eee5cdd28472e689267e6'
for response, request, expected in zip(responses[1:], requests, ['__label__en', '__label__zh']):
    assert response['id'] == request['id'] and response['type'] == 'prediction'
    assert response['modelSha256'] == '7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e'
    top, runner = response['predictions']
    assert top['label'] == expected and top['score'] >= .6 and top['score'] - runner['score'] >= .2
print(json.dumps(receipt, indent=2))
