import {test,expect} from '@playwright/test';
for (const locale of ['en','zh-cn']) {
 test(`Evergreen layout remains usable at every viewport (${locale})`,async({page})=>{
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  await page.emulateMedia({reducedMotion:'reduce'});
  for(const width of [320,390,768,1024,1440]) {
   await page.setViewportSize({width,height:width<700?844:1000});
   await page.goto(`/DropSpace/${locale}/`);
   await expect(page.locator('h1')).toBeVisible();
   const overflow=await page.evaluate(()=>[...document.querySelectorAll('body *')].filter(el=>{const r=el.getBoundingClientRect();return r.width>0&&(r.right>innerWidth+1||r.left< -1)&&getComputedStyle(el).position!=='fixed';}).map(el=>({tag:el.tagName,class:el.className,right:el.getBoundingClientRect().right,text:el.textContent.slice(0,80)})));
   await page.screenshot({path:`test-results/check-${locale}-${width}.png`,fullPage:true});
   expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),JSON.stringify({width,overflow})).toBe(true);
   for(const target of ['.hero','.native-story','#widgets','#music','#download','.faq']) {
    const box=await page.locator(target).boundingBox();
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x+box.width).toBeLessThanOrEqual(width+1);
   }
   await page.screenshot({path:`test-results/home-${locale}-${width}.png`,fullPage:true});
   if(width===1440||width===390) await page.locator('.hero').screenshot({path:`test-results/hero-${locale}-${width}.png`});
  }
  expect(errors).toEqual([]);
 });
}
