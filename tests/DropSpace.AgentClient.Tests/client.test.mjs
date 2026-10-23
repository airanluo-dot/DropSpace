import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { randomBytes, randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const client = join(repository, 'src/DropSpace.App/Assets/AgentIntegration/dropspace-agent/scripts/Invoke-DropSpace.ps1');
const bridge = join(dirname(client), 'dropspace-mcp.mjs');
const artifactDirectory = join(repository, 'artifacts/1.1-beta1-agent');
await mkdir(artifactDirectory, { recursive: true });
const directory = await mkdtemp(join(artifactDirectory, 'client-check-'));
const endpointFile = join(directory, 'endpoint.json');
const credentials = join(directory, 'ClientCredentials');
const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const bindings = new Map();
const checkNames = [];
let redirect = false;
let catalogVisits = 0;
let slowStarted;
const slowStart = new Promise(resolve => { slowStarted = resolve; });
let slowFinished;
let observedArguments;
const timers = new Set();
const servers = [];
const children = new Set();

function check(name, block) { block(); checkNames.push(name); }
function bind() {
  const binding = { id: randomUUID(), key: randomBytes(32).toString('base64url'), token: randomBytes(60).toString('base64url'), paired: false, revoked: false };
  bindings.set(binding.id, binding);
  return binding;
}
const first = bind();
const second = bind();

async function startServer() {
  const server = createServer(async (request, response) => {
    response.setHeader('Content-Type', 'application/json');
    const reply = (status, value) => { response.writeHead(status); response.end(JSON.stringify(value)); };
    if (request.url === '/v1/pair') {
      let content = '';
      for await (const chunk of request) content += chunk;
      const input = JSON.parse(content);
      const binding = bindings.get(input.bindingId);
      if (!binding || binding.key !== input.pairingKey || binding.paired || binding.revoked) { reply(401, { error: { code: 'unauthorized' } }); return; }
      binding.paired = true;
      reply(200, { bindingId: binding.id, protocolVersion: 1, bearerToken: binding.token });
      return;
    }
    const binding = [...bindings.values()].find(value => request.headers.authorization === `Bearer ${value.token}`);
    if (!binding?.paired || binding.revoked) { reply(401, { error: { code: 'revoked' } }); return; }
    if (request.url === '/v1/catalog') {
      catalogVisits++;
      if (redirect) { response.writeHead(302, { Location: 'http://127.0.0.1:1/credentials-must-not-follow' }); response.end('{}'); return; }
      reply(200, { protocolVersion: 1, commands: [{ name: 'materials.search', description: 'Search test materials', parameters: { type: 'object', properties: { query: { type: 'string' } } }, mutates: false }] });
      return;
    }
    if (request.url === '/v1/execute') {
      let content = '';
      for await (const chunk of request) content += chunk;
      const input = JSON.parse(content);
      if (input.command === 'slow.operation') {
        slowStarted();
        slowFinished = new Promise(resolve => response.on('close', resolve));
        const timer = setTimeout(() => { reply(200, { result: { ok: true } }); timers.delete(timer); }, 30000);
        timers.add(timer);
        return;
      }
      if (input.command !== 'materials.search') { reply(400, { error: { code: 'unknown_command' } }); return; }
      observedArguments = input.arguments;
      reply(200, { result: { items: [{ id: 'fixture-id', text: input.arguments.query }], bindingId: binding.id } });
      return;
    }
    reply(404, { error: { code: 'unknown_command' } });
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  servers.push(server);
  return server;
}

function child(program, args, input) {
  return new Promise((resolve, reject) => {
    const process = spawn(program, args, { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
    children.add(process);
    let stdout = '';
    let stderr = '';
    process.stdout.setEncoding('utf8');
    process.stderr.setEncoding('utf8');
    process.stdout.on('data', value => { stdout += value; });
    process.stderr.on('data', value => { stderr += value; });
    process.on('error', reject);
    process.on('close', code => { children.delete(process); resolve({ code, stdout, stderr }); });
    process.stdin.end(input ?? '', 'utf8');
  });
}
async function invoke(action, binding = first, input, overrideEndpoint = endpointFile) {
  const result = await child(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', client,
    '-Action', action, '-BindingId', binding.id, '-EndpointFile', overrideEndpoint], input === undefined ? '' : JSON.stringify(input));
  for (const grant of bindings.values()) {
    assert(!result.stdout.includes(grant.token) && !result.stderr.includes(grant.token), 'The client must not print bearer tokens.');
    assert(!result.stdout.includes(grant.key) && !result.stderr.includes(grant.key), 'The client must not print pairing keys.');
  }
  assert.equal(result.stderr, '', 'The client must not emit runtime diagnostics.');
  return { ...result, value: JSON.parse(result.stdout) };
}
function mcp() {
  const process = spawn(globalThis.process.execPath, [bridge, '--binding-id', second.id, '--endpoint-file', endpointFile], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  children.add(process);
  const waiters = new Map();
  const messages = [];
  let buffer = '';
  let stderr = '';
  process.stdout.setEncoding('utf8');
  process.stderr.setEncoding('utf8');
  process.stderr.on('data', value => { stderr += value; });
  process.stdout.on('data', chunk => {
    buffer += chunk;
    let end;
    while ((end = buffer.indexOf('\n')) >= 0) {
      const message = JSON.parse(buffer.slice(0, end));
      buffer = buffer.slice(end + 1);
      messages.push(message);
      if (waiters.has(message.id)) { waiters.get(message.id)(message); waiters.delete(message.id); }
    }
  });
  const done = once(process, 'close').then(([code]) => { children.delete(process); assert.equal(stderr, ''); return code; });
  const send = value => process.stdin.write(`${JSON.stringify(value)}\n`);
  const request = value => {
    const promise = new Promise(resolve => waiters.set(value.id, resolve));
    send(value);
    return Promise.race([promise, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error(`MCP request timed out: ${value.id}`)), 15000); timer.unref(); })]);
  };
  return { process, request, send, messages, done };
}

try {
  const server = await startServer();
  const descriptor = port => JSON.stringify({ protocolVersion: 1, endpoint: `http://127.0.0.1:${port}`, processId: process.pid });
  await writeFile(endpointFile, descriptor(server.address().port));
  const firstPair = await invoke('Pair', first, { pairingKey: first.key });
  check('pairing returns safe success without credentials', () => { assert.equal(firstPair.code, 0, firstPair.stdout); assert.equal(firstPair.value.paired, true); });
  const credential = await readFile(join(credentials, `${first.id}.json`), 'utf8');
  check('credential is DPAPI protected on disk', () => { assert(!credential.includes(first.token)); assert(!credential.includes(first.key)); assert.match(JSON.parse(credential).protectedToken, /^[A-Za-z0-9+/]+=*$/); });
  const duplicatePair = await invoke('Pair', first, { pairingKey: first.key });
  check('one-time pairing cannot be replayed', () => assert.equal(duplicatePair.value.error.code, 'unauthorized'));
  const secondPair = await invoke('Pair', second, { pairingKey: second.key });
  check('independent binding pairs without overwriting prior credential', () => assert.equal(secondPair.code, 0, secondPair.stdout));
  const firstCredentialAfter = await readFile(join(credentials, `${first.id}.json`), 'utf8');
  assert.equal(firstCredentialAfter, credential);
  const status = await invoke('Status');
  check('new process reuses saved credential', () => { assert.equal(status.code, 0, status.stdout); assert.equal(status.value.commandCount, 1); });
  const execute = await invoke('Execute', first, { command: 'materials.search', arguments: { query: '中文资料 🧪 C:\\工作\\新图片.png' } });
  check('UTF-8 stdin and stdout preserve Chinese and supplementary characters', () => { assert.equal(execute.code, 0, execute.stdout); assert.equal(execute.value.result.items[0].text, '中文资料 🧪 C:\\工作\\新图片.png'); assert.equal(observedArguments.query, execute.value.result.items[0].text); });
  const replacementServer = await startServer();
  await writeFile(endpointFile, descriptor(replacementServer.address().port));
  const rediscovered = await invoke('Status');
  check('app port is rediscovered from the descriptor', () => assert.equal(rediscovered.code, 0, rediscovered.stdout));
  first.revoked = true;
  const revoked = await invoke('Catalog');
  check('revocation is reported without another binding fallback', () => { assert.equal(revoked.code, 1); assert.equal(revoked.value.error.code, 'revoked'); });
  redirect = true;
  const redirected = await invoke('Catalog', second);
  redirect = false;
  check('HTTP redirects are rejected without forwarding authorization', () => assert.equal(redirected.value.error.code, 'request_failed'));
  const unsafeEndpoint = join(directory, 'unsafe-endpoint.json');
  await writeFile(unsafeEndpoint, JSON.stringify({ protocolVersion: 1, endpoint: 'http://example.invalid:4567' }));
  const unsafe = await invoke('Status', second, undefined, unsafeEndpoint);
  check('non-loopback discovery is rejected', () => assert.equal(unsafe.value.error.code, 'invalid_endpoint'));
  const configuration = await invoke('McpConfiguration', second);
  check('MCP configuration contains paths and binding only', () => { assert.equal(configuration.code, 0, configuration.stdout); assert(configuration.value.args.includes(second.id)); assert(configuration.value.command.endsWith('node.exe')); });

  const rpc = mcp();
  const init = await rpc.request({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'client-test', version: '1' } } });
  check('legacy initialize negotiates 2025-11-25', () => { assert.equal(init.result.protocolVersion, '2025-11-25'); assert.equal(init.result.resultType, undefined); });
  rpc.send({ jsonrpc: '2.0', method: 'notifications/initialized' });
  const listed = await rpc.request({ jsonrpc: '2.0', id: 2, method: 'tools/list' });
  check('legacy tools/list maps live catalog schema', () => { assert.equal(listed.result.tools[0].name, 'materials.search'); assert.equal(listed.result.tools[0].annotations.readOnlyHint, true); });
  const called = await rpc.request({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'materials.search', arguments: { query: '桥接结果 🚀' } } });
  check('legacy tools/call preserves structured output', () => assert.equal(called.result.structuredContent.items[0].text, '桥接结果 🚀'));
  const unknown = await rpc.request({ jsonrpc: '2.0', id: 4, method: 'tools/call', params: { name: 'does.not.exist' } });
  check('unknown tools use protocol errors', () => assert.equal(unknown.error.code, -32602));
  const meta = { 'io.modelcontextprotocol/protocolVersion': '2026-07-28', 'io.modelcontextprotocol/clientCapabilities': {} };
  const modern = mcp();
  const discover = await modern.request({ jsonrpc: '2.0', id: 10, method: 'server/discover', params: { _meta: meta } });
  check('modern discovery works without initialize', () => { assert.equal(discover.result.resultType, 'complete'); assert.deepEqual(discover.result.supportedVersions, ['2026-07-28']); });
  const modernList = await modern.request({ jsonrpc: '2.0', id: 11, method: 'tools/list', params: { _meta: meta } });
  check('modern tools/list includes complete envelope', () => assert.equal(modernList.result.resultType, 'complete'));
  const modernCall = await modern.request({ jsonrpc: '2.0', id: 12, method: 'tools/call', params: { _meta: meta, name: 'materials.search', arguments: { query: 'modern' } } });
  check('modern tools/call maps execution output', () => assert.equal(modernCall.result.structuredContent.items[0].text, 'modern'));
  const missing = await modern.request({ jsonrpc: '2.0', id: 13, method: 'ping', params: { _meta: { 'io.modelcontextprotocol/protocolVersion': '2026-07-28' } } });
  check('modern requests require per-request capability metadata', () => assert.equal(missing.error.code, -32602));
  const unsupported = await modern.request({ jsonrpc: '2.0', id: 14, method: 'server/discover', params: { _meta: { ...meta, 'io.modelcontextprotocol/protocolVersion': '2000-01-01' } } });
  check('unsupported modern version reports supported versions', () => { assert.equal(unsupported.error.code, -32022); assert.deepEqual(unsupported.error.data.supported, ['2026-07-28']); });
  second.revoked = true;
  const revokedTool = await modern.request({ jsonrpc: '2.0', id: 15, method: 'tools/call', params: { _meta: meta, name: 'materials.search', arguments: {} } });
  check('MCP does not cache authorization after revocation', () => { assert.equal(revokedTool.result.isError, true); assert.equal(revokedTool.result.structuredContent.error.code, 'revoked'); });
  second.revoked = false;
  modern.send({ jsonrpc: '2.0', id: 16, method: 'tools/call', params: { _meta: meta, name: 'slow.operation' } });
  await slowStart;
  modern.send({ jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 16 } });
  await Promise.race([slowFinished, new Promise((_, reject) => setTimeout(() => reject(new Error('Helper remained connected after cancellation.')), 5000))]);
  const afterCancel = await modern.request({ jsonrpc: '2.0', id: 17, method: 'ping', params: { _meta: meta } });
  check('cancellation closes helper request and suppresses its response', () => { assert.equal(afterCancel.result.resultType, 'complete'); assert(!modern.messages.some(message => message.id === 16)); });
  rpc.process.stdin.end();
  modern.process.stdin.end();
  const exitCodes = await Promise.all([rpc.done, modern.done]);
  check('EOF exits both bridges without closing the app server', () => { assert.deepEqual(exitCodes, [0, 0]); assert(replacementServer.listening); });
  for (const message of [...rpc.messages, ...modern.messages]) for (const binding of bindings.values()) {
    assert(!JSON.stringify(message).includes(binding.token)); assert(!JSON.stringify(message).includes(binding.key));
  }
  const receipt = { passed: true, checkedAt: new Date().toISOString(), checks: checkNames, count: checkNames.length,
    environment: { platform: process.platform, node: process.version, powershell: 'Windows PowerShell 5.1' },
    boundary: 'Isolated HTTP fixture, real PowerShell DPAPI and real MCP subprocesses; app command implementations are not exercised.', directory, catalogVisits };
  await writeFile(join(directory, 'receipt.json'), JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify(receipt, null, 2));
} finally {
  for (const timer of timers) clearTimeout(timer);
  for (const process of children) process.kill();
  for (const server of servers) { server.closeAllConnections(); server.close(); }
}
