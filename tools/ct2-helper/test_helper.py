"""Offline protocol tests. Native dependencies are stubbed; no model quality claim."""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock

import helper


class HelperTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        for name in ('source.spm', 'target.spm'):
            (self.root / name).write_bytes(b'unit-test-placeholder')
        self.request = {'version': 1, 'source': 'ja', 'target': 'en', 'modelDirectory': str(self.root),
                        'sourceTokenizer': str(self.root / 'source.spm'),
                        'targetTokenizer': str(self.root / 'target.spm'), 'targetPrefix': None,
                        'tokenizerProtocol': 'ArgosSentencePiece', 'decoderProtocol': 'Argos',
                        'lines': [{'id': 17, 'text': '歌詞'}]}

    def frame(self, request=None):
        return json.dumps(self.request if request is None else request, ensure_ascii=False).encode() + b'\n'

    def reject(self, request):
        with self.assertRaises(helper.ProtocolError):
            helper.read_request(io.BytesIO(json.dumps(request, ensure_ascii=True).encode() + b'\n'))

    def modules(self, *, encode=None, decode=None, hypotheses=None, result_count=None):
        seen = {'tokenizers': [], 'inputs': [], 'settings': [], 'decoded': []}
        class Tokenizer:
            def __init__(self, model_file):
                self.path = model_file
                seen['tokenizers'].append(model_file)
            def encode(self, text, out_type):
                self_test.assertTrue(self.path.endswith('source.spm'))
                return ['▁hello'] if encode is None else encode(text)
            def decode(self, pieces):
                self_test.assertTrue(self.path.endswith('target.spm'))
                seen['decoded'].append(pieces)
                return ' 世界 ' if decode is None else decode(pieces)
        class Translator:
            def __init__(self, path, **kwargs):
                seen['settings'].append(kwargs)
            def translate_batch(self, batch, **kwargs):
                seen['inputs'].append(batch)
                seen['settings'].append(kwargs)
                count = len(batch) if result_count is None else result_count
                return [types.SimpleNamespace(hypotheses=[['▁world', '</s>']] if hypotheses is None else hypotheses)
                        for _ in range(count)]
        self_test = self
        return mock.patch.dict(sys.modules, {'sentencepiece': types.SimpleNamespace(SentencePieceProcessor=Tokenizer),
                                            'ctranslate2': types.SimpleNamespace(Translator=Translator)}), seen

    def test_valid_request_roundtrip(self):
        self.assertEqual(self.request, helper.read_request(io.BytesIO(self.frame())))

    def test_only_one_lf_framed_request(self):
        for raw in (b'', self.frame()[:-1], self.frame() + b'\n', self.frame() + b' ',
                    self.frame().replace(b'\n', b'\r\n'), b'\xef\xbb\xbf' + self.frame(), b'\xff\n'):
            with self.subTest(raw=raw[:40]), self.assertRaises(helper.ProtocolError):
                helper.read_request(io.BytesIO(raw))

    def test_request_byte_budget_counts_json_not_final_lf(self):
        raw = self.frame()[:-1]
        exact = raw + b' ' * (helper.MAX_BYTES - len(raw)) + b'\n'
        self.assertEqual(self.request, helper.read_request(io.BytesIO(exact)))
        with self.assertRaises(helper.ProtocolError):
            helper.read_request(io.BytesIO(exact[:-1] + b' \n'))

    def test_duplicate_keys_at_both_levels(self):
        for raw in (self.frame().replace(b'"version": 1', b'"version": 1,"version": 1'),
                    self.frame().replace(b'"id": 17', b'"id": 17,"id": 18')):
            with self.assertRaises(helper.ProtocolError): helper.read_request(io.BytesIO(raw))

    def test_nonfinite_and_excessively_nested_json(self):
        for raw in (b'{"version":NaN}\n', b'{"version":Infinity}\n', b'[' * 2000 + b']' * 2000 + b'\n'):
            with self.assertRaises(helper.ProtocolError): helper.read_request(io.BytesIO(raw))

    def test_invalid_top_level_and_missing_extra_fields(self):
        for value in (None, [], True, {}, dict(self.request, extra=1)):
            self.reject(value)
        for key in self.request:
            value = self.request.copy(); del value[key]
            with self.subTest(key=key): self.reject(value)

    def test_all_nonnullable_request_fields_reject_null(self):
        for key in set(self.request) - {'targetPrefix'}:
            with self.subTest(key=key): self.reject(dict(self.request, **{key: None}))

    def test_version_is_exact_integer_one(self):
        for value in (True, 1.0, '1', 2): self.reject(dict(self.request, version=value))

    def test_route_is_explicit_supported_canonical_pair(self):
        for source, target in [('JA', 'en'), ('ja-JP', 'en'), ('en', 'en'), ('en', 'ja'), ('ja', 'ko'), ('', 'en'), ([], 'en')]:
            self.reject(dict(self.request, source=source, target=target))
        for source, target in helper.ROUTES:
            helper.validate_request(dict(self.request, source=source, target=target))

    def test_protocol_pair_not_independent_allowlists(self):
        for tokenizer, decoder in [('ArgosSentencePiece', 'HelsinkiOpus'), ('HelsinkiSentencePiece', 'Argos'),
                                   ('other', 'Argos'), ([], 'Argos')]:
            self.reject(dict(self.request, tokenizerProtocol=tokenizer, decoderProtocol=decoder))

    def test_target_prefix_requires_reviewed_explicit_single_token(self):
        self.reject(dict(self.request, targetPrefix='>>en<<'))
        helsinki = dict(self.request, tokenizerProtocol='HelsinkiSentencePiece', decoderProtocol='HelsinkiOpus')
        for value in (True, '', 'en', '>>a b<<', '>>a\n<<', '>>' + 'a' * 61 + '<<', ['>>en<<']):
            self.reject(dict(helsinki, targetPrefix=value))
        helper.validate_request(dict(helsinki, targetPrefix='>>cmn_Hans<<'))
        helper.validate_request(dict(helsinki, targetPrefix='>>' + 'a' * 60 + '<<'))

    def test_line_count_and_schema(self):
        for lines in ([], {}, [None], [{'id': 0}], [{'id': 0, 'text': 'x', 'extra': 1}],
                      [{'id': i, 'text': 'x'} for i in range(helper.MAX_LINES + 1)]):
            self.reject(dict(self.request, lines=lines))

    def test_ids_strict_int32_nonnegative_and_unique(self):
        for identifier in (True, 1.0, '1', None, -1, 2147483648):
            self.reject(dict(self.request, lines=[{'id': identifier, 'text': 'x'}]))
        self.reject(dict(self.request, lines=[{'id': 4, 'text': 'x'}, {'id': 4, 'text': 'y'}]))
        helper.validate_request(dict(self.request, lines=[{'id': 2147483647, 'text': 'x'}]))

    def test_line_text_types_unicode_controls_and_byte_limit(self):
        for text in (None, True, 3, '', ' \t ', '\x00x', 'x\nx', 'x\rx', 'x\x01x', '\ud800',
                     'a' * (helper.MAX_LINE_BYTES + 1), '歌' * 1366):
            self.reject(dict(self.request, lines=[{'id': 0, 'text': text}]))
        helper.validate_request(dict(self.request, lines=[{'id': 0, 'text': 'a' * helper.MAX_LINE_BYTES}]))

    def test_paths_must_exist_and_be_absolute_canonical(self):
        for key in ('modelDirectory', 'sourceTokenizer', 'targetTokenizer'):
            for value in ('relative', '', str(self.root / 'missing'), str(self.root) + '/./source.spm', '\x00', 7):
                with self.subTest(key=key, value=value): self.reject(dict(self.request, **{key: value}))
        self.reject(dict(self.request, modelDirectory=self.request['sourceTokenizer']))
        self.reject(dict(self.request, sourceTokenizer=str(self.root)))

    def test_empty_tokenizer_and_symlink_rejected(self):
        empty = self.root / 'empty'; empty.touch()
        self.reject(dict(self.request, sourceTokenizer=str(empty)))
        link = self.root / 'link'
        try: link.symlink_to(self.root / 'source.spm')
        except OSError: self.skipTest('Symlinks unavailable')
        self.reject(dict(self.request, sourceTokenizer=str(link)))

    def test_invalid_request_never_imports_native_code(self):
        original = __import__
        def guarded(name, *args, **kwargs):
            if name in ('ctranslate2', 'sentencepiece'): self.fail('Native import before validation')
            return original(name, *args, **kwargs)
        with mock.patch('builtins.__import__', side_effect=guarded), self.assertRaises(helper.ProtocolError):
            helper.translate(dict(self.request, decoderProtocol='wrong'))

    def test_argos_uses_explicit_source_and_target_models(self):
        modules, seen = self.modules()
        with modules: result = helper.translate(self.request)
        self.assertEqual([{'id': 17, 'text': '世界'}], result)
        self.assertEqual([self.request['sourceTokenizer'], self.request['targetTokenizer']], seen['tokenizers'])
        self.assertEqual([[['▁hello']]], seen['inputs'])
        self.assertEqual([['▁world']], seen['decoded'])
        self.assertEqual(0, seen['settings'][1]['max_input_length'])
        self.assertIs(True, seen['settings'][1]['return_end_token'])

    def test_helsinki_prefix_is_explicit_and_eos_added(self):
        for prefix, expected in [(None, ['▁hello', '</s>']), ('>>cmn_Hans<<', ['>>cmn_Hans<<', '▁hello', '</s>'])]:
            modules, seen = self.modules()
            request = dict(self.request, source='en', target='zh', targetPrefix=prefix,
                           tokenizerProtocol='HelsinkiSentencePiece', decoderProtocol='HelsinkiOpus')
            with modules: helper.translate(request)
            self.assertEqual([[expected]], seen['inputs'])

    def test_overbudget_input_never_constructs_translator(self):
        for count in (0, helper.MAX_INPUT_TOKENS + 1):
            modules, seen = self.modules(encode=lambda _: ['x'] * count)
            with modules, self.assertRaises(helper.ProtocolError): helper.translate(self.request)
            self.assertEqual([], seen['settings'])

    def test_helsinki_reserved_tokens_count_toward_input_limit(self):
        modules, seen = self.modules(encode=lambda _: ['x'] * helper.MAX_INPUT_TOKENS)
        with modules, self.assertRaises(helper.ProtocolError):
            helper.translate(dict(self.request, tokenizerProtocol='HelsinkiSentencePiece', decoderProtocol='HelsinkiOpus'))
        self.assertEqual([], seen['settings'])

    def test_total_input_budget(self):
        modules, seen = self.modules(encode=lambda _: ['x'] * helper.MAX_INPUT_TOKENS)
        with modules, self.assertRaises(helper.ProtocolError):
            helper.translate(dict(self.request, lines=[{'id': i, 'text': 'x'} for i in range(33)]))
        self.assertEqual([], seen['settings'])

    def test_missing_or_partial_or_multiple_hypotheses_fail(self):
        for hypotheses in ([], [['x']], [['x', '</s>', 'y', '</s>']], [[]], [['x', '</s>'], ['y', '</s>']],
                           [['x'] * 512 + ['</s>']], [[None, '</s>']]):
            modules, _ = self.modules(hypotheses=hypotheses)
            with modules, self.assertRaises(helper.ProtocolError): helper.translate(self.request)

    def test_wrong_result_count_fails(self):
        for count in (0, 2):
            modules, _ = self.modules(result_count=count)
            with modules, self.assertRaises(helper.ProtocolError): helper.translate(self.request)

    def test_invalid_decoded_text_fails(self):
        for text in ('', '   ', 'x\ny', '\ud800', 'x' * (helper.MAX_LINE_BYTES + 1)):
            modules, _ = self.modules(decode=lambda _: text)
            with modules, self.assertRaises(helper.ProtocolError): helper.translate(self.request)

    def test_bounded_batches_preserve_ids(self):
        modules, seen = self.modules()
        request = dict(self.request, lines=[{'id': i * 7, 'text': 'x'} for i in range(35)])
        with modules: result = helper.translate(request)
        self.assertEqual([16, 16, 3], [len(batch) for batch in seen['inputs']])
        self.assertEqual([line['id'] for line in request['lines']], [line['id'] for line in result])

    def test_total_output_budget_stops_next_batch(self):
        modules, seen = self.modules(hypotheses=[['x'] * 511 + ['</s>']])
        request = dict(self.request, lines=[{'id': i, 'text': 'x'} for i in range(100)])
        with modules, self.assertRaises(helper.ProtocolError): helper.translate(request)
        self.assertEqual(3, len(seen['inputs']))

    def test_response_byte_budget_is_utf8_and_whole_message(self):
        lines = [{'id': i, 'text': '歌' * 1300} for i in range(100)]
        with self.assertRaises(helper.ProtocolError): helper.encode_response(lines)

    def test_response_duplicate_ids_rejected(self):
        with self.assertRaises(helper.ProtocolError):
            helper.encode_response([{'id': 0, 'text': 'x'}, {'id': 0, 'text': 'y'}])

    def test_stdout_native_and_python_logs_are_isolated_even_at_exit(self):
        code = '''import atexit, os, sys
sys.path.insert(0, sys.argv.pop(1))
import helper
sys.argv = ['helper', '--stdio-once']
def translate(request):
    os.write(1, b'native-log\\n')
    print('python-log')
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.GetStdHandle.argtypes = [wintypes.DWORD]
        kernel.GetStdHandle.restype = wintypes.HANDLE
        kernel.WriteFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
        kernel.WriteFile.restype = wintypes.BOOL
        written = wintypes.DWORD()
        assert kernel.WriteFile(kernel.GetStdHandle(wintypes.DWORD(-11)), b'win32-log\\n', 10, ctypes.byref(written), None)
    return [{'id': request['lines'][0]['id'], 'text': 'result'}]
helper.translate = translate
atexit.register(lambda: os.write(1, b'exit-log\\n'))
raise SystemExit(helper.main())
'''
        result = subprocess.run([sys.executable, '-I', '-c', code, str(Path(helper.__file__).parent)],
                                input=self.frame(), capture_output=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({'version': 1, 'lines': [{'id': 17, 'text': 'result'}]}, json.loads(result.stdout))
        self.assertEqual(b'{"version":1,"lines":[{"id":17,"text":"result"}]}', result.stdout)
        self.assertIn(b'native-log', result.stderr)
        self.assertIn(b'python-log', result.stderr)
        self.assertIn(b'exit-log', result.stderr)
        if os.name == 'nt': self.assertIn(b'win32-log', result.stderr)

    def test_real_entrypoint_invalid_input_has_no_protocol_output(self):
        result = subprocess.run([sys.executable, '-I', helper.__file__, '--stdio-once'],
                                input=b'{"version":true}\n', capture_output=True, timeout=10)
        self.assertEqual(2, result.returncode)
        self.assertEqual(b'', result.stdout)
        self.assertNotIn(b'Traceback', result.stderr)


if __name__ == '__main__': unittest.main()
