import { test, expect } from '@playwright/test';

async function geometry(story) {
  return story.evaluate(element=>{
    const island=element.querySelector('.lyrics-island'),box=island.getBoundingClientRect();
    const halo=element.querySelector('canvas').getBoundingClientRect();
    const viewport=element.querySelector('[data-lyrics-viewport]').getBoundingClientRect();
    const track=element.querySelector('.lyrics-track').getBoundingClientRect();
    const lines=element.querySelector('.lyrics-lines'),lineBox=lines.getBoundingClientRect();
    const playback=element.querySelector('.lyrics-playback').getBoundingClientRect();
    const controls=element.querySelector('.lyrics-demo-controls').getBoundingClientRect();
    return {width:box.width,height:box.height,logicalWidth:island.clientWidth,logicalHeight:island.clientHeight,haloWidth:halo.width,haloHeight:halo.height,viewportWidth:viewport.width,contained:lineBox.top>=track.bottom&&lineBox.bottom<=playback.top+.1&&lineBox.left>=box.left&&lineBox.right<=box.right+.1,horizontal:lines.scrollWidth<=lines.clientWidth,scrollable:lines.scrollHeight>lines.clientHeight,controlsTop:controls.top,islandBottom:box.bottom,pageHeight:document.documentElement.scrollHeight,stageHeight:element.querySelector('.lyrics-stage').getBoundingClientRect().height,pageFits:document.documentElement.scrollWidth<=innerWidth};
  });
}
async function expectFixedIsland(story) {
  await expect.poll(()=>story.locator('.lyrics-island').evaluate(el=>el.getBoundingClientRect().width/el.getBoundingClientRect().height)).toBeCloseTo(560/340,5);
  const g=await geometry(story);
  expect(g.logicalWidth).toBe(560);expect(g.logicalHeight).toBe(340);
  expect(g.width/560).toBeCloseTo(g.haloWidth/640,5);
  expect(g.width).toBeLessThanOrEqual(g.viewportWidth+.1);
  expect(g.contained).toBe(true);expect(g.horizontal).toBe(true);expect(g.pageFits).toBe(true);
  expect(g.controlsTop).toBeGreaterThan(g.islandBottom);
  return g;
}
async function capture(page,story,name) {
  // Isolated module captures omit fixed navigation; production is unchanged.
  await page.locator('.site-header,.skip-link').evaluateAll(elements=>elements.forEach(element=>element.remove()));
  await story.screenshot({animations:'disabled',path:`test-results/${name}.png`});
}
for(const locale of ['en','zh-cn']) {
  test(`AI lyrics preserves fixed desktop/mobile proportions and exterior glow (${locale})`,async({page})=>{
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    await page.goto(`/DropSpace/${locale}/`);
    const story=page.locator('#ai-lyrics'),stage=story.locator('[data-lyrics-demo]');
    await story.scrollIntoViewIfNeeded();
    await expect(story.locator('input,[data-lyrics-size]')).toHaveCount(0);
    await expect(story).not.toContainText(locale==='zh-cn'?'调整歌词字体':'Adjust lyric fonts');
    expect(await story.locator('audio').evaluate(audio=>audio.paused)).toBe(true);
    const desktop=await expectFixedIsland(story),desktopRatio=desktop.width/desktop.height;
    await story.locator('[data-glow-choice="ai"]').click();await expect(stage).toHaveAttribute('data-glow-mode','ai');
    const halo=await story.locator('canvas').evaluate(canvas=>{
      const pixels=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;
      const alpha=(x,y)=>pixels[(y*canvas.width+x)*4+3];
      return {center:alpha(Math.floor(canvas.width/2),Math.floor(canvas.height/2)),nonzero:pixels.filter((v,i)=>i%4===3&&v>0).length,edge:Math.max(...Array.from({length:canvas.width},(_,x)=>alpha(x,0)))};
    });
    expect(halo.center).toBe(0);expect(halo.edge).toBe(0);expect(halo.nonzero).toBeGreaterThan(10000);
    await story.locator('[data-glow-choice="off"]').focus();await page.keyboard.press('Space');
    await expect(stage).toHaveAttribute('data-glow-mode','off');
    expect(await story.locator('canvas').evaluate(canvas=>canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data.some((v,i)=>i%4===3&&v>0))).toBe(false);
    await story.locator('[data-glow-choice="music"]').click();
    await expect(story.locator('.lyrics-translation')).toHaveAttribute('lang',locale==='zh-cn'?'zh-CN':'en');
    await expect(story.locator('.lyrics-current')).not.toHaveText(await story.locator('.lyrics-translation').textContent());
    await page.emulateMedia({reducedMotion:'reduce'});
    const still=await story.locator('canvas').evaluate(canvas=>canvas.toDataURL());
    await page.waitForTimeout(160);expect(await story.locator('canvas').evaluate(canvas=>canvas.toDataURL())).toBe(still);
    await page.emulateMedia({reducedMotion:'no-preference'});
    await capture(page,story,`ai-lyrics-${locale}-desktop`);
    await story.locator('[data-lyrics-play]').click();await expect(story.locator('[data-lyrics-play]')).toHaveAttribute('aria-pressed','true');
    await expect.poll(()=>story.locator('audio').evaluate(audio=>audio.currentTime)).toBeGreaterThan(0);
    await story.locator('[data-lyrics-play]').click();await expect(story.locator('[data-lyrics-play]')).toHaveAttribute('aria-pressed','false');
    for(const viewport of [{width:320,height:844},{width:390,height:844},{width:844,height:390},{width:390,height:844},{width:768,height:844}]) {
      await page.setViewportSize(viewport);await story.scrollIntoViewIfNeeded();
      await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
      const mobile=await expectFixedIsland(story);expect(mobile.width/mobile.height).toBeCloseTo(desktopRatio,5);
      await capture(page,story,`ai-lyrics-${locale}-${viewport.width}`);
    }
    await page.emulateMedia({forcedColors:'active'});await expect(story.locator('canvas')).toBeHidden();
    await expect(story.locator('[data-glow-choice="music"]')).toBeVisible();expect(errors).toEqual([]);
  });

  test(`Long CN/EN lyrics remain bounded without moving controls through rotation (${locale})`,async({page})=>{
    await page.goto(`/DropSpace/${locale}/`);await page.emulateMedia({reducedMotion:'reduce'});
    const story=page.locator('#ai-lyrics');
    const short=await story.locator('.lyrics-lines p').allTextContents();
    const english='A long line of original lyrics keeps its own meaning while the day moves on, without changing the shape of the Island. '.repeat(5)+'unbrokentext'.repeat(30);
    const chinese='这是一段很长的中文歌词，用来验证文字仍留在灵动岛内部的歌词视口，不挤压控制按钮，也不改变灵动岛的外形。'.repeat(6);
    for(const viewport of [{width:1440,height:900},{width:320,height:844},{width:390,height:844},{width:844,height:390},{width:390,height:844}]) {
      await page.setViewportSize(viewport);
      await story.locator('.lyrics-lines p').evaluateAll((nodes,texts)=>nodes.forEach((node,index)=>{node.textContent=texts[index];}),short);
      await story.scrollIntoViewIfNeeded();
      const before=await expectFixedIsland(story);
      await story.locator('.lyrics-lines').evaluate((el,text)=>{
        el.querySelector('.lyrics-current').textContent=text.original;
        el.querySelector('.lyrics-translation').textContent=text.translation;
        el.scrollTop=0;
      },{original:locale==='en'?chinese:english,translation:locale==='en'?english:chinese});
      const after=await expectFixedIsland(story);expect(after.scrollable).toBe(true);
      for(const key of ['width','height','haloWidth','haloHeight','controlsTop','pageHeight','stageHeight']) expect(after[key],key).toBeCloseTo(before[key],3);
      const scroller=story.locator('.lyrics-lines');await scroller.focus();await scroller.press('PageDown');
      await expect.poll(()=>scroller.evaluate(el=>el.scrollTop)).toBeGreaterThan(0);
      await scroller.press('Home');
    }
  });
}

test('Mobile glow and audio controls remain touch-sized outside the scaled mockup',async({browser})=>{
  const context=await browser.newContext({viewport:{width:390,height:844},hasTouch:true,isMobile:true});
  const page=await context.newPage();await page.goto('http://127.0.0.1:4173/DropSpace/zh-cn/');
  const story=page.locator('#ai-lyrics');await story.locator('[data-glow-choice="off"]').tap();
  await expect(story.locator('[data-lyrics-demo]')).toHaveAttribute('data-glow-mode','off');
  await story.locator('[data-glow-choice="music"]').tap();
  const play=story.locator('[data-lyrics-play]');expect((await play.boundingBox()).height).toBeGreaterThanOrEqual(44);
  await play.tap();await expect(play).toHaveAttribute('aria-pressed','true');await play.tap();
  await expectFixedIsland(story);await context.close();
});

test('GPT static showcase retains matching layout without release polling or removed controls',async({page})=>{
  const requests=[];page.on('request',request=>requests.push(request.url()));
  for(const locale of ['en','zh-cn']) {
    await page.goto(`http://127.0.0.1:4174/${locale}/index.html`);
    await expect(page.locator('html')).toHaveAttribute('data-site-variant','static');
    await expect(page.locator('[data-stable-version],[data-latest-change],.stable-line')).toHaveCount(0);
    await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute('href','https://github.com/airanluo-dot/DropSpace/releases/latest/download/DropSpaceSetup.exe');
    await expect(page.locator('.nav-links a').nth(2)).toHaveAttribute('href','https://github.com/airanluo-dot/DropSpace/releases');
    const story=page.locator('#ai-lyrics');await story.scrollIntoViewIfNeeded();
    await expect(story.locator('input,[data-lyrics-size]')).toHaveCount(0);await expectFixedIsland(story);
    await capture(page,story,`ai-lyrics-static-${locale}`);
  }
  expect(requests.filter(url=>/\/api\/|release-data\.json/.test(url))).toEqual([]);
});
