"""Repository-only guardrails; no downloads, package installs or native execution."""
import importlib.util
import json
from pathlib import Path
import re
import unittest
import urllib.parse

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent

class LockTests(unittest.TestCase):
    def setUp(self):
        self.lock = json.loads((HERE / 'dependencies.json').read_text())
        self.source = json.loads((HERE / 'provenance.json').read_text())

    def test_exact_allowlist_and_cp312(self):
        self.assertEqual(set(self.lock), {'schemaVersion','python','packages'})
        self.assertEqual(self.lock['python'], '3.12.10')
        names = [p['name'] for p in self.lock['packages']]
        self.assertEqual(len(names), len(set(names)))
        self.assertEqual(set(names), {'ctranslate2','sentencepiece','numpy','pyyaml','setuptools','pyinstaller',
            'pyinstaller-hooks-contrib','packaging','altgraph','pefile','pywin32-ctypes'})
        for p in self.lock['packages']:
            self.assertRegex(p['version'], r'^\d+(\.\d+){1,3}$')
            self.assertRegex(p['sha256'], r'^[a-f0-9]{64}$')
            self.assertGreater(p['bytes'], 0)
            self.assertLessEqual(p['bytes'], 268435456)
            self.assertTrue(p['file'].endswith(('cp312-cp312-win_amd64.whl','py3-none-win_amd64.whl','py3-none-any.whl','py2.py3-none-any.whl')))

    def test_official_provenance_is_exact(self):
        self.assertEqual(len(self.source['packages']), 11)
        for p in self.lock['packages']:
            matched = [s for s in self.source['packages'] if (s['name'],s['version']) == (p['name'],p['version'])]
            self.assertEqual(len(matched), 1)
            url = urllib.parse.urlsplit(matched[0]['url'])
            self.assertEqual((url.scheme, url.hostname), ('https','files.pythonhosted.org'))
            self.assertEqual(url.path.rsplit('/',1)[-1], p['file'])
            self.assertEqual(matched[0]['metadataUrl'], f"https://pypi.org/pypi/{p['name']}/{p['version']}/json")
        for license in self.source['supplementaryLicenses']:
            self.assertEqual(urllib.parse.urlsplit(license['url']).hostname, 'raw.githubusercontent.com')
            self.assertRegex(license['sha256'], r'^[a-f0-9]{64}$')
        self.assertIn('BLOCKED', self.source['redistributionStatus'])

    def test_matches_model_qa_engine_versions(self):
        versions = {p['name']:p['version'] for p in self.lock['packages']}
        self.assertEqual(versions['ctranslate2'], '4.8.2')
        self.assertEqual(versions['sentencepiece'], '0.2.2')
        self.assertEqual(versions['numpy'], '2.4.6')

    def test_workflow_is_private_nonpublishing(self):
        workflow = (ROOT / '.github/workflows/ct2-runtime-diagnostics.yml').read_text()
        self.assertIn('branches: [qa/ct2-runtime-031]', workflow)
        self.assertIn("if: github.ref == 'refs/heads/qa/ct2-runtime-031'", workflow)
        self.assertIn('contents: read', workflow)
        self.assertIn('retention-days: 14', workflow)
        self.assertIn('persist-credentials: false', workflow)
        self.assertNotIn('write', workflow)
        self.assertNotIn('secrets.', workflow)
        self.assertNotIn('release upload', workflow)

    def test_native_harness_links_real_process_boundary(self):
        project = (HERE / 'Ct2RuntimeQa.csproj').read_text()
        self.assertIn('src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs', project)
        self.assertIn('src/DropSpace.Infrastructure/Lyrics/LocalInferenceProcess.cs', project)
        program = (HERE / 'Program.cs').read_text()
        self.assertIn('WindowsInferenceProcess.Start(start, retainStandardInput: true)', program)
        self.assertNotRegex(program, r'(?<![A-Za-z])Process\.Start\(')
        self.assertIn('WaitForCleanupAsync', program)
        self.assertIn('modelQuality = "NOT TESTED"', program)

if __name__ == '__main__': unittest.main()
