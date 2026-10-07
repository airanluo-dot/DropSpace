'use strict';
// Only mutation endpoints and OIDC are mocked. Pages artifact discovery uses the
// real Actions artifact service and the run-scoped artifact uploaded by the probe.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
(async () => {
  const kind = process.env.COMPAT_KIND;
  assert.ok(['pages', 'release'].includes(kind));
  const calls = [];
  let unexpected = false, uploadBytes = 0, release = null;
  const fixture = Buffer.from('DropSpace Linux runner compatibility fixture\n');
  const dir = path.resolve('runner-compat/evidence');
  fs.mkdirSync(dir, {recursive: true});
  fs.writeFileSync(path.join(dir, 'probe.txt'), fixture);
  fs.writeFileSync(path.join(dir, 'body.md'), 'Isolated runner compatibility probe. No publication.\n');
  const server = http.createServer(async (req, res) => {
    try {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = Buffer.concat(chunks);
      const url = new URL(req.url, 'http://localhost');
      const route = `${req.method} ${url.pathname}`;
      calls.push(route);
      const reply = (status, value) => {res.writeHead(status, {'content-type': 'application/json'}); res.end(JSON.stringify(value));};
      if (route === 'GET /oidc') return reply(200, {value: 'e30.eyJzdWIiOiJydW5uZXItY29tcGF0In0.probe'});
      if (route === 'POST /repos/airanluo-dot/DropSpace/pages/deployments') {
        const data = JSON.parse(body);
        assert.ok(Number(data.artifact_id) > 0);
        assert.equal(data.pages_build_version, process.env.GITHUB_SHA);
        return reply(201, {id: 'compat', page_url: `${origin}/site`, status_url: `${origin}/repos/airanluo-dot/DropSpace/pages/deployments/compat`});
      }
      if (route === 'GET /repos/airanluo-dot/DropSpace/pages/deployments/compat') return reply(200, {status: 'succeed'});
      if (route === 'GET /repos/airanluo-dot/DropSpace') return reply(200, {name: 'DropSpace', full_name: 'airanluo-dot/DropSpace', immutable_releases_enabled: false});
      if (route === 'GET /repos/airanluo-dot/DropSpace/releases/tags/runner-compat-fixture') return reply(404, {message: 'Not Found'});
      if (route === 'GET /repos/airanluo-dot/DropSpace/releases') return reply(200, []);
      if (route === 'POST /repos/airanluo-dot/DropSpace/releases') {
        const data = JSON.parse(body);
        assert.equal(data.tag_name, 'runner-compat-fixture');
        assert.equal(data.target_commitish, process.env.GITHUB_SHA);
        assert.match(data.body, /Isolated runner compatibility probe/);
        release = {...data, id: 1, html_url: `${origin}/release/1`, upload_url: `${origin}/uploads/1{?name,label}`, assets: []};
        return reply(201, release);
      }
      if (route === 'GET /repos/airanluo-dot/DropSpace/releases/1/assets') return reply(200, release?.assets ?? []);
      if (route === 'POST /uploads/1') {
        assert.deepEqual(body, fixture);
        uploadBytes += body.length;
        const asset = {id: 2, name: url.searchParams.get('name'), size: body.length, browser_download_url: `${origin}/download/probe.txt`, state: 'uploaded'};
        release.assets.push(asset);
        return reply(201, asset);
      }
      if (route === 'PATCH /repos/airanluo-dot/DropSpace/releases/1') {
        release = {...release, ...JSON.parse(body)};
        return reply(200, release);
      }
      unexpected = true;
      return reply(404, {message: `Unexpected isolated API route: ${route}`});
    } catch (error) {
      unexpected = true;
      res.writeHead(500, {'content-type': 'application/json'});
      res.end(JSON.stringify({message: error.message}));
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const output = path.join(dir, `${kind}-action-output.txt`);
  fs.writeFileSync(output, '');
  const env = {...process.env,
    GITHUB_API_URL: origin, GITHUB_GRAPHQL_URL: `${origin}/graphql`,
    GITHUB_TOKEN: 'probe-no-github-permissions', INPUT_TOKEN: 'probe-no-github-permissions',
    ACTIONS_ID_TOKEN_REQUEST_URL: `${origin}/oidc?audience=pages`, ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'probe-only',
    GITHUB_OUTPUT: output,
    INPUT_ARTIFACT_NAME: process.env.COMPAT_ARTIFACT,
    INPUT_TIMEOUT: '60000', INPUT_REPORTING_INTERVAL: '100', INPUT_ERROR_COUNT: '2', INPUT_PREVIEW: 'false',
    INPUT_TAG_NAME: 'runner-compat-fixture', INPUT_TARGET_COMMITISH: process.env.GITHUB_SHA,
    INPUT_NAME: 'Isolated compatibility probe', INPUT_BODY_PATH: path.join(dir, 'body.md'),
    INPUT_DRAFT: 'false', INPUT_PRERELEASE: 'true', INPUT_MAKE_LATEST: 'false',
    INPUT_FAIL_ON_UNMATCHED_FILES: 'true', INPUT_FILES: path.join(dir, 'probe.txt'), INPUT_OVERWRITE_FILES: 'false'};
  const child = spawn(process.execPath, [path.resolve(`.probe/upstream/${kind}/dist/index.js`)], {env, stdio: 'inherit'});
  const timer = setTimeout(() => child.kill('SIGKILL'), 70000);
  const code = await new Promise((resolve, reject) => {child.on('error', reject); child.on('close', resolve);});
  clearTimeout(timer);
  await new Promise(resolve => server.close(resolve));
  const report = {kind, runtime: process.version, openssl: process.versions.openssl, api: 'loopback-only (not a live publication)', realPagesArtifactDiscovery: kind === 'pages', calls, code, unexpected, uploadBytes};
  fs.writeFileSync(path.join(dir, `${kind}-action.json`), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
  assert.equal(code, 0);
  assert.equal(unexpected, false);
  if (kind === 'pages') {
    assert.ok(calls.includes('GET /oidc'));
    assert.ok(calls.includes('POST /repos/airanluo-dot/DropSpace/pages/deployments'));
    assert.ok(calls.includes('GET /repos/airanluo-dot/DropSpace/pages/deployments/compat'));
  } else {
    assert.ok(calls.includes('POST /repos/airanluo-dot/DropSpace/releases'));
    assert.equal(uploadBytes, fixture.length);
  }
})().catch(error => {console.error(error); process.exitCode = 1;});
