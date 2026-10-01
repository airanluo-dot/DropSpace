import {test,expect} from '@playwright/test';

for(const [locale,route] of [['en-US','en'],['zh-CN','zh-cn']]) {
 test(`GPT static root and language navigation use root paths (${locale})`,async({browser})=>{
  const context=await browser.newContext({locale});
  const page=await context.newPage();
  const failures=[],projectPaths=[];
  page.on('response',response=>{
   if(response.url().startsWith('http://127.0.0.1:4174/')&&response.status()>=400) failures.push(`${response.status()} ${response.url()}`);
  });
  page.on('request',request=>{
   if(request.url().startsWith('http://127.0.0.1:4174/DropSpace/')) projectPaths.push(request.url());
  });
  await page.goto('http://127.0.0.1:4174/#ai-lyrics');
  await expect(page).toHaveURL(`http://127.0.0.1:4174/${route}/#ai-lyrics`);
  await expect(page.locator('#ai-lyrics')).toBeVisible();
  await page.locator('[data-language-switch]').click();
  await expect(page).toHaveURL(`http://127.0.0.1:4174/${route==='en'?'zh-cn':'en'}/`);
  await expect(page.locator('h1')).toBeVisible();
  await page.locator('[data-language-switch]').click();
  await expect(page).toHaveURL(`http://127.0.0.1:4174/${route}/`);
  await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute('href','https://github.com/airanluo-dot/DropSpace/releases/latest/download/DropSpaceSetup.exe');
  expect(projectPaths).toEqual([]);
  expect(failures).toEqual([]);
  await context.close();
 });
}
