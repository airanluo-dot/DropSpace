// Product illustration only. No lyric service, model, microphone, or desktop access.
// Halo geometry, three ribbons and palette mirror IslandGlowRasterizer.cs. Canvas
// uses straight-alpha RGBA; the native layered window uses premultiplied BGRA.
(() => {
  const stage = document.querySelector('[data-lyrics-demo]');
  if (!stage) return;
  const island = stage.querySelector('[data-lyrics-island]');
  const viewport = stage.querySelector('[data-lyrics-viewport]');
  const device = stage.querySelector('[data-lyrics-device]');
  const canvas = stage.querySelector('[data-lyrics-halo]');
  const context = canvas.getContext('2d');
  if (!context) return;
  const audio = stage.querySelector('[data-lyrics-audio]');
  const play = stage.querySelector('[data-lyrics-play]');
  const status = stage.querySelector('[data-lyrics-status]');
  const isChinese = document.documentElement.lang.toLowerCase().startsWith('zh');
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const contrast = matchMedia('(forced-colors: active)');
  const words = isChinese ? {
    play: '播放示例', pause: '暂停示例', off: '关闭 · 光晕已隐藏',
    ai: 'AI · 演示翻译工作时的柔和光晕', music: '音乐 · 光晕随示例节奏呼吸',
    reduced: '已遵循减少动态效果设置，光晕保持静止', error: '音频暂不可用，仍可体验无声光效'
  } : {
    play: 'Play sample', pause: 'Pause sample', off: 'Off · the glow is hidden',
    ai: 'AI · a soft glow while translation works', music: 'Music · light follows the sample rhythm',
    reduced: 'Reduced motion is on; the glow stays still', error: 'Audio is unavailable; the silent preview still works'
  };
  const padding = 40, aroundSteps = 256, distanceSteps = 160;
  const paletteColors = [[255,147,78],[255,79,171],[69,137,255]];
  const lookup = new Uint32Array(aroundSteps * distanceSteps);
  let image, pixels, samples = [], visible = false, frame = 0, lastDraw = -Infinity;
  const motionStart = performance.now();
  let silentStart = motionStart, silentOffset = 0;
  const gaussian = value => Math.exp(-.5 * value * value);
  const clamp = value => Math.max(0, Math.min(1, value));
  const palette = position => {
    const p = (position - Math.floor(position)) * 3;
    const segment = Math.floor(p), b = p - segment, blend = b*b*(3-2*b);
    return paletteColors[segment].map((v,i) => v + (paletteColors[(segment+1)%3][i]-v)*blend);
  };
  function resize() {
    const scale = Math.min(window.devicePixelRatio || 1, 1.5);
    const width = island.clientWidth, height = island.clientHeight;
    const radius = parseFloat(getComputedStyle(island).borderTopLeftRadius);
    canvas.width = Math.ceil((width + 2*padding)*scale);
    canvas.height = Math.ceil((height + 2*padding)*scale);
    image = context.createImageData(canvas.width, canvas.height);
    pixels = new Uint32Array(image.data.buffer);
    samples = [];
    const halfWidth = width/2, halfHeight = height/2;
    for (let y=1;y<canvas.height-1;y++) {
      const dy=(y+.5)/scale-padding-halfHeight, qy=Math.abs(dy)-halfHeight+radius;
      for (let x=1;x<canvas.width-1;x++) {
        const dx=(x+.5)/scale-padding-halfWidth, qx=Math.abs(dx)-halfWidth+radius;
        if(qx<=0 && qy<=0) continue;
        const distance=Math.hypot(Math.max(0,qx),Math.max(0,qy))+Math.min(Math.max(qx,qy),0)-radius;
        if(distance<.5/scale || distance>=padding-1) continue;
        const around=(Math.atan2(dy/halfHeight,dx/halfWidth)+Math.PI)/(2*Math.PI);
        const a=Math.min(aroundSteps-1,Math.floor(around*aroundSteps));
        const d=Math.min(distanceSteps-1,Math.floor(distance*distanceSteps/padding));
        samples.push([y*canvas.width+x,a*distanceSteps+d]);
      }
    }
    draw(performance.now());
  }
  // Exactly the envelope used to synthesize assets/lyrics-demo.wav. Use the media
  // clock during playback so music and light do not drift apart or use a fake mic.
  function envelope(t) {
    const beat=(.5+.5*Math.cos(t*Math.PI*2))**6;
    const slow=.5+.5*Math.sin(t*Math.PI/2);
    return (.22+.50*beat+.18*slow)*Math.max(0,Math.min(1,t/.12,(8-t)/.16));
  }
  function draw(now) {
    if (!image) return;
    const mode=stage.dataset.glowMode;
    if (mode==='off' || contrast.matches) { context.clearRect(0,0,canvas.width,canvas.height); return; }
    const time=audio.paused ? (silentOffset+(now-silentStart)/1000)%8 : audio.currentTime%8;
    const phase=reducedMotion.matches ? 1.2 : (now-motionStart)/1000;
    const brightness=reducedMotion.matches ? .62 : mode==='ai' ? .62 : .30+.53*envelope(time);
    for (let a=0;a<aroundSteps;a++) {
      const theta=a*2*Math.PI/aroundSteps;
      const broadCenter=11+3*Math.sin(theta*2-phase*.47);
      const ribbonCenter=4+2.4*Math.sin(theta*3+phase*.63);
      const outerCenter=20+4*Math.sin(theta-phase*.39);
      const broadWidth=11+1.5*Math.cos(theta*2+phase*.29);
      const broadColor=palette(theta/(2*Math.PI)-phase*.018);
      const ribbonColor=palette(theta/(2*Math.PI)+.08+phase*.024);
      const outerColor=palette(theta/(2*Math.PI)+.24-phase*.013);
      for(let d=0;d<distanceSteps;d++) {
        const distance=(d+.5)*padding/distanceSteps;
        const broad=.40*gaussian((distance-broadCenter)/broadWidth);
        const ribbon=.50*gaussian((distance-ribbonCenter)/4.8);
        const outer=.30*gaussian((distance-outerCenter)/6.4);
        const edge=.18*gaussian(distance/2.5), total=broad+ribbon+outer+edge;
        let tail=clamp((padding-1-distance)/9); tail=tail*tail*(3-2*tail);
        const alpha=Math.round(255*clamp(total*brightness*tail));
        const rgb=broadColor.map((c,i)=>Math.round((c*(broad+edge)+ribbonColor[i]*ribbon+outerColor[i]*outer)/total));
        lookup[a*distanceSteps+d]=(alpha<<24)|(rgb[2]<<16)|(rgb[1]<<8)|rgb[0];
      }
    }
    for (const [pixel,index] of samples) pixels[pixel]=lookup[index];
    context.putImageData(image,0,0);
  }
  function animate(now) {
    frame=0;
    if(!visible || document.hidden || reducedMotion.matches || contrast.matches || stage.dataset.glowMode==='off') return;
    if(now-lastDraw>=50) { draw(now); lastDraw=now; }
    frame=requestAnimationFrame(animate);
  }
  function update() {
    cancelAnimationFrame(frame); frame=0;
    status.textContent=reducedMotion.matches && stage.dataset.glowMode!=='off' ? words.reduced : words[stage.dataset.glowMode];
    draw(performance.now());
    if(visible && !document.hidden && !reducedMotion.matches && !contrast.matches && stage.dataset.glowMode!=='off') frame=requestAnimationFrame(animate);
  }
  for (const button of stage.querySelectorAll('[data-glow-choice]')) button.addEventListener('click',()=>{
    stage.dataset.glowMode=button.dataset.glowChoice;
    for(const item of stage.querySelectorAll('[data-glow-choice]')) item.setAttribute('aria-pressed',String(item===button));
    update();
  });
  // The logical desktop/App surface is always 560 × 340, at every viewport.
  // Transforming its common parent scales all text, corners, playback artwork
  // and exterior glow together; outside controls keep touch-sized targets.
  function fitPreview() {
    const scale = Math.min(1, viewport.clientWidth / 560);
    device.style.transform = `scale(${scale})`;
  }
  new ResizeObserver(fitPreview).observe(viewport);
  fitPreview();
  function playbackState() {
    play.setAttribute('aria-pressed',String(!audio.paused));
    stage.querySelector('[data-lyrics-play-label]').textContent=audio.paused?words.play:words.pause;
    if(audio.paused) { silentOffset=audio.currentTime; silentStart=performance.now(); }
  }
  play.addEventListener('click',async()=>{
    if(!audio.paused) audio.pause();
    else { try { await audio.play(); } catch { status.textContent=words.error; } }
    playbackState();
  });
  audio.addEventListener('play',playbackState);
  audio.addEventListener('pause',playbackState);
  new ResizeObserver(resize).observe(island);
  new IntersectionObserver(entries=>{
    visible=entries[0].isIntersecting;
    if(!visible) audio.pause();
    update();
  },{threshold:.05}).observe(stage);
  document.addEventListener('visibilitychange',()=>{ if(document.hidden) audio.pause(); update(); });
  reducedMotion.addEventListener('change',update);
  contrast.addEventListener('change',update);
  resize();
})();
