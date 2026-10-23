// Dependency-free stdio adapter. The PowerShell client owns DPAPI credentials.
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const MODERN = '2026-07-28';
const LEGACY = '2025-11-25';
const INFO = { name: 'dropspace-local', version: '1.0.0' };
const VERSION_KEY = 'io.modelcontextprotocol/protocolVersion';
const CAPABILITIES_KEY = 'io.modelcontextprotocol/clientCapabilities';
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  const name = process.argv[index];
  const value = process.argv[index + 1];
  if (!['--binding-id', '--endpoint-file', '--credential-directory'].includes(name) || !value || options.has(name)) {
    process.stderr.write('DropSpace MCP: invalid arguments. Use McpConfiguration from Invoke-DropSpace.ps1.\n');
    process.exit(1);
  }
  options.set(name, value);
}
if (process.platform !== 'win32' || Number(process.versions.node.split('.')[0]) < 18 ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(options.get('--binding-id') ?? '')) {
  process.stderr.write('DropSpace MCP requires Windows, Node.js 18 or newer and --binding-id. No runtime is downloaded.\n');
  process.exit(1);
}
const helper = join(dirname(fileURLToPath(import.meta.url)), 'Invoke-DropSpace.ps1');
const powershell = join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const requests = new Map();
let initialized = false;
let initializeReceived = false;
let closing = false;

class RpcError extends Error {
  constructor(code, message, data) { super(message); this.code = code; this.data = data; }
}

function send(value) {
  if (!closing) process.stdout.write(`${JSON.stringify(value)}\n`);
}

function failure(id, code, message, data) {
  const error = { code, message };
  if (data !== undefined) error.data = data;
  const response = { jsonrpc: '2.0', error };
  if (id !== undefined) response.id = id;
  send(response);
}

function invoke(action, input, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new Error('cancelled')); return; }
    const args = ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', helper,
      '-Action', action, '-BindingId', options.get('--binding-id')];
    for (const [option, parameter] of [['--endpoint-file', '-EndpointFile'], ['--credential-directory', '-CredentialDirectory']]) {
      if (options.has(option)) args.push(parameter, options.get(option));
    }
    const child = spawn(powershell, args, { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
    let output = '';
    let bytes = 0;
    let settled = false;
    const stop = () => child.kill();
    const timer = setTimeout(stop, 40000);
    signal.addEventListener('abort', stop, { once: true });
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      signal.removeEventListener('abort', stop);
      if (error) reject(error); else resolve(value);
    };
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', data => {
      bytes += Buffer.byteLength(data);
      if (bytes > 4194304) { stop(); finish(new RpcError(-32603, 'DropSpace response exceeds the limit.')); }
      else output += data;
    });
    // Do not forward arbitrary PowerShell diagnostics: a runtime error could include user input.
    child.stderr.resume();
    child.on('error', () => finish(new RpcError(-32603, 'The Windows PowerShell client could not start.')));
    child.stdin.on('error', () => {});
    child.on('close', code => {
      if (signal.aborted) { finish(new Error('cancelled')); return; }
      try {
        const value = JSON.parse(output);
        if (!object(value) || (code !== 0 && !object(value.error))) throw new Error('invalid response');
        finish(null, value);
      } catch { finish(new RpcError(-32603, 'The local client did not return a valid response; inspect app state before retrying a mutation.')); }
    });
    child.stdin.end(input === undefined ? '' : JSON.stringify(input), 'utf8');
  });
}

function protocol(request) {
  const meta = request.params?._meta;
  if (object(meta) && Object.hasOwn(meta, VERSION_KEY)) {
    if (meta[VERSION_KEY] !== MODERN) throw new RpcError(-32022, 'Unsupported protocol version.', { supported: [MODERN], requested: meta[VERSION_KEY] });
    if (!object(meta[CAPABILITIES_KEY])) throw new RpcError(-32602, 'Required clientCapabilities metadata is missing.');
    return true;
  }
  if (request.method === 'initialize') return false;
  if (initialized || (initializeReceived && request.method === 'ping')) return false;
  throw new RpcError(-32602, 'Supply protocolVersion and clientCapabilities metadata, or initialize the legacy protocol first.');
}

async function dispatch(request, signal, modern) {
  const params = request.params ?? {};
  if (request.method === 'initialize') {
    if (modern || initializeReceived || typeof params.protocolVersion !== 'string' || !object(params.capabilities) || !object(params.clientInfo)) {
      throw new RpcError(-32602, 'Invalid or repeated legacy initialization.');
    }
    initializeReceived = true;
    return { protocolVersion: LEGACY, capabilities: { tools: {} }, serverInfo: INFO,
      instructions: 'Discover the current DropSpace command catalog with tools/list. Use a binding paired in DropSpace Settings.' };
  }
  if (request.method === 'server/discover') {
    if (!modern) throw new RpcError(-32601, 'server/discover requires the modern protocol metadata.');
    return { supportedVersions: [MODERN], capabilities: { tools: {} },
      _meta: { 'io.modelcontextprotocol/serverInfo': INFO },
      instructions: 'Local DropSpace commands are discovered from the running app. Pairing is completed separately through the PowerShell client.' };
  }
  if (request.method === 'ping') return {};
  if (request.method === 'tools/list') {
    if (params.cursor !== undefined) throw new RpcError(-32602, 'This catalog is not paginated.');
    const catalog = await invoke('Catalog', undefined, signal);
    if (catalog.error) throw new RpcError(-32603, 'DropSpace catalog is unavailable.', catalog.error);
    if (catalog.protocolVersion !== 1 || !Array.isArray(catalog.commands)) throw new RpcError(-32603, 'DropSpace returned an invalid catalog.');
    return { tools: catalog.commands.map(command => {
      if (typeof command.name !== 'string' || typeof command.description !== 'string' || !object(command.parameters)) {
        throw new RpcError(-32603, 'DropSpace returned an invalid command schema.');
      }
      return { name: command.name, description: command.description, inputSchema: command.parameters,
        annotations: { readOnlyHint: command.mutates === false, destructiveHint: command.mutates !== false, openWorldHint: true } };
    }) };
  }
  if (request.method === 'tools/call') {
    if (typeof params.name !== 'string' || !params.name || (params.arguments !== undefined && !object(params.arguments))) {
      throw new RpcError(-32602, 'Tool name and an arguments object are required.');
    }
    const response = await invoke('Execute', { command: params.name, arguments: params.arguments ?? {} }, signal);
    if (response.error?.code === 'unknown_command') throw new RpcError(-32602, 'Unknown DropSpace tool. Refresh tools/list.');
    const value = response.error ? response : response.result;
    if (value === undefined) throw new RpcError(-32603, 'DropSpace returned an invalid execution result.');
    const result = { content: [{ type: 'text', text: JSON.stringify(value) }], isError: Boolean(response.error) };
    if (object(value)) result.structuredContent = value;
    return result;
  }
  throw new RpcError(-32601, 'Method not found.');
}

async function receive(line) {
  let request;
  try { request = JSON.parse(line); } catch { failure(undefined, -32700, 'Invalid JSON.'); return; }
  if (!object(request) || request.jsonrpc !== '2.0' || typeof request.method !== 'string' ||
      (request.params !== undefined && !object(request.params))) {
    failure(undefined, -32600, 'Invalid JSON-RPC request.'); return;
  }
  if (!Object.hasOwn(request, 'id')) {
    if (request.method === 'notifications/initialized' && initializeReceived) initialized = true;
    if (request.method === 'notifications/cancelled') requests.get(request.params?.requestId)?.abort();
    return;
  }
  const id = request.id;
  if (!(typeof id === 'string' || Number.isSafeInteger(id))) { failure(undefined, -32600, 'Request ID must be a string or integer.'); return; }
  if (requests.has(id)) { failure(id, -32600, 'Duplicate active request ID.'); return; }
  if (requests.size >= 4) { failure(id, -32603, 'The bridge is busy; wait for an active request to finish.'); return; }
  const controller = new AbortController();
  requests.set(id, controller);
  try {
    const modern = protocol(request);
    const result = await dispatch(request, controller.signal, modern);
    if (!controller.signal.aborted) send({ jsonrpc: '2.0', id, result: modern ? { ...result, resultType: 'complete' } : result });
  } catch (error) {
    if (!controller.signal.aborted) failure(id, error instanceof RpcError ? error.code : -32603,
      error instanceof RpcError ? error.message : 'Local bridge failure.', error instanceof RpcError ? error.data : undefined);
  } finally { requests.delete(id); }
}

let pending = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => {
  pending += chunk;
  let newline;
  while ((newline = pending.indexOf('\n')) >= 0) {
    const line = pending.slice(0, newline);
    pending = pending.slice(newline + 1);
    if (Buffer.byteLength(line) > 1048576) failure(undefined, -32600, 'Request exceeds the 1 MiB limit.');
    else if (line.trim()) void receive(line);
  }
  if (Buffer.byteLength(pending) > 1048576) {
    failure(undefined, -32600, 'Unterminated request exceeds the 1 MiB limit.');
    shutdown();
  }
});

function shutdown() {
  if (closing) return;
  closing = true;
  for (const controller of requests.values()) controller.abort();
  process.stdin.destroy();
}
process.stdin.on('end', shutdown);
process.stdin.on('error', shutdown);
process.stdout.on('error', shutdown);
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
