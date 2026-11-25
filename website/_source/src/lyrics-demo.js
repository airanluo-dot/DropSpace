// Product illustration only. No lyric service, model, microphone, or desktop access.
// Full-contour halo from IslandGlowRasterizer.cs, composited into straight-alpha Canvas pixels.
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
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const contrast = matchMedia('(forced-colors: active)');
  const words = new Proxy({}, {get:(_, key) => window.DropSpaceI18n.t('demo.' + String(key))});
  addEventListener('dropspace-language-change', () => {
    status.textContent = reducedMotion.matches && stage.dataset.glowMode !== 'off' ? words.reduced : words[stage.dataset.glowMode];
    stage.querySelector('[data-lyrics-play-label]').textContent = audio.paused ? words.play : words.pause;
  });
  const reach = parseFloat(getComputedStyle(canvas).getPropertyValue('--halo-reach'));
  const overlap = 3, aroundSteps = 256, distanceSteps = (reach + overlap) * 3;
  const lookup = new Uint32Array(aroundSteps * distanceSteps);
  const clamp = value => Math.max(0, Math.min(1, value));
  const smooth = value => { const t = clamp(value); return t * t * (3 - 2 * t); };
  const bells = Float32Array.from({ length: 2049 }, (_, i) => Math.exp(-.5 * (i / 256) ** 2));
  function gaussian(value) {
    const x = Math.abs(value) * 256;
    if (x >= 2048) return 0;
    const i = Math.floor(x);
    return bells[i] + (bells[i + 1] - bells[i]) * (x - i);
  }

  const profiles = Array.from({ length: distanceSteps }, (_, i) => {
    const distance = (i + .5) * (reach + overlap) / distanceSteps - overlap;
    return { distance, base: gaussian(distance / 1.6), core: gaussian(distance / .9),
      coverage: smooth((reach - 1 - distance) / 6) * smooth((distance + overlap) / 1.5) };
  });
  function palette(position, centers) {
    function weight(center, width) {
      const d = Math.abs(position - center - Math.round(position - center));
      return gaussian(d / width);
    }
    const orange = weight(centers[0], .14), pink = weight(centers[1], .16), blue = weight(centers[2], .17);
    const total = orange + pink + blue;
    return [(255 * orange + 255 * pink + 69 * blue) / total,
      (147 * orange + 79 * pink + 137 * blue) / total,
      (78 * orange + 171 * pink + 255 * blue) / total];
  }

  let image, pixels, offsets, indices, visible = false, frame = 0, lastDraw = -Infinity;
  let silentStart = performance.now(), silentOffset = 0;
  let phase = 0, brightness = .08, meanEnergy, pulse = 0;
  function resize() {
    const scale = Math.min(window.devicePixelRatio || 1, 2);
    const width = island.clientWidth, height = island.clientHeight;
    const radius = parseFloat(getComputedStyle(island).borderTopLeftRadius);
    canvas.width = Math.ceil((width + 2 * reach) * scale);
    canvas.height = Math.ceil((height + 2 * reach) * scale);
    image = context.createImageData(canvas.width, canvas.height);
    pixels = new Uint32Array(image.data.buffer);

    const halfWidth = width / 2, halfHeight = height / 2;
    const quarter = halfWidth + halfHeight + (Math.PI / 2 - 2) * radius;
    const perimeter = 4 * quarter;
    const pixelOffsets = [], lookupIndices = [];
    for (let y = 0; y < canvas.height; y++) {
      const dy = (y + .5) / scale - reach - halfHeight;
      const qy = Math.abs(dy) - halfHeight + radius;
      for (let x = 0; x < canvas.width; x++) {
        const dx = (x + .5) / scale - reach - halfWidth;
        const qx = Math.abs(dx) - halfWidth + radius;
        if (qx < 0 && qy < 0) continue;
        const distance = Math.hypot(Math.max(0, qx), Math.max(0, qy)) + Math.min(Math.max(qx, qy), 0) - radius;
        if (distance <= -overlap || distance >= reach - 1) continue;

        // Project the pixel onto the rounded perimeter. A color field keeps its
        // position as it diffuses outward, including around each corner.
        const arc = qx > 0 && qy > 0 ? halfWidth - radius + radius * Math.atan2(qx, qy)
          : qx > qy ? quarter - Math.abs(dy) : Math.abs(dx);
        const length = dy >= 0 ? (dx >= 0 ? arc : perimeter - arc)
          : (dx >= 0 ? 2 * quarter - arc : 2 * quarter + arc);
        const around = (length / perimeter + .75) % 1;
        pixelOffsets.push(y * canvas.width + x);
        lookupIndices.push(Math.floor(around * aroundSteps) * distanceSteps +
          Math.floor((distance + overlap) * distanceSteps / (reach + overlap)));
      }
    }
    offsets = Uint32Array.from(pixelOffsets);
    indices = Uint32Array.from(lookupIndices);
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
    const mode = stage.dataset.glowMode;
    const dt = Math.min(.1, Math.max(0, (now - lastDraw) / 1000));
    lastDraw = now;
    if (mode === 'off' || contrast.matches) {
      context.clearRect(0, 0, canvas.width, canvas.height);
      brightness = pulse = 0;
      meanEnergy = undefined;
      return;
    }
    const time = audio.paused ? (silentOffset + (now - silentStart) / 1000) % 8 : audio.currentTime % 8;
    const energy = mode === 'music' || !audio.paused ? envelope(time) : 0;
    meanEnergy ??= energy;
    const response = clamp(.55 * energy + 4 * (energy - meanEnergy));
    meanEnergy += (energy - meanEnergy) * (1 - Math.exp(-dt / 1.5));
    if (reducedMotion.matches) {
      brightness = .08;
      pulse = 0;
    } else {
      phase += dt;
      const target = .08 + .38 * Math.sqrt(response);
      brightness += (target - brightness) * (1 - Math.exp(-dt / (target > brightness ? .055 : .18)));
      pulse += (response - pulse) * (1 - Math.exp(-dt / (response > pulse ? .045 : .12)));
    }

    const travel = phase * .095;
    const baseCenters = [.08 + travel + .035 * Math.sin(phase * .6),
      .42 + travel + .045 * Math.sin(phase * .55 + 2), .76 + travel + .04 * Math.sin(phase * .5 + 4)];
    const waveCenters = [.1 + phase * .07 + .1 * Math.sin(phase * .35),
      .43 + phase * .08 + .085 * Math.sin(phase * .41 + 2), .74 + phase * .055 + .09 * Math.sin(phase * .3 + 4)];
    const bloomCenters = [.04 + phase * .045 + .07 * Math.sin(phase * .28 + 1),
      .38 + phase * .05 + .08 * Math.sin(phase * .32 + 3), .72 + phase * .04 + .07 * Math.sin(phase * .26 + 5)];
    // The original demo chord spans two of the app's six audio bands.
    const bands = [0, pulse, pulse * .5, 0, 0, 0];
    for (let a = 0; a < aroundSteps; a++) {
      const pos = a / aroundSteps, theta = pos * 2 * Math.PI;
      const band = Math.floor(pos * 6);
      const local = bands[band] + (bands[(band + 1) % 6] - bands[band]) * smooth(pos * 6 - band);
      const motion = Math.sqrt(.55 * local + .45 * pulse);
      const crest = .5 + .5 * (.7 * Math.sin(theta * 2 - phase * 1.1) + .3 * Math.sin(theta * 3 - phase * .7));
      const light = brightness * (.7 + .3 * motion);
      const waveStrength = Math.min(.95, 1.65 * light * (.2 + .8 * motion) * (.65 + .35 * motion * crest));
      const waveWidth = 1.25 + 10 * motion * crest, bloomWidth = 3.2 + 7 * motion * crest;
      const base = palette(pos, baseCenters), wave = palette(pos, waveCenters);
      const bloom = palette(pos, bloomCenters), halo = palette(pos - .08, bloomCenters);
      const core = base.map(channel => .88 * channel + .12 * 255);
      for (let d = 0; d < distanceSteps; d++) {
        const profile = profiles[d];
        const distance = profile.distance;
        // Front-to-back weights of the same five translucent layers as the app.
        const c = .42 * brightness * profile.core;
        const b = (1 - c) * .95 * brightness * profile.base;
        const w = (1 - c - b) * waveStrength * gaussian(distance / waveWidth);
        const m = (1 - c - b - w) * .32 * light * (.35 + .65 * motion * crest) * gaussian(distance / bloomWidth);
        const h = (1 - c - b - w - m) * .16 * light * gaussian(distance / (8.5 + 1.5 * motion));
        const alpha = c + b + w + m + h;
        const opacity = Math.round(255 * alpha * profile.coverage);
        let rgba = opacity << 24;
        if (opacity) {
          for (let k = 0; k < 3; k++) {
            const channel = (c * core[k] + b * base[k] + w * wave[k] + m * bloom[k] + h * halo[k]) / alpha;
            rgba |= Math.round(channel) << (8 * k);
          }
        }
        lookup[a * distanceSteps + d] = rgba;
      }
    }
    for (let i = 0; i < offsets.length; i++) pixels[offsets[i]] = lookup[indices[i]];
    context.putImageData(image, 0, 0);
  }
  function animate(now) {
    frame=0;
    if(!visible || document.hidden || reducedMotion.matches || contrast.matches || stage.dataset.glowMode==='off') return;
    if (now - lastDraw >= 1000 / 30) draw(now);
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
