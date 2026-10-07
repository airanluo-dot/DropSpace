"""Focused metadata/packaging contracts; never executes a native worker or model."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import cuda_runtime_contract as contract

ROOT = Path(__file__).resolve().parent.parent
EVIDENCE = Path('docs/dev/evidence/beta11-local-cuda')


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / (name + '.py'))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


stage = load_script('stage-reviewed-cuda-metadata').stage
packager = load_script('package-cuda-runtime')


class CudaComponentDeliveryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        shutil.copytree(ROOT / EVIDENCE, self.root / EVIDENCE)
        (self.root / 'RELEASE_VERSION').write_text('v0.3.1-beta.16\n')
        subprocess.run(['git', 'init', '-q', str(self.root)], check=True)
        self.commit()

    def commit(self):
        subprocess.run(['git', '-C', str(self.root), 'add', 'RELEASE_VERSION'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Fixture', '-c',
                        'user.email=fixture@example.invalid', 'commit', '-qm', 'fixture'], check=True)
        self.sha = subprocess.check_output(['git', '-C', str(self.root), 'rev-parse', 'HEAD'], text=True).strip()

    def descriptor(self):
        return json.loads((self.root / 'artifacts/cuda-runtime/win-x64' / contract.DESCRIPTOR).read_text())

    def test_app_releases_reuse_exact_component_and_manifest(self):
        first = stage(self.root, self.sha)
        descriptor = self.descriptor()
        metadata = Path(first['metadataDirectory'])
        raw = (metadata / contract.MANIFEST).read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), contract.MANIFEST_SHA256)
        self.assertEqual(len(raw), contract.MANIFEST_BYTES)
        shutil.rmtree(metadata)
        (self.root / 'RELEASE_VERSION').write_text('v0.3.1-beta.17\n')
        self.commit()
        stage(self.root, self.sha)
        next_descriptor = self.descriptor()
        self.assertNotEqual(descriptor.pop('appRelease'), next_descriptor.pop('appRelease'))
        self.assertEqual(descriptor, next_descriptor)
        self.assertEqual(raw, (metadata / contract.MANIFEST).read_bytes())
        self.assertEqual(next_descriptor['download']['url'], contract.ARCHIVE_URL)
        self.assertEqual(next_descriptor['download']['sha256'], contract.ARCHIVE_SHA256)
        self.assertEqual(next_descriptor['schemaVersion'], 2)
        self.assertFalse(any(path.suffix == '.zip' for path in metadata.iterdir()))

    def test_exact_app_commit_is_still_required(self):
        for invalid in ['0' * 40, 'main', '']:
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                stage(self.root, invalid)
        self.assertFalse((self.root / 'artifacts').exists())

    def test_existing_metadata_is_not_overwritten(self):
        result = stage(self.root, self.sha)
        descriptor = self.descriptor()
        with self.assertRaisesRegex(ValueError, 'must be fresh'):
            stage(self.root, self.sha)
        self.assertEqual(descriptor, self.descriptor())

    def test_bad_app_tags_fail_closed(self):
        for tag in ['main', contract.COMPONENT_TAG, 'v0.3.1-beta.0', 'v0.3.1-beta.9999', 'v21.0.0', 'v0.3.1?x=1']:
            (self.root / 'RELEASE_VERSION').write_text(tag)
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                stage(self.root, self.sha)

    def test_manifest_mutation_fails_before_staging(self):
        path = self.root / EVIDENCE / 'cuda13-runtime-manifest.json'
        path.write_bytes(path.read_bytes() + b' ')
        with self.assertRaisesRegex(ValueError, 'manifest bytes changed'):
            stage(self.root, self.sha)
        self.assertFalse((self.root / 'artifacts').exists())

    def test_archive_mutation_fails_before_staging(self):
        path = self.root / EVIDENCE / 'cuda13-producer-report.json'
        value = json.loads(path.read_text())
        value['archive']['sha256'] = '0' * 64
        path.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, 'archive bytes changed'):
            stage(self.root, self.sha)

    def test_independent_descriptor_has_no_app_binding(self):
        report = json.loads((self.root / EVIDENCE / 'cuda13-producer-report.json').read_text())
        manifest = json.loads((self.root / EVIDENCE / 'cuda13-runtime-manifest.json').read_text())
        archive = dict(report['archive'], name=contract.ARCHIVE_NAME)
        descriptor = contract.component_descriptor(manifest, report['manifest'], archive,
                                                   report['notices'], contract.COMPONENT_COMMIT)
        self.assertNotIn('appRelease', descriptor)
        self.assertEqual(descriptor['componentRelease']['tag'], contract.COMPONENT_TAG)
        self.assertNotIn('v0.3.1-beta.', descriptor['download']['url'])
        for key, bad in [('sha256', '0' * 64), ('bytes', archive['bytes'] + 1), ('name', '../bad.zip')]:
            changed = dict(archive, **{key: bad})
            with self.subTest(key=key), self.assertRaises(ValueError):
                contract.component_descriptor(manifest, report['manifest'], changed,
                                              report['notices'], contract.COMPONENT_COMMIT)
        for key, bad in [('profile', 'changed'), ('protocol', 2), ('runtimeId', 'other')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                contract.component_descriptor(dict(manifest, **{key: bad}), report['manifest'], archive,
                                              report['notices'], contract.COMPONENT_COMMIT)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is not installed')
    def test_build_binding_checker_rejects_changed_app_or_component(self):
        result = stage(self.root, self.sha)
        metadata = Path(result['metadataDirectory'])
        descriptor = self.descriptor()
        command = ['pwsh', '-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                   str(ROOT / 'scripts/Test-CudaBuildBinding.ps1'), '-MetadataDirectory', str(metadata),
                   '-InformationalVersion', '0.3.1-beta.16+' + self.sha]
        checked = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(checked.returncode, 0, checked.stderr)
        for parent, key, value in [
            ('appRelease', 'tag', 'v0.3.1-beta.17'),
            ('appRelease', 'sourceCommit', '0' * 40),
            ('componentRelease', 'tag', 'cuda-other-v1'),
            ('download', 'url', contract.ARCHIVE_URL + '?other=1'),
            ('download', 'name', 'DropSpace-CUDA-win-x64-v0.3.1-beta.16.zip'),
            ('download', 'sha256', 'invalid'),
            ('manifest', 'sha256', '0' * 64),
        ]:
            changed = json.loads(json.dumps(descriptor))
            changed[parent][key] = value
            (metadata / contract.DESCRIPTOR).write_text(json.dumps(changed))
            with self.subTest(parent=parent, key=key):
                self.assertNotEqual(subprocess.run(command, capture_output=True).returncode, 0)

    def test_packager_cli_is_component_only(self):
        result = subprocess.run(['python', str(ROOT / 'scripts/package-cuda-runtime.py'), '--help'],
                                check=True, capture_output=True, text=True)
        self.assertNotIn('--app-tag', result.stdout)
        self.assertNotIn('--app-commit', result.stdout)
        self.assertIn('--component-commit', result.stdout)
        self.assertEqual(packager.ARCHIVE_NAME, contract.ARCHIVE_NAME)


if __name__ == '__main__':
    unittest.main()
