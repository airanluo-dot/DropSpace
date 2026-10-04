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
if(!/^v\d+\.\d+\.\d+-beta\.[1-9]\d*$/.test(version))throw new Error('Expected an exact Beta release version.');
if(!Number.isFinite(Date.parse(acceptedAt))||Date.parse(acceptedAt)>Date.now())throw new Error('Use the actual, non-future owner authorization timestamp.');
if(path.isAbsolute(decision)||decision.split(/[\\/]/).includes('..')||!decision.startsWith('scripts/ai-model-qa/evidence/'))throw new Error('Owner decision must be a repository evidence file.');
const read=p=>fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
const hash=s=>createHash('sha256').update(s).digest('hex');
const old=read('RELEASE_VERSION').trim();
if(version===old)throw new Error('Version is already current; refusing to overwrite release evidence.');
const oldScope=readScope(root);
const approval=JSON.parse(read('scripts/ai-model-qa/release-approval.json'));
const review=JSON.parse(read(approval.review.path));
if(JSON.stringify(oldScope)!==JSON.stringify(approval.scope)||JSON.stringify(oldScope)!==JSON.stringify(review.scope))throw new Error('Existing release inputs differ from approved scope; explicit review required before preparation.');
const decisionText=read(decision);
if(!decisionText.includes(version))throw new Error('Owner decision must name the exact target version.');
const notes=read(`.github/release-notes/${version}.md`);
if(!notes.includes(`${old} is the immediate upgrade baseline`))throw new Error('Release notes must identify the current release as upgrade baseline.');
const reviewPath=`scripts/ai-model-qa/evidence/${version}-owner-accepted-review.json`;
if(fs.existsSync(reviewPath))throw new Error('Target review already exists; refusing to replace evidence.');
const n=version.split('.').at(-1);
const edits=new Map([['RELEASE_VERSION',version+'\n']]);
for(const file of ['README.md','ROADMAP.md']) {
 let text=read(file);
 text=text.replace(/v\d+\.\d+\.\d+-beta\.\d+ is the immediate upgrade baseline/g,'__UPGRADE_BASELINE__');
 text=text.split(old).join(version).replace(/\(Beta \d+\)/g,`(Beta ${n})`);
 text=text.replaceAll('__UPGRADE_BASELINE__',`${old} is the immediate upgrade baseline`);
 edits.set(file,text);
}
const gate='scripts/test-ai-release-approval.mjs';
edits.set(gate,read(gate).replace(/export const experimentalBetaVersion = '[^']+';/,`export const experimentalBetaVersion = '${version}';`));
const gateTests='scripts/test-ai-release-approval.test.mjs';
const next=version.replace(/\d+$/,String(Number(n)+1));
let testText=read(gateTests).split(old).join(version);
testText=testText.replace(/(\['future Beta', x => \{ x.write\('RELEASE_VERSION', ')[^']+('; x.scope.releaseVersion = ')[^']+(')/,`$1${next}$2${next}$3`);
edits.set(gateTests,testText);
// readScope is unchanged except for this explicitly supplied release identity.
const scope={...oldScope,releaseVersion:version};
approval.scope=review.scope=scope;
review.reviewedAt=new Date().toISOString();review.expiresAt=new Date(Date.now()+7*86400000).toISOString();
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
console.log(`Prepared ${version}: one version updated metadata, owner acceptance and evidence hashes. No build or publication started.`);
