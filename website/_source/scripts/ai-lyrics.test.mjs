import assert from 'node:assert/strict';
import {readFile,stat} from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import test from 'node:test';
import {JSDOM} from 'jsdom';
import vm from 'node:vm';
const read = file => readFile(new URL(`../${file}`,import.meta.url),'utf8');

test('AI lyrics story preserves App behavior, cautious copy and language-specific targets',async()=>{
 for(const [locale,lang] of [['en','en'],['zh-cn','zh-CN']]) {
  const doc=new JSDOM(await read(`dist/${locale}/index.html`)).window.document;
  const story=doc.querySelector('#ai-lyrics'); assert.ok(story);
  assert.equal(story.querySelector('.lyrics-translation').lang,lang);
  assert.notEqual(story.querySelector('.lyrics-current').textContent,story.querySelector('.lyrics-translation').textContent);
  assert.equal(story.querySelector('audio').autoplay,false);
  assert.equal(story.querySelectorAll('[data-glow-choice]').length,3);
  assert.equal(story.querySelectorAll('input,[data-lyrics-size],[data-lyrics-size-range],[data-lyrics-size-number]').length,0);
  assert.doesNotMatch(story.textContent,/Beta\s*\d+|all languages|zero errors|always accurate|保证|全语言/);
  assert.ok(story.querySelector('.lyrics-accuracy').textContent.length>30);
  assert.match(story.textContent,/Beta/);
  assert.match(story.textContent,locale==='en'?/off by default/:/默认关闭/);
  assert.match(story.querySelector('.lyrics-accuracy').textContent,locale==='en'?/several minutes/:/数分钟/);
  assert.match(story.querySelector('.lyrics-accuracy').textContent,locale==='en'?/change meaning or omit details/:/误译或遗漏细节/);
 }
});

test('static showcase strips live release state and retains official GitHub destinations',async()=>{
 const result=spawnSync(process.execPath,['scripts/build-static.mjs'],{cwd:new URL('../',import.meta.url),encoding:'utf8'});
 assert.equal(result.status,0,result.stderr);
 const root=new JSDOM(await read('dist-static/index.html')).window.document;
 const redirect=root.querySelector('script:not([src])').textContent;
 for(const [language,expected] of [['en-US','/en/index.html#ai-lyrics'],['zh-CN','/zh-cn/index.html#ai-lyrics']]) {
  let destination;
  vm.runInNewContext(redirect,{navigator:{languages:[language]},location:{hash:'#ai-lyrics',replace:value=>{destination=value;}}});
  assert.equal(destination,expected,'GPT root must not inherit the GitHub Pages project prefix');
 }
 for(const locale of ['en','zh-cn']) {
  const doc=new JSDOM(await read(`dist-static/${locale}/index.html`)).window.document;
  assert.equal(doc.querySelector('[data-language-switch]').getAttribute('href'),locale==='en'?'/zh-cn/index.html':'/en/index.html');
  for(const element of doc.querySelectorAll('[href],[src],[poster]')) {
   for(const attribute of ['href','src','poster']) {
    const value=element.getAttribute(attribute);
    if(!value?.startsWith('/')) continue;
    assert.doesNotMatch(value,/^\/DropSpace(?:\/|$)/,'Static routes and assets are root-relative');
    const pathname=value.split(/[?#]/)[0];
    if(pathname) assert.equal((await stat(new URL(`../dist-static${pathname}`,import.meta.url))).isFile(),true,'Static navigation must resolve to a file, without directory-index rewrites');
   }
  }
  assert.equal(doc.documentElement.dataset.siteVariant,'static');
  assert.equal(doc.querySelectorAll('[data-stable-version],[data-latest-change],.stable-line').length,0);
  assert.ok(doc.querySelector('#ai-lyrics'));
  for(const link of doc.querySelectorAll('[data-download]')) assert.match(link.href,/^https:\/\/github\.com\/airanluo-dot\/DropSpace\/releases\/latest\/download\//);
  assert.equal(doc.querySelector('.nav-links a:nth-child(3)').href,'https://github.com/airanluo-dot/DropSpace/releases');
  const script=doc.querySelector('script[src*="/script."]').getAttribute('src');
  assert.doesNotMatch(await read(`dist-static${script}`),/refreshReleaseData|fetch\(|api\/v1/);
  assert.equal(JSON.parse(doc.querySelector('script[type="application/ld+json"]').textContent).softwareVersion,undefined);
 }
 assert.equal(JSON.parse(await read('dist-static/site.webmanifest')).version,undefined);
 await assert.rejects(stat(new URL('../dist-static/api/v1/releases.json',import.meta.url)),{code:'ENOENT'});
 await assert.rejects(stat(new URL('../dist-static/en/changelog',import.meta.url)),{code:'ENOENT'});
});

test('demo audio is a small original PCM loop, separate from any live data',async()=>{
 const wav=await readFile(new URL('../src/assets/lyrics-demo.wav',import.meta.url));
 assert.equal(wav.subarray(0,4).toString(),'RIFF');
 assert.equal(wav.readUInt32LE(24),24000);
 assert.equal(wav.length,44+24000*8*2);
 const code=await read('src/lyrics-demo.js');
 assert.doesNotMatch(code,/getUserMedia|fetch\(|localStorage|eval\(/);
 assert.match(code,/audio.currentTime/);
 assert.match(code,/prefers-reduced-motion/);
 assert.doesNotMatch(code,/sizeRange|sizeNumber|applySize|data-lyrics-size|fontSize|minimumSize|maximumSize/);
});
