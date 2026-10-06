import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// These checks establish byte identity inside the trusted-repository-writer model.
// They do not establish semantic quality or authenticate a reviewer.
export const releaseRepository = 'airanluo-dot/DropSpace';
export const runtimeProducerWorkflow = '.github/workflows/release.yml';
export const releaseBindingName = 'runtime-publication.json';
export const releasePackageNames = Object.freeze(['DropSpace.exe', 'DropSpace-x64.msix', 'DropSpaceSetup.exe']);
const hashPattern = /^[a-f0-9]{64}$/;
const commitPattern = /^[a-f0-9]{40}$/;
const order = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
export const hashBytes = bytes => createHash('sha256').update(bytes).digest('hex');
const readJson = filename => JSON.parse(fs.readFileSync(filename, 'utf8').replace(/^\uFEFF/, ''));

export function canonicalRuntimePath(value) {
  assert.ok(typeof value === 'string' && value.length <= 240 && /^[A-Za-z0-9_.\/-]+$/.test(value), 'Runtime path must be canonical');
  assert.ok(value.split('/').every(part => part && part !== '.' && part !== '..' && !part.endsWith('.') &&
    !/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part)), 'Runtime path must be canonical');
  return value;
}

export function validateInventory(files) {
  assert.ok(Array.isArray(files) && files.length > 0 && files.length <= 4096, 'Runtime inventory is required');
  const seen = new Set();
  let total = 0;
  const result = files.map(file => {
    assert.ok(file && typeof file === 'object', 'Runtime file identity is required');
    const name = canonicalRuntimePath(file.path);
    assert.ok(!seen.has(name.toLowerCase()), 'Duplicate runtime inventory path');
    seen.add(name.toLowerCase());
    assert.match(file.sha256 ?? '', hashPattern, 'Runtime file SHA256 is required');
    assert.ok(Number.isSafeInteger(file.bytes) && file.bytes > 0, 'Runtime file byte count is required');
    total += file.bytes;
    assert.ok(total <= 2 * 1024 ** 3, 'Runtime inventory is too large');
    return { path: name, sha256: file.sha256, bytes: file.bytes };
  }).sort(order);
  return result;
}

export function fileIdentity(filename, name = path.basename(filename)) {
  const info = fs.lstatSync(filename);
  assert.ok(info.isFile() && !info.isSymbolicLink(), `Expected a plain file: ${name}`);
  const hash = createHash('sha256');
  const fd = fs.openSync(filename, 'r');
  const buffer = Buffer.alloc(1024 * 1024);
  try {
    let count;
    while ((count = fs.readSync(fd, buffer, 0, buffer.length, null)) > 0) hash.update(buffer.subarray(0, count));
  } finally { fs.closeSync(fd); }
  assert.equal(fs.statSync(filename).size, info.size, `File changed during inspection: ${name}`);
  return { name, sha256: hash.digest('hex'), bytes: info.size };
}

export function directoryInventory(directory) {
  assert.ok(fs.lstatSync(directory).isDirectory() && !fs.lstatSync(directory).isSymbolicLink(), 'Runtime directory must be plain');
  const files = [];
  const visit = (current, prefix) => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      canonicalRuntimePath(relative);
      const filename = path.join(current, entry.name);
      const info = fs.lstatSync(filename);
      assert.ok(!info.isSymbolicLink(), 'Runtime inventory cannot contain links');
      if (info.isDirectory()) visit(filename, relative);
      else {
        const identity = fileIdentity(filename, relative);
        files.push({ path: relative, sha256: identity.sha256, bytes: identity.bytes });
      }
    }
  };
  visit(directory, '');
  return validateInventory(files);
}

export function compareInventory(actual, expected, label = 'Runtime payload') {
  assert.deepEqual(validateInventory(actual), validateInventory(expected), `${label} differs from the exact reviewed runtime files`);
}

export function validateArtifactContract(contract) {
  assert.equal(contract?.schemaVersion, 1, 'Unsupported runtime artifact contract');
  assert.equal(contract.repository, releaseRepository, 'Runtime artifact repository mismatch');
  assert.equal(contract.workflowPath, runtimeProducerWorkflow, 'Untrusted runtime producer workflow');
  for (const field of ['runId', 'runAttempt', 'artifactId'])
    assert.ok(Number.isSafeInteger(contract[field]) && contract[field] > 0, `Runtime artifact ${field} is required`);
  assert.match(contract.headCommit ?? '', commitPattern, 'Runtime artifact head commit is required');
  assert.match(contract.checkoutCommit ?? '', commitPattern, 'Runtime artifact checkout commit is required');
  assert.equal(contract.artifactName, `ai-candidate-runtime-${contract.runId}-${contract.runAttempt}`, 'Runtime artifact name/attempt mismatch');
  assert.match(contract.archiveSha256 ?? '', hashPattern, 'Runtime archive SHA256 is required');
  validateInventory(contract.files);
  return contract;
}

export function validateArtifactMetadata(contract, artifact, run, now = Date.now()) {
  validateArtifactContract(contract);
  assert.equal(artifact?.id, contract.artifactId, 'Runtime artifact ID mismatch');
  assert.equal(artifact.name, contract.artifactName, 'Runtime artifact name mismatch');
  assert.equal(artifact.expired, false, 'Runtime artifact has expired');
  assert.ok(Number.isFinite(Date.parse(artifact.expires_at)) && Date.parse(artifact.expires_at) > now, 'Runtime artifact expiry is missing or expired');
  assert.equal(artifact.digest, `sha256:${contract.archiveSha256}`, 'Runtime artifact archive digest mismatch');
  assert.equal(artifact.workflow_run?.id, contract.runId, 'Runtime artifact run mismatch');
  assert.equal(artifact.workflow_run?.head_sha, contract.headCommit, 'Runtime artifact source mismatch');
  assert.equal(run?.id, contract.runId, 'Runtime producer run mismatch');
  assert.equal(run.run_attempt, contract.runAttempt, 'Runtime producer attempt mismatch');
  assert.equal(run.head_sha, contract.headCommit, 'Runtime producer source mismatch');
  assert.equal(run.repository?.full_name, contract.repository, 'Runtime producer repository mismatch');
  assert.equal(run.head_repository?.full_name, contract.repository, 'Runtime producer head repository mismatch');
  assert.equal(run.path?.split('@')[0], contract.workflowPath, 'Runtime producer workflow mismatch');
  assert.equal(run.status, 'completed', 'Runtime producer is incomplete');
  assert.equal(run.conclusion, 'success', 'Runtime producer did not succeed');
}

export function validateRuntimeProducer(manifest, contract) {
  const expected = Object.fromEntries(['repository', 'workflowPath', 'runId', 'runAttempt', 'headCommit', 'checkoutCommit'].map(key => [key, contract[key]]));
  assert.deepEqual(manifest.producer, expected, 'Reviewed runtime producer metadata mismatch');
}

function packageIdentity(value, name) {
  assert.equal(value?.name, name, 'Unexpected release package name');
  assert.match(value.sha256 ?? '', hashPattern, 'Release package SHA256 is required');
  assert.ok(Number.isSafeInteger(value.bytes) && value.bytes > 0, 'Release package byte count is required');
  return { name, sha256: value.sha256, bytes: value.bytes };
}

// These exact owner-authorized Betas record compiler inputs; installation was not tested.
function installerPortableIdentity(installer) {
  if(installer.kind === 'installer-payload') return installer.installedPortable;
  assert.equal(installer.kind, 'installer-build-input', 'Unsupported installer verification kind');
  const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const release=fs.readFileSync(path.join(root,'RELEASE_VERSION'),'utf8').trim();
  assert.ok(['v0.3.1-beta.11','v0.3.1-beta.12','v0.3.1-beta.13', 'v0.3.1-beta.14'].includes(release), 'Compiler-input inspection requires an exact owner-authorized Beta');
  assert.equal(installer.verificationScope, 'compiler-input-bytes-only; installer not executed; tests waived');
  return installer.buildInputPortable;
}

export function verifyReleaseBinding(directory, { expectedInventory, expectedCommit } = {}) {
  const binding = readJson(path.join(directory, releaseBindingName));
  assert.equal(binding.schemaVersion, 1, 'Unsupported runtime publication binding');
  assert.match(binding.sourceCommit ?? '', commitPattern, 'Publication source commit is required');
  if (expectedCommit !== undefined) assert.equal(binding.sourceCommit, expectedCommit, 'Publication source commit mismatch');
  const files = validateInventory(binding.runtimeFiles);
  if (expectedInventory !== undefined) compareInventory(files, expectedInventory, 'Published runtime');
  assert.equal(binding.portable?.schemaVersion, 1, 'Portable runtime inspection is required');
  assert.equal(binding.msix?.schemaVersion, 1, 'MSIX runtime inspection is required');
  assert.equal(binding.installer?.schemaVersion, 1, 'Installer payload inspection is required');
  compareInventory(binding.portable.files, files, 'Portable runtime');
  compareInventory(binding.msix.files, files, 'MSIX runtime');
  for (const [key, name] of [['portable', 'DropSpace.exe'], ['msix', 'DropSpace-x64.msix'], ['installer', 'DropSpaceSetup.exe']]) {
    const recorded = packageIdentity(binding[key].package, name);
    assert.deepEqual(recorded, fileIdentity(path.join(directory, name), name), `Final release package changed: ${name}`);
  }
  assert.deepEqual(packageIdentity(installerPortableIdentity(binding.installer), 'DropSpace.exe'), binding.portable.package, 'Installer verification records a different portable identity');
  return binding;
}

export function writeReleaseBinding(directory, { runtimeFiles, sourceCommit, portable, msix, installer }) {
  const binding = { schemaVersion: 1, sourceCommit, runtimeFiles: validateInventory(runtimeFiles), portable, msix, installer };
  const filename = path.join(directory, releaseBindingName);
  // Validate everything before writing a record. No verdict or approval is produced.
  assert.match(sourceCommit ?? '', commitPattern, 'Publication source commit is required');
  for (const [key, name] of [['portable', 'DropSpace.exe'], ['msix', 'DropSpace-x64.msix'], ['installer', 'DropSpaceSetup.exe']]) {
    assert.equal(binding[key]?.schemaVersion, 1, `${key} inspection is required`);
    assert.deepEqual(packageIdentity(binding[key].package, name), fileIdentity(path.join(directory, name), name), `Final release package changed: ${name}`);
  }
  compareInventory(portable.files, binding.runtimeFiles, 'Portable runtime');
  compareInventory(msix.files, binding.runtimeFiles, 'MSIX runtime');
  assert.deepEqual(packageIdentity(installerPortableIdentity(installer), 'DropSpace.exe'), portable.package, 'Installer verification records a different portable identity');
  fs.writeFileSync(filename, JSON.stringify(binding, null, 2) + '\n');
  return verifyReleaseBinding(directory, { expectedInventory: runtimeFiles, expectedCommit: sourceCommit });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    const usage = 'Usage: ai-runtime-publication.mjs verify-artifact CONTRACT ARTIFACT RUN | verify-contract CONTRACT | verify-runtime-directory DIRECTORY CONTRACT | verify-bundle DIRECTORY | record-bundle DIRECTORY (runtime-directory|reference-binding) PATH PORTABLE MSIX INSTALLER';
    if (args[0] === 'verify-artifact' && args.length === 4) validateArtifactMetadata(...args.slice(1).map(readJson));
    else if (args[0] === 'verify-contract' && args.length === 2) validateArtifactContract(readJson(args[1]));
    else if (args[0] === 'verify-runtime-directory' && args.length === 3) {
      const contract = validateArtifactContract(readJson(args[2]));
      compareInventory(directoryInventory(args[1]), contract.files);
    }
    else if (args[0] === 'verify-bundle' && args.length === 2) verifyReleaseBinding(args[1], { expectedCommit: process.env.GITHUB_SHA });
    else if (args[0] === 'record-bundle' && args.length === 7 && ['runtime-directory', 'reference-binding'].includes(args[2])) {
      const runtimeFiles = args[2] === 'runtime-directory' ? directoryInventory(args[3]) : validateInventory(readJson(args[3]).runtimeFiles);
      writeReleaseBinding(args[1], { runtimeFiles, sourceCommit: process.env.GITHUB_SHA, portable: readJson(args[4]), msix: readJson(args[5]), installer: readJson(args[6]) });
    } else throw new Error(usage);
    console.log('Exact runtime/publication byte bindings verified; this does not grant semantic approval.');
  } catch (error) { console.error(`Runtime publication blocked: ${error.message}`); process.exitCode = 1; }
}
