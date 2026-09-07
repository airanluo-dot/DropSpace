"""Inventory every tracked blob at a revision; flags are review hints, not proof of defects."""
import argparse
import hashlib
import json
import posixpath
import re
import subprocess
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--revision', default='HEAD')
parser.add_argument('--output', required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]

def git(*arguments):
    return subprocess.check_output(['git', '-C', str(root), *arguments])

revision = git('rev-parse', args.revision).decode().strip()
entries = git('ls-tree', '-rz', '--full-tree', revision).split(b'\0')
patterns = {
    'async_void': r'\basync\s+void\b',
    'detached_assignment': r'\b_\s*=\s*[^;]*(?:Async|Task\.)',
    'blocking_wait': r'\.(?:Wait|Join|GetResult)\s*\(|\.Result\b',
    'task_thread_timer': r'\b(?:Task|Thread|Timer|DispatcherQueue|CancellationTokenSource)\b',
    'event_subscription': r'\+=',
    'event_unsubscription': r'-=',
    'filesystem': r'\b(?:File|Directory|FileStream|FileInfo|DirectoryInfo)\b',
    'cleanup': r'\b(?:Dispose|DisposeAsync|Cleanup|StopAsync)\b',
    'exception_boundary': r'\b(?:catch|throw)\b',
}
files, projects, packages = [], {}, {}
modules = Counter()
source_lines = 0
for entry in entries:
    if not entry:
        continue
    metadata, raw_path = entry.split(b'\t', 1)
    mode, kind, sha = metadata.decode().split()
    path = raw_path.decode()
    if kind != 'blob':
        files.append({'path': path, 'kind': kind, 'git_sha': sha})
        continue
    data = git('cat-file', 'blob', sha)
    item = {'path': path, 'git_sha': sha, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}
    try:
        content = data.decode('utf-8-sig')
        if '\0' in content:
            raise UnicodeError('binary')
    except UnicodeError:
        item['binary'] = True
        files.append(item)
        continue
    lines = content.splitlines()
    item.update(binary=False, lines=len(lines))
    module = '/'.join(path.split('/')[:3]) if path.startswith(('src/', 'tests/')) else path.split('/')[0]
    modules[module] += len(lines)
    if path.endswith(('.cs', '.xaml')) and path.startswith(('src/', 'tests/')):
        source_lines += len(lines)
    if path.endswith('.cs'):
        item['signals'] = {name: [number for number, line in enumerate(lines, 1) if re.search(pattern, line)] for name, pattern in patterns.items()}
        item['namespaces'] = re.findall(r'^using\s+(DropSpace[\w.]+)\s*;', content, re.M)
    if path.endswith('.csproj'):
        project = ET.fromstring(content)
        projects[path] = [posixpath.normpath(posixpath.join(posixpath.dirname(path), ref.attrib['Include'].replace('\\', '/'))) for ref in project.iter('ProjectReference')]
    if path == 'Directory.Packages.props':
        packages = {item.attrib['Include']: item.attrib['Version'] for item in ET.fromstring(content).iter('PackageVersion')}
    files.append(item)

cycles = []
def visit(project, chain):
    if project in chain:
        cycles.append(chain[chain.index(project):] + [project])
        return
    for dependency in projects.get(project, []):
        visit(dependency, chain + [project])
for project in projects:
    visit(project, [])

report = {
    'revision': revision,
    'method': 'Every git-tracked blob read in full. Regex flags are candidates for review; this is not whole-program semantic analysis or runtime evidence.',
    'file_count': len(files),
    'binary_count': sum(item.get('binary', False) for item in files),
    'source_and_test_lines': source_lines,
    'project_references': projects,
    'project_cycles': cycles,
    'pinned_packages': packages,
    'module_text_lines': dict(sorted(modules.items())),
    'files': files,
}
output = Path(args.output)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({key: report[key] for key in ('revision', 'file_count', 'binary_count', 'source_and_test_lines', 'project_cycles')}, ensure_ascii=False))
raise SystemExit(1 if cycles else 0)
