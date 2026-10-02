[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PythonExecutable,
  [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$PythonSha256,
  [Parameter(Mandatory)][string]$Wheelhouse,
  [Parameter(Mandatory)][string]$Inventory,
  [Parameter(Mandatory)][string]$Output
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    ![Environment]::Is64BitProcess -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
  throw 'Windows x64 and a 64-bit PowerShell process are required.'
}
function Assert-LocalPath([string]$Path, [bool]$MustExist) {
  if (![IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or
      $Path.Substring(2).Contains(':') -or [IO.Path]::GetFullPath($Path) -cne $Path) {
    throw 'Use canonical absolute local paths, without alternate streams or device namespaces.'
  }
  if ($MustExist -and !(Test-Path -LiteralPath $Path)) { throw 'Required build path does not exist.' }
  $cursor = $Path
  while ($cursor) {
    if (Test-Path -LiteralPath $cursor) {
      if ((Get-Item -Force -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Reparse points are forbidden in build paths.'
      }
    }
    $parent = [IO.Path]::GetDirectoryName($cursor)
    if ($parent -eq $cursor) { break }
    $cursor = $parent
  }
}
foreach ($path in @($PythonExecutable, $Wheelhouse, $Inventory, $PSScriptRoot)) { Assert-LocalPath $path $true }
Assert-LocalPath $Output $false
if (!(Test-Path -LiteralPath $PythonExecutable -PathType Leaf) -or
    !(Test-Path -LiteralPath $Inventory -PathType Leaf) -or
    !(Test-Path -LiteralPath $Wheelhouse -PathType Container)) { throw 'Invalid build input kind.' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $PythonExecutable).Hash.ToLowerInvariant() -cne $PythonSha256) {
  throw 'The explicitly selected CPython executable does not match its reviewed hash.'
}
# Output is never merged with or used to overwrite an existing runtime.
if (Test-Path -LiteralPath $Output) { throw 'Output must be a new directory.' }
$parent = [IO.Path]::GetDirectoryName($Output)
if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'Output parent directory must exist.' }
$venv = Join-Path $parent ('.dropspace-ct2-build-' + [guid]::NewGuid().ToString('N'))
$lock = Join-Path $venv 'offline-requirements.txt'
$stage = Join-Path $venv 'stage'
$validator = @'
import email.parser, hashlib, json, os, pathlib, re, stat, sys, zipfile
ALLOWED = {'ctranslate2', 'sentencepiece', 'pyinstaller', 'pyinstaller-hooks-contrib', 'numpy',
           'pyyaml', 'setuptools', 'packaging', 'altgraph', 'pefile', 'pywin32-ctypes'}
def check(ok, message):
    if not ok: raise ValueError(message)
def pairs(items):
    result = {}
    for key, value in items:
        check(key not in result, 'Duplicate inventory JSON member')
        result[key] = value
    return result
def normalize(name): return re.sub(r'[-_.]+', '-', name).lower()
def safe_path(path):
    check(path.is_absolute() and str(path) == os.path.normpath(str(path)), 'Noncanonical path')
    for item in [path, *path.parents]:
        info = item.lstat()
        check(not stat.S_ISLNK(info.st_mode) and not getattr(info, 'st_file_attributes', 0) & 0x400, 'Reparse path')
def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1048576), b''): value.update(block)
    return value.hexdigest()
def validate(wheelhouse, inventory, lock, python_version):
    safe_path(wheelhouse); safe_path(inventory)
    check(inventory.stat().st_size <= 65536, 'Inventory too large')
    data = json.loads(inventory.read_text(encoding='utf-8'), object_pairs_hook=pairs,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite inventory value')))
    check(type(data) is dict and set(data) == {'schemaVersion', 'python', 'packages'}, 'Inventory schema')
    check(type(data['schemaVersion']) is int and data['schemaVersion'] == 1, 'Inventory version')
    check(type(data['python']) is str and re.fullmatch(r'3\.12\.[0-9]+', data['python']) and
          python_version == data['python'], 'Pinned CPython 3.12 patch version mismatch')
    packages = data['packages']
    check(type(packages) is list and len(packages) == len(ALLOWED), 'Exact dependency allowlist required')
    names, filenames, requirements = set(), set(), []
    for package in packages:
        check(type(package) is dict and set(package) == {'name', 'version', 'file', 'bytes', 'sha256'}, 'Package schema')
        name, version, filename = package['name'], package['version'], package['file']
        check(type(name) is str and name in ALLOWED and name not in names, 'Unreviewed or repeated dependency')
        check(type(version) is str and re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,3}', version), 'Exact stable version required')
        check(type(filename) is str and re.fullmatch(r'[A-Za-z0-9_.+-]+\.whl', filename) and
              filename.casefold() not in filenames, 'Invalid or repeated wheel path')
        check(type(package['bytes']) is int and 0 < package['bytes'] <= 268435456, 'Invalid wheel size')
        check(type(package['sha256']) is str and re.fullmatch(r'[a-f0-9]{64}', package['sha256']), 'Invalid wheel hash')
        # No build tags, source archives, other ABIs, platforms, URLs, or inferred versions.
        parts = filename[:-4].split('-')
        check(len(parts) == 5 and normalize(parts[0]) == name and parts[1] == version and
              (parts[2:] == ['cp312', 'cp312', 'win_amd64'] or
               parts[2:] == ['py3', 'none', 'win_amd64'] or
               parts[2] in ('py3', 'py2.py3') and parts[3:] == ['none', 'any']), 'Wheel name/version/platform mismatch')
        path = wheelhouse / filename
        safe_path(path)
        check(path.is_file() and path.stat().st_size == package['bytes'] and digest(path) == package['sha256'],
              'Offline wheel size/hash mismatch')
        with zipfile.ZipFile(path) as archive:
            members = archive.infolist()
            check(0 < len(members) <= 20000 and sum(item.file_size for item in members) <= 1073741824, 'Wheel content limit')
            archive_names = set()
            for item in members:
                # ZipInfo.filename has already normalized Windows separators and truncated NULs.
                # Reject unsafe raw archive names before using any normalized view.
                raw_name = item.orig_filename
                entry = pathlib.PurePosixPath(raw_name)
                check(raw_name and raw_name == item.filename and not entry.is_absolute() and '..' not in entry.parts and
                      '\\' not in raw_name and ':' not in raw_name and '\x00' not in raw_name and
                      raw_name.casefold() not in archive_names and
                      not stat.S_ISLNK(item.external_attr >> 16), 'Unsafe or duplicate wheel member')
                archive_names.add(raw_name.casefold())
            metadata_names = [item.filename for item in members if item.filename.endswith('.dist-info/METADATA')]
            check(len(metadata_names) == 1 and archive.getinfo(metadata_names[0]).file_size <= 1048576, 'Wheel metadata missing/large')
            metadata = email.parser.BytesParser().parsebytes(archive.read(metadata_names[0]))
            check(len(metadata.get_all('Name', [])) == 1 and len(metadata.get_all('Version', [])) == 1 and
                  normalize(metadata['Name']) == name and metadata['Version'] == version, 'Wheel metadata mismatch')
        names.add(name); filenames.add(filename.casefold())
        requirements.append(name + ' @ ' + path.as_uri() + ' --hash=sha256:' + package['sha256'])
    check(names == ALLOWED, 'Dependency closure incomplete')
    entries = list(wheelhouse.iterdir())
    check(len(entries) == len(filenames) and all(p.name.casefold() in filenames for p in entries), 'Uninventoried wheelhouse files')
    lock.write_text('\n'.join(sorted(requirements)) + '\n', encoding='utf-8')
if __name__ == '__main__':
    check(sys.platform == 'win32' and sys.maxsize > 2**32, 'Windows x64 CPython required')
    validate(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3]),
             '.'.join(map(str, sys.version_info[:3])))
'@
$verifyInstalled = @'
import importlib.metadata, json, re, sys
normalize = lambda value: re.sub(r'[-_.]+', '-', value).lower()
data = json.load(open(sys.argv[1], encoding='utf-8'))
expected = {item['name']: item['version'] for item in data['packages']}
actual = {}
for item in importlib.metadata.distributions():
    name = normalize(item.metadata['Name'])
    if name in actual: raise ValueError('Duplicate installed distribution')
    actual[name] = item.version
# pip is bootstrapped by the pre-reviewed CPython installation; it is build-only.
actual.pop('pip', None)
if actual != expected: raise ValueError('Installed distributions differ from exact reviewed versions')
'@
$runtimeInventory = @'
import hashlib, json, os, pathlib, stat, sys
root = pathlib.Path(sys.argv[1])
files = []
for directory, dirs, names in os.walk(root, followlinks=False):
    for name in sorted(dirs + names):
        path = pathlib.Path(directory) / name
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Runtime contains a reparse point')
    for name in sorted(names):
        path = pathlib.Path(directory) / name
        if not path.is_file() or path.stat().st_size <= 0: raise ValueError('Invalid runtime file')
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for block in iter(lambda: stream.read(1048576), b''): digest.update(block)
        files.append({'path': path.relative_to(root).as_posix(), 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()})
if not any(item['path'] == 'dropspace-ct2-helper.exe' for item in files): raise ValueError('Helper missing')
pathlib.Path(sys.argv[2]).write_text(json.dumps({'schemaVersion': 1, 'files': sorted(files, key=lambda x: x['path'])},
                                             indent=2) + '\n', encoding='utf-8')
'@
try {
  New-Item -ItemType Directory -Path $venv | Out-Null
  & $PythonExecutable -I -S -c $validator $Wheelhouse $Inventory $lock
  if ($LASTEXITCODE -ne 0) { throw 'Offline wheel inventory validation failed.' }
  & $PythonExecutable -I -m venv $venv
  if ($LASTEXITCODE -ne 0) { throw 'Isolated build environment creation failed.' }
  $python = Join-Path $venv 'Scripts\python.exe'
  & $python -I -m pip --isolated --disable-pip-version-check install --no-index --no-deps --no-cache-dir --only-binary=:all: --require-hashes -r $lock
  if ($LASTEXITCODE -ne 0) { throw 'Hash-pinned offline installation failed.' }
  & $python -I -m pip --isolated --disable-pip-version-check check
  if ($LASTEXITCODE -ne 0) { throw 'Offline dependency closure is inconsistent.' }
  & $python -I -c $verifyInstalled $Inventory
  if ($LASTEXITCODE -ne 0) { throw 'Installed dependency inventory verification failed.' }
  # --onefile creates a bootloader child and violates the mandatory ActiveProcessLimit=1 Job.
  # --onedir keeps a single process. Inventory and distribute the ENTIRE resulting directory.
  & $python -I -m PyInstaller --clean --noconfirm --onedir --console --noupx --name dropspace-ct2-helper --distpath $stage --workpath (Join-Path $venv 'work') --specpath $venv (Join-Path $PSScriptRoot 'helper.py')
  if ($LASTEXITCODE -ne 0) { throw 'Single-process helper packaging failed.' }
  $runtime = Join-Path $stage 'dropspace-ct2-helper'
  $inventoryOutput = Join-Path $venv 'engine-files.json'
  & $PythonExecutable -I -S -c $runtimeInventory $runtime $inventoryOutput
  if ($LASTEXITCODE -ne 0) { throw 'Whole-runtime inventory generation failed.' }
  # Output intentionally keeps inventory OUTSIDE engine; package manifest binds this complete list.
  New-Item -ItemType Directory -Path $Output | Out-Null
  Move-Item -LiteralPath $runtime -Destination (Join-Path $Output 'engine')
  Move-Item -LiteralPath $inventoryOutput -Destination (Join-Path $Output 'engine-files.json')
  Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Output 'engine-files.json')
} finally {
  if (Test-Path -LiteralPath $venv) { Remove-Item -LiteralPath $venv -Recurse -Force }
}
