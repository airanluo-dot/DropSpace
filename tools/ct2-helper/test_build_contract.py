"""Exercise the exact stdlib-only validator embedded in build.ps1, without installing anything."""
import copy
import hashlib
import json
from pathlib import Path
import re
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).with_name('build.ps1').read_text(encoding='utf-8')
VALIDATOR = re.search(r"\$validator = @'\n(.*?)\n'@", SCRIPT, re.S).group(1)
UNSET = object()
SCOPE = {'__name__': 'ct2_build_contract_test'}
exec(compile(VALIDATOR, 'build.ps1:validator', 'exec'), SCOPE)


class BuildContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.wheels = self.root / 'wheels'; self.wheels.mkdir()
        self.inventory = self.root / 'dependencies.json'
        self.lock = self.root / 'offline-requirements.txt'
        self.data = {'schemaVersion': 1, 'python': '3.12.10', 'packages': []}
        for name in sorted(SCOPE['ALLOWED']):
            filename = name.replace('-', '_') + '-1.2.3-py3-none-any.whl'
            with zipfile.ZipFile(self.wheels / filename, 'w') as archive:
                archive.writestr(name.replace('-', '_') + '-1.2.3.dist-info/METADATA',
                                 f'Metadata-Version: 2.1\nName: {name}\nVersion: 1.2.3\n')
            package = {'name': name, 'version': '1.2.3', 'file': filename}
            self.refresh(package)
            self.data['packages'].append(package)

    def refresh(self, package):
        content = (self.wheels / package['file']).read_bytes()
        package.update(bytes=len(content), sha256=hashlib.sha256(content).hexdigest())

    def validate(self, data=UNSET, version='3.12.10'):
        self.inventory.write_text(json.dumps(self.data if data is UNSET else data), encoding='utf-8')
        SCOPE['validate'](self.wheels, self.inventory, self.lock, version)

    def reject(self, data=UNSET):
        self.assertFalse(self.lock.exists(), 'Rejection fixture must start without an install plan')
        try:
            with self.assertRaises((ValueError, OSError, zipfile.BadZipFile)): self.validate(data)
            self.assertFalse(self.lock.exists(), 'Rejected inventory must not create an install plan')
        finally:
            # A regression can unexpectedly write a plan before assertRaises fails. Do not let
            # that failed subtest make the next malicious case fail for an unrelated stale file.
            self.lock.unlink(missing_ok=True)

    def test_exact_offline_inventory_generates_only_hashed_file_requirements(self):
        self.validate()
        lines = self.lock.read_text().splitlines()
        self.assertEqual(11, len(lines))
        for line in lines:
            self.assertRegex(line, r'^[-a-z0-9]+ @ file:///.*\.whl --hash=sha256:[a-f0-9]{64}$')
        self.assertNotIn('https:', self.lock.read_text())

    def test_strict_top_level_and_version(self):
        for value in ([], {}, None, dict(self.data, extra=1), dict(self.data, schemaVersion=True),
                      dict(self.data, schemaVersion=2), dict(self.data, python='3.12.'),
                      dict(self.data, python='3.12.10-dev'), dict(self.data, packages=None)):
            with self.subTest(value=value): self.reject(value)

    def test_cpython_patch_version_must_match(self):
        with self.assertRaises(ValueError): self.validate(version='3.12.11')

    def test_json_duplicate_members_rejected(self):
        self.inventory.write_text(json.dumps(self.data).replace('"schemaVersion": 1', '"schemaVersion": 1,"schemaVersion": 1'))
        with self.assertRaises(ValueError): SCOPE['validate'](self.wheels, self.inventory, self.lock, '3.12.10')

    def test_nonfinite_and_large_inventory_rejected(self):
        for raw in ('{"schemaVersion":NaN}', ' ' * 65537):
            self.inventory.write_text(raw)
            with self.assertRaises(ValueError): SCOPE['validate'](self.wheels, self.inventory, self.lock, '3.12.10')

    def test_missing_or_additional_or_repeated_package_rejected(self):
        for packages in (self.data['packages'][:-1], self.data['packages'] + [self.data['packages'][0]],
                         [self.data['packages'][0]] * 11):
            self.reject(dict(self.data, packages=packages))

    def test_unallowlisted_dependencies_rejected(self):
        for name in ('argostranslate', 'flask', 'minisbd', 'transformers', 'torch', 'pip', 'requests', 'PyYAML'):
            data = copy.deepcopy(self.data); data['packages'][0]['name'] = name
            with self.subTest(name=name): self.reject(data)

    def test_package_schema_types_hash_and_version(self):
        invalid = [('extra', 1), ('version', '1.2.*'), ('version', '>=1.2'), ('version', '1.2.3+local'),
                   ('version', None), ('version', True), ('sha256', 'A' * 64), ('sha256', 'a' * 63),
                   ('sha256', None), ('bytes', True), ('bytes', 0), ('bytes', 268435457)]
        for key, value in invalid:
            data = copy.deepcopy(self.data); data['packages'][0][key] = value
            with self.subTest(key=key, value=value): self.reject(data)
        data = copy.deepcopy(self.data); del data['packages'][0]['sha256']; self.reject(data)

    def test_wheel_path_injection_rejected(self):
        for value in ('../evil.whl', '/evil.whl', 'https://evil.whl', 'C:\\evil.whl', 'evil.whl:stream', 'x.whl\n', None):
            data = copy.deepcopy(self.data); data['packages'][0]['file'] = value
            with self.subTest(file=value): self.reject(data)

    def test_wheel_filename_abi_and_version_must_match(self):
        for filename in ('altgraph-9.9.9-py3-none-any.whl', 'altgraph-1.2.3-cp311-cp311-win_amd64.whl',
                         'altgraph-1.2.3-cp312-cp312-linux_x86_64.whl', 'other-1.2.3-py3-none-any.whl',
                         'altgraph-1.2.3-1-py3-none-any.whl'):
            data = copy.deepcopy(self.data); data['packages'][0]['file'] = filename
            with self.subTest(file=filename): self.reject(data)

    def test_wheel_size_and_hash_mismatch(self):
        for key, value in [('sha256', '0' * 64), ('bytes', 1)]:
            data = copy.deepcopy(self.data); data['packages'][0][key] = value; self.reject(data)

    def test_extra_wheelhouse_file_or_directory_rejected(self):
        extra = self.wheels / 'extra.txt'; extra.write_text('x'); self.reject(); extra.unlink()
        extra.mkdir(); self.reject()

    def test_symlink_wheel_rejected(self):
        package = self.data['packages'][0]; path = self.wheels / package['file']
        outside = self.root / 'external.whl'; path.rename(outside)
        try: path.symlink_to(outside)
        except OSError: self.skipTest('Symlinks unavailable')
        self.reject()

    def test_malicious_wheel_members_rejected_even_when_hash_matches(self):
        package = self.data['packages'][0]
        path = self.wheels / package['file']
        original = path.read_bytes()
        entries = ('../outside.py', '/outside.py', 'folder/../../outside.py',
                   'folder\\outside.py', 'file:stream', 'safe.py\x00hidden')
        # Exercise both reader semantics on every OS. ZipInfo normalizes Windows backslashes
        # and truncates NULs, so writestr(str) would silently repair the malicious fixture.
        for separator in ('/', '\\'):
            for entry in entries:
                with self.subTest(separator=separator, entry=entry):
                    path.write_bytes(original)
                    raw = zipfile.ZipInfo('placeholder')
                    raw.filename = entry
                    raw.orig_filename = entry
                    with zipfile.ZipFile(path, 'a') as archive: archive.writestr(raw, 'x')
                    self.refresh(package)
                    with patch.object(zipfile.os, 'sep', separator):
                        with zipfile.ZipFile(path) as archive:
                            self.assertEqual(entry, archive.infolist()[-1].orig_filename,
                                             'The raw attack must survive archive construction')
                        self.reject()

    def test_zip_reader_preserves_original_names_before_platform_normalization(self):
        import io
        content = io.BytesIO()
        raw = zipfile.ZipInfo('placeholder')
        raw.filename = raw.orig_filename = 'folder\\outside.py'
        with zipfile.ZipFile(content, 'w') as archive: archive.writestr(raw, 'x')
        with patch.object(zipfile.os, 'sep', '\\'), zipfile.ZipFile(io.BytesIO(content.getvalue())) as archive:
            entry = archive.infolist()[0]
            self.assertEqual('folder/outside.py', entry.filename)
            self.assertEqual('folder\\outside.py', entry.orig_filename)

    def test_failed_rejection_subtest_cleans_its_install_plan(self):
        # Intentionally pass a valid inventory to the rejection assertion to reproduce the
        # first failing subtest. Its generated plan must not contaminate the next rejection.
        with self.assertRaises(AssertionError): self.reject()
        self.assertFalse(self.lock.exists())
        data = copy.deepcopy(self.data)
        data['packages'][0]['name'] = 'flask'
        self.reject(data)

    def test_case_alias_duplicate_wheel_members_rejected(self):
        package = self.data['packages'][0]
        with zipfile.ZipFile(self.wheels / package['file'], 'a') as archive:
            archive.writestr('same.py', 'x'); archive.writestr('SAME.py', 'x')
        self.refresh(package); self.reject()

    def test_wheel_symlink_member_rejected(self):
        package = self.data['packages'][0]
        entry = zipfile.ZipInfo('link'); entry.create_system = 3; entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        with zipfile.ZipFile(self.wheels / package['file'], 'a') as archive: archive.writestr(entry, 'outside')
        self.refresh(package); self.reject()

    def test_wheel_metadata_name_version_must_match(self):
        package = self.data['packages'][0]
        for metadata in ('Name: flask\nVersion: 1.2.3\n', 'Name: altgraph\nVersion: 9.9.9\n',
                         'Name: altgraph\nName: flask\nVersion: 1.2.3\n'):
            with zipfile.ZipFile(self.wheels / package['file'], 'w') as archive:
                archive.writestr('altgraph-1.2.3.dist-info/METADATA', metadata)
            self.refresh(package); self.reject()

    def test_build_is_onedir_offline_and_checks_each_process(self):
        invocations = [line.strip() for line in SCRIPT.splitlines() if line.strip().startswith('& $')]
        self.assertEqual(7, len(invocations))
        for invocation in invocations:
            self.assertIn(invocation + '\n  if ($LASTEXITCODE -ne 0)', SCRIPT)
        self.assertIn('--onedir --console --noupx', SCRIPT)
        self.assertNotIn('--onefile --', SCRIPT)
        self.assertIn('--no-index --no-deps --no-cache-dir --only-binary=:all: --require-hashes', SCRIPT)
        self.assertIn('Output must be a new directory', SCRIPT)
        self.assertIn('PythonSha256', SCRIPT)
        self.assertNotIn('py -3.12', SCRIPT)

    def test_runtime_inventory_includes_nested_dependencies(self):
        source = re.search(r"\$runtimeInventory = @'\n(.*?)\n'@", SCRIPT, re.S).group(1)
        runtime = self.root / 'engine'; runtime.mkdir()
        (runtime / 'dropspace-ct2-helper.exe').write_bytes(b'fixture')
        inner = runtime / '_internal'; inner.mkdir(); (inner / 'python312.dll').write_bytes(b'library')
        output = self.root / 'files.json'
        import sys
        with patch.object(sys, 'argv', ['inventory', str(runtime), str(output)]):
            exec(compile(source, 'build.ps1:runtimeInventory', 'exec'), {'__name__': 'test'})
        files = json.loads(output.read_text())['files']
        self.assertEqual(['_internal/python312.dll', 'dropspace-ct2-helper.exe'], [item['path'] for item in files])
        self.assertEqual(hashlib.sha256(b'library').hexdigest(), files[0]['sha256'])


if __name__ == '__main__': unittest.main()
