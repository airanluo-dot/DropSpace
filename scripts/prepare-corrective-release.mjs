// Prepares explicitly authorized, unchanged-model corrective releases from one version.
// Does not publish, grant approval, alter model evidence or run a build.
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {readScope} from './test-ai-release-approval.mjs';
const args=process.argv.slice(2);
const get=name=>{const i=args.indexOf(name); if(i<0||!args[i+1])throw new Error(`Required: ${name}`); return args[i+1];};
const root=process.cwd();
const version=get('--version');
const decision=get('--owner-decision');
const acceptedAt=get('--accepted-at');
const rebindCurrent=args.includes('--rebind-current');
if(!/^v\d+\.\d+\.\d+-beta\.[1-9]\d*$/.test(version))throw new Error('Expected an exact Beta release version.');
if(!Number.isFinite(Date.parse(acceptedAt))||Date.parse(acceptedAt)>Date.now())throw new Error('Use the actual, non-future owner authorization timestamp.');
if(path.isAbsolute(decision)||decision.split(/[\\/]/).includes('..')||!decision.startsWith('scripts/ai-model-qa/evidence/'))throw new Error('Owner decision must be a repository evidence file.');
const read=p=>fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
const hash=s=>createHash('sha256').update(s).digest('hex');
const old=read('RELEASE_VERSION').trim();
if(version===old&&!rebindCurrent)throw new Error('Version is already current; refusing to overwrite release evidence.');
if(rebindCurrent&&version!==old)throw new Error('Current-release source review cannot change the release version.');
const oldScope=readScope(root);
const approval=JSON.parse(read('scripts/ai-model-qa/release-approval.json'));
if(hash(fs.readFileSync(approval.review.path))!==approval.review.sha256)throw new Error('Previous review bytes do not match the active approval pointer.');
const review=JSON.parse(read(approval.review.path));
const reviewedPaths=new Set(args.flatMap((arg,i)=>arg==='--reviewed-source-path'&&args[i+1]?[args[i+1]]:[]));
for(const name of reviewedPaths)if(!oldScope.sources.files.some(file=>file.path===name))throw new Error(`Reviewed source is not a current code-owned fingerprint input: ${name}`);
const admissionIndex=args.indexOf('--reviewed-fixture-admission');
const reviewedAdmission=admissionIndex<0?null:args[admissionIndex+1];
if(admissionIndex>=0) {
 if(reviewedAdmission!==oldScope.fixtureAdmission.path)throw new Error('Reviewed host admission must name the exact current computation path.');
 for(const name of [reviewedAdmission,'src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs','scripts/test-ai-release-approval.mjs'])
  if(!reviewedPaths.has(name))throw new Error(`Host admission renewal needs explicit source review: ${name}`);
 if(oldScope.fixtureAdmission.modelInferenceExecuted!==false||oldScope.fixtureAdmission.semanticApproved!==false)
  throw new Error('Corrective preparation can renew host computation only, never model or semantic evidence.');
}
function reviewedScope(previous) {
 const copy=structuredClone(previous);
 const previousFiles=new Map(copy.sources.files.map(file=>[file.path,file]));
 if(previousFiles.size!==copy.sources.files.length)throw new Error('Previous approval has duplicate source paths.');
 for(const name of previousFiles.keys())if(!oldScope.sources.files.some(file=>file.path===name))throw new Error(`Previously reviewed production input was removed: ${name}`);
 copy.sources.files=oldScope.sources.files.map(current=>{
  const previousFile=previousFiles.get(current.path);
  if(!previousFile&&!reviewedPaths.has(current.path))throw new Error(`New production input needs an exact reviewed source path: ${current.path}`);
  return structuredClone(reviewedPaths.has(current.path)?current:previousFile);
 });
 // A fingerprint algorithm upgrade is code-owned too. It cannot be silently
 // accepted by adding source names while leaving the gate implementation unreviewed.
 if(copy.sources.algorithm!==oldScope.sources.algorithm&&reviewedPaths.has('scripts/test-ai-release-approval.mjs'))copy.sources.algorithm=oldScope.sources.algorithm;
 copy.sources.sha256=hash(JSON.stringify(copy.sources.files));
 if(reviewedAdmission!==null) {
  if(JSON.stringify(previous.fixture)!==JSON.stringify(oldScope.fixture))throw new Error('Host admission renewal cannot change the independent fixture.');
  copy.fixtureAdmission=structuredClone(oldScope.fixtureAdmission);
 }
 return copy;
}
if(JSON.stringify(oldScope)!==JSON.stringify(reviewedScope(approval.scope))||JSON.stringify(oldScope)!==JSON.stringify(reviewedScope(review.scope)))throw new Error('Unreviewed release input changes remain; record exact reviewed source paths before preparation.');
const decisionText=read(decision);
if(!decisionText.includes(version))throw new Error('Owner decision must name the exact target version.');
const notes=read(`.github/release-notes/${version}.md`);
if(!rebindCurrent&&!notes.includes(`${old} is the immediate upgrade baseline`))throw new Error('Release notes must identify the current release as upgrade baseline.');
const reviewPath=rebindCurrent?get('--review-record'):`scripts/ai-model-qa/evidence/${version}-owner-accepted-review.json`;
if(path.isAbsolute(reviewPath)||reviewPath.split(/[\\/]/).includes('..')||
 !reviewPath.startsWith(`scripts/ai-model-qa/evidence/${version}-`)||!reviewPath.endsWith('.json'))
 throw new Error('A fresh review record must be in the exact current Beta evidence namespace.');
if(fs.existsSync(reviewPath))throw new Error('Target review already exists; refusing to replace evidence.');
const n=version.split('.').at(-1);
const previousBeta=old.split('.').at(-1);
const edits=new Map();
if(!rebindCurrent) {
edits.set('RELEASE_VERSION',version+'\n');
for(const file of ['README.md','ROADMAP.md']) {
 let text=read(file);
 text=text.split(old).join(version).replaceAll(`(Beta ${previousBeta})`,`(Beta ${n})`);
 edits.set(file,text);
}
const gate='scripts/test-ai-release-approval.mjs';
edits.set(gate,read(gate).replace(/export const experimentalBetaVersion = '[^']+';/,`export const experimentalBetaVersion = '${version}';`));
const gateTests='scripts/test-ai-release-approval.test.mjs';
const next=version.replace(/\d+$/,String(Number(n)+1));
let testText=read(gateTests).split(old).join(version);
testText=testText.replace(/(\['future Beta', x => \{ x.write\('RELEASE_VERSION', ')[^']+('; x.scope.releaseVersion = ')[^']+(')/,`$1${next}$2${next}$3`);
edits.set(gateTests,testText);
}
// Bind the exact prospective writes, including the gate's own version pin.
// Computing this before changing the pin used to make a prepared approval stale.
const scope={...oldScope,releaseVersion:version};
scope.sources={...oldScope.sources,files:oldScope.sources.files.map(file=>({...file,
 sha256:edits.has(file.path)?hash(edits.get(file.path)):file.sha256}))};
scope.sources.sha256=hash(JSON.stringify(scope.sources.files));
approval.scope=review.scope=scope;
review.reviewedAt=new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');review.expiresAt=new Date(Date.now()+7*86400000).toISOString().replace(/\.\d{3}Z$/, 'Z');
review.reviewedBy=`Local Codex, repository-owner-authorized ${version} source review`;
review.userAcceptance={...review.userAcceptance,releaseVersion:version,reference:decision,acceptedAt,timestampMeaning:'Explicit owner authorization recorded in the linked decision; historical model observations unchanged.'};
review.ownerDecision={path:decision,sha256:hash(decisionText)};
review.summary=`Owner-authorized corrective release ${version}; model inputs and historical evidence unchanged. See exact owner decision.`;
approval.reason=review.summary;
review.candidateValidation={summary:'Release preparation only. No new tests or model validation claimed.'};
const reviewText=JSON.stringify(review,null,2)+'\n';
edits.set(reviewPath,reviewText);approval.review={path:reviewPath,sha256:hash(reviewText)};
edits.set('scripts/ai-model-qa/release-approval.json',JSON.stringify(approval,null,2)+'\n');
// Inputs validated before any writes; publication remains a separate explicit action.
for(const [file,text] of edits)fs.writeFileSync(file,text);
console.log(`${rebindCurrent?'Rebound current source review for':'Prepared'} ${version}: fresh review and evidence hashes. No build or publication started.`);
