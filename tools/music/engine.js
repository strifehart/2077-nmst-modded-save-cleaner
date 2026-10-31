// Synthwave engine for the app's background music: five tracks built from oscillators and noise with the Web
// Audio API, arranged Intro (8 bars) · Build (4) · Drop (8) · Breakdown (8) · Drop 2 (8) · Outro (8, fades out).
// render.html renders each track offline to WAV; serve.js saves them; ffmpeg turns them into OGG for the app.
'use strict';

const TRACKS = [
  { id: 'overdrive', title: 'Overdrive', bpm: 128, ch: [[58,61,65],[54,58,61],[56,61,65],[56,60,63]], br: [46,42,37,44], bs: 'roll16', dist: 1, arp: 0,
    ld: [[0,77,4],[4,73,4],[8,77,4],[12,80,4],[16,78,8],[24,77,4],[28,73,4],[32,73,4],[36,77,4],[40,80,8],[48,80,4],[52,78,4],[56,75,8]], lw: 'sawtooth',
    k: 'x...x...x...x...', s: '....x.......x...', h: 'xxxxxxxxxxxxxxxx', o: '..x...x...x...x.' },
  { id: 'chrome-highway', title: 'Chrome Highway', bpm: 132, ch: [[60,63,67],[56,60,63],[58,63,67],[58,62,65]], br: [36,44,39,46], bs: 'gallop', arp: 1,
    ld: [[0,72,2],[2,75,2],[4,79,4],[8,77,4],[12,75,4],[16,75,8],[24,72,8],[32,70,2],[34,74,2],[36,75,4],[40,79,4],[44,82,4],[48,81,12],[60,79,4]], lw: 'sawtooth',
    k: 'x...x...x...x...', s: '....x.......x...', h: '..x...x...x...x.', o: '' },
  { id: 'laser-city', title: 'Laser City', bpm: 140, ch: [[62,65,69],[58,62,65],[57,60,65],[60,64,67]], br: [38,46,41,48], bs: 'oct16', arp: 2,
    ld: [[0,81,8],[8,79,8],[16,82,12],[28,81,4],[32,81,8],[40,77,8],[48,79,16]], lw: 'triangle',
    k: 'x...x...x...x...', s: '....x.......x...', h: '.x.x.x.x.x.x.x.x', o: '' },
  { id: 'turbo-heart', title: 'Turbo Heart', bpm: 150, ch: [[55,58,62],[55,58,63],[53,58,62],[53,57,60]], br: [43,39,46,41], bs: 'oct8', arp: 0,
    ld: [[0,74,2],[2,74,2],[4,77,4],[8,74,2],[10,72,2],[12,70,4],[16,70,2],[18,70,2],[20,74,4],[24,72,4],[28,70,4],[32,74,2],[34,74,2],[36,77,4],[40,79,4],[44,82,4],[48,81,8],[56,77,8]], lw: 'square',
    k: 'x.....x...x.....', s: '....x.......x...', h: 'x.x.x.x.x.x.x.x.', o: '' },
  { id: 'neon-afterhours', title: 'Neon Afterhours', bpm: 128, ch: [[59,62,64,67],[59,60,64,67],[57,60,64,67],[57,59,62,66]], br: [40,36,45,47], bs: 'offbeat', arp: 0,
    ld: [[2,76,2],[6,79,2],[10,76,2],[14,74,2],[18,72,2],[22,76,2],[26,72,2],[30,71,2],[34,72,2],[38,76,2],[42,79,2],[46,81,2],[50,79,2],[54,78,2],[58,74,4]], lw: 'square',
    k: 'x...x...x...x...', s: '....x.......x...', h: 'x.x.x.x.x.x.x.x.', o: '..x...x...x...x.' },
];

const SEC = [{ n: 'Intro', b: 8 }, { n: 'Build', b: 4 }, { n: 'Drop', b: 8 }, { n: 'Breakdown', b: 8 }, { n: 'Drop 2', b: 8 }, { n: 'Outro', b: 8 }];
const BARS = SEC.reduce((a, s) => a + s.b, 0), STEPS = BARS * 16;
const START = 0.05;   // seconds of silence before the first note
const TAIL = 3;       // reverb/delay tail after the last step
const f = m => 440 * Math.pow(2, (m - 69) / 12);
function where(bar) { let a = 0; for (let i = 0; i < SEC.length; i++) { if (bar < a + SEC[i].b) return { i, in: bar - a, len: SEC[i].b }; a += SEC[i].b } return null }

// timing the app needs for crossfades: the outro starts 8 bars from the end and already fades to silence
function timing(l) {
  const bar = 60 / l.bpm * 4;
  return { duration: START + BARS * bar + TAIL, outroStart: START + (BARS - 8) * bar };
}

function engine(ctx, dest) {
  const E = { ctx };
  E.comp = ctx.createDynamicsCompressor(); E.comp.threshold.value = -14; E.comp.ratio.value = 4;
  E.fade = ctx.createGain(); E.comp.connect(E.fade); E.fade.connect(dest);
  E.rev = ctx.createConvolver();
  const len = ctx.sampleRate * 2.4, b = ctx.createBuffer(2, len, ctx.sampleRate);
  let seed = 1; const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647 * 2 - 1;   // deterministic renders
  for (let c = 0; c < 2; c++) { const d = b.getChannelData(c); for (let i = 0; i < len; i++) d[i] = rnd() * Math.pow(1 - i / len, 2.8) }
  E.rev.buffer = b; const rg = ctx.createGain(); rg.gain.value = 0.4; E.rev.connect(rg); rg.connect(E.comp);
  E.dly = ctx.createDelay(2); const fb = ctx.createGain(); fb.gain.value = 0.36; const dl = ctx.createBiquadFilter(); dl.type = 'lowpass'; dl.frequency.value = 2600;
  E.dly.connect(dl); dl.connect(fb); fb.connect(E.dly); const dg = ctx.createGain(); dg.gain.value = 0.45; dl.connect(dg); dg.connect(E.comp);
  E.pad = ctx.createGain(); E.pad.connect(E.comp); const ps = ctx.createGain(); ps.gain.value = 0.6; E.pad.connect(ps); ps.connect(E.rev);
  E.curve = new Float32Array(1025); for (let i = 0; i < 1025; i++) E.curve[i] = Math.tanh((i / 512 - 1) * 4);   // odd length: input 0 maps exactly to 0
  E.nb = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate); const nd = E.nb.getChannelData(0); for (let i = 0; i < nd.length; i++) nd[i] = rnd();
  E.rnd = () => (rnd() + 1) / 2;
  return E;
}
function out(E, node, dry, wet, del) {
  const c = E.ctx, g = c.createGain(); g.gain.value = dry; node.connect(g); g.connect(E.comp);
  if (wet) { const w = c.createGain(); w.gain.value = wet; node.connect(w); w.connect(E.rev) }
  if (del) { const x = c.createGain(); x.gain.value = del; node.connect(x); x.connect(E.dly) }
}
function voice(E, m, t, dur, { w = 'sawtooth', g = 0.1, cut = 1800, a = 0.01, r = 0.15, det = [0], dst = null, dry = 1, wet = 0, del = 0, vib = 0, drive = 0 } = {}) {
  const c = E.ctx, lp = c.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = cut; lp.Q.value = 2;
  // gain must be 0 before the note: offline renders create every note up front, and a gain param sits at its
  // default (1) until its first event, so anything a pending note leaks (the waveshaper's offset) would add up
  const env = c.createGain(); env.gain.value = 0; env.gain.setValueAtTime(0.0001, t); env.gain.exponentialRampToValueAtTime(g, t + a);
  env.gain.setValueAtTime(g, t + Math.max(a, dur - 0.02)); env.gain.exponentialRampToValueAtTime(0.0001, t + dur + r);
  let head = lp; if (drive) { const ws = c.createWaveShaper(); ws.curve = E.curve; const pre = c.createGain(); pre.gain.value = 2.5; lp.connect(pre); pre.connect(ws); head = ws }
  det.forEach(dt => {
    const o = c.createOscillator(); o.type = w; o.frequency.value = f(m); o.detune.value = dt;
    if (vib) { const lfo = c.createOscillator(), lg = c.createGain(); lfo.frequency.value = 5.4; lg.gain.value = vib; lfo.connect(lg); lg.connect(o.detune); lfo.start(t); lfo.stop(t + dur + r + 0.05) }
    o.connect(lp); o.start(t); o.stop(t + dur + r + 0.05)
  });
  head.connect(env); if (dst) env.connect(dst); else out(E, env, dry, wet, del);
}
function noise(E, t, dur, { hp = 6000, g = 0.1, bp = 0, wet = 0 } = {}) {
  const c = E.ctx, n = c.createBufferSource(); n.buffer = E.nb;
  const fl = c.createBiquadFilter(); fl.type = bp ? 'bandpass' : 'highpass'; fl.frequency.value = bp || hp;
  const e = c.createGain(); e.gain.value = 0; e.gain.setValueAtTime(g, t); e.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  n.connect(fl); fl.connect(e); out(E, e, 1, wet, 0); n.start(t, E.rnd() * 1.5, dur + 0.05);
}
function riser(E, t, dur) {
  const c = E.ctx, n = c.createBufferSource(); n.buffer = E.nb; n.loop = true;
  const fl = c.createBiquadFilter(); fl.type = 'bandpass'; fl.Q.value = 3; fl.frequency.setValueAtTime(300, t); fl.frequency.exponentialRampToValueAtTime(7000, t + dur);
  const e = c.createGain(); e.gain.setValueAtTime(0.0001, t); e.gain.exponentialRampToValueAtTime(0.16, t + dur); e.gain.linearRampToValueAtTime(0.0001, t + dur + 0.05);
  n.connect(fl); fl.connect(e); out(E, e, 1, 0.5, 0); n.start(t); n.stop(t + dur + 0.1);
}
function kick(E, t, hard) {
  const c = E.ctx, o = c.createOscillator(), e = c.createGain(); o.frequency.setValueAtTime(hard ? 160 : 140, t); o.frequency.exponentialRampToValueAtTime(44, t + 0.11);
  e.gain.setValueAtTime(0.95, t); e.gain.exponentialRampToValueAtTime(0.0001, t + 0.32); o.connect(e); e.connect(E.comp); o.start(t); o.stop(t + 0.35);
  E.pad.gain.cancelScheduledValues(t); E.pad.gain.setValueAtTime(0.22, t); E.pad.gain.linearRampToValueAtTime(1, t + 0.22);
}
function snare(E, t, g = 0.36) { noise(E, t, 0.24, { bp: 1900, g, wet: 0.45 }); voice(E, 50, t, 0.04, { w: 'triangle', g: g / 3, cut: 4000, r: 0.08 }) }

function doStep(E, l, gs, t) {
  const sd = 60 / l.bpm / 4, bar = Math.floor(gs / 16), b16 = gs % 16, w = where(bar); if (!w) return;
  const sec = SEC[w.i].n, pb = w.in % 4, s = pb * 16 + b16, ch = l.ch[pb], root = l.br[pb];
  const drop = sec === 'Drop' || sec === 'Drop 2', lastBar = w.in === w.len - 1;
  if (b16 === 0 && w.in === 0) {
    if (drop) noise(E, t, 1.8, { hp: 3500, g: 0.22, wet: 0.7 });
    if (sec === 'Build') riser(E, t, sd * 64);
    if (sec === 'Outro') { E.fade.gain.setValueAtTime(1, t + sd * 64); E.fade.gain.linearRampToValueAtTime(0.0001, t + sd * 128) }
  }
  if (sec === 'Breakdown' && w.in === 6 && b16 === 0) riser(E, t, sd * 32);
  const kOn = drop || (sec === 'Build' && w.in < 3 && b16 % 8 === 0) || (sec === 'Outro' && w.in < 4);
  if (kOn && (drop || sec === 'Outro' ? l.k[b16] === 'x' : true)) kick(E, t, l.dist);
  if ((drop || (sec === 'Outro' && w.in < 4)) && l.s[b16] === 'x') snare(E, t);
  if (sec === 'Build' && w.in === 2 && b16 % 4 === 0) snare(E, t, 0.18);
  if ((sec === 'Build' || sec === 'Breakdown') && lastBar) snare(E, t, 0.08 + 0.3 * b16 / 15);
  const hatsOn = drop || sec === 'Build' || (sec === 'Intro' && w.in >= 4) || (sec === 'Outro' && w.in < 6);
  if (hatsOn && l.h[b16] === 'x') noise(E, t, 0.035, { hp: 8500, g: (l.h.indexOf('.') < 0 ? 0.045 : 0.075) * (sec === 'Intro' ? 0.6 : 1) });
  if (drop && l.o && l.o[b16] === 'x') noise(E, t, 0.16, { hp: 7000, g: 0.06 });
  if (b16 === 0) {
    const cut = sec === 'Intro' ? 300 + 1700 * (w.in / w.len) : sec === 'Breakdown' ? 1300 : 2000;
    ch.forEach(m => voice(E, m, t, sd * 16 - 0.05, { w: 'sawtooth', g: 0.04, cut, a: 0.2, r: 0.5, det: [-9, 9], dst: E.pad }))
  }
  const bassOn = drop || sec === 'Build' || (sec === 'Outro' && w.in < 4);
  if (bassOn) {
    const bass = (m, len, g, cut) => voice(E, m, t, len, { g, cut, r: 0.04, drive: l.dist ? 1 : 0 });
    if (l.bs === 'oct8' && b16 % 2 === 0) bass(root + (b16 % 4 ? 12 : 0), sd * 1.6, 0.22, 750);
    if (l.bs === 'roll16') bass(root, sd * 0.8, b16 % 4 === 0 ? (l.dist ? 0.16 : 0.26) : (l.dist ? 0.11 : 0.17), b16 % 4 === 0 ? 1000 : 600);
    if (l.bs === 'oct16') bass(root + (b16 % 2 ? 12 : 0), sd * 0.8, 0.2, 850);
    if (l.bs === 'gallop' && [0, 2, 3].includes(b16 % 4)) bass(root + (b16 % 4 === 0 ? 0 : 12), sd * 0.85, b16 % 4 === 0 ? 0.24 : 0.16, b16 % 4 === 0 ? 900 : 650);
    if (l.bs === 'offbeat' && b16 % 4 === 2) bass(root, sd * 1.7, 0.26, 800);
  }
  const arpStyle = l.arp || ((sec === 'Intro' || sec === 'Breakdown' || sec === 'Outro') ? 1 : 0);
  if (arpStyle) {
    const soft = !l.arp || sec === 'Breakdown'; const tones = arpStyle === 2 ? [0, 1, 2, 1, 0, 1, 2, 3] : [0, 1, 2, 1]; const idx = tones[b16 % tones.length];
    const m = (idx === 3 ? ch[0] + 12 : ch[idx]) + 12;
    voice(E, m, t, sd * 0.7, { w: 'square', g: (arpStyle === 2 ? 0.055 : 0.04) * (soft ? 0.7 : 1), cut: sec === 'Intro' ? 600 + 2400 * (w.in / w.len) : (arpStyle === 2 ? 3400 : 2400), r: 0.05, dry: 0.8, wet: 0.15, del: 0.55 })
  }
  const leadOn = drop || sec === 'Breakdown' || (sec === 'Outro' && w.in < 4);
  if (leadOn) l.ld.forEach(([st, m, len]) => {
    if (st !== s) return; const tri = l.lw === 'triangle', bd = sec === 'Breakdown';
    const o = { w: l.lw, g: (tri ? 0.11 : (l.dist ? 0.07 : 0.06)) * (bd ? 0.8 : 1), cut: tri ? 3000 : (l.dist ? 3400 : 2600) * (bd ? 0.7 : 1), a: bd ? 0.06 : 0.015, r: bd ? 0.5 : 0.25, det: tri ? [0] : [-7, 7], dry: 0.8, wet: bd ? 0.7 : 0.4, del: 0.45, vib: tri ? 12 : 6 };
    voice(E, m, t, sd * len - 0.02, o);
    if (sec === 'Drop 2') voice(E, m + 12, t, sd * len - 0.02, { ...o, g: o.g * 0.55 })
  });
}

async function renderTrack(l, sampleRate = 44100) {
  const { duration } = timing(l), sd = 60 / l.bpm / 4;
  const oc = new OfflineAudioContext(2, Math.ceil(sampleRate * duration), sampleRate);
  const g = oc.createGain(); g.gain.value = 0.55; g.connect(oc.destination);
  const E = engine(oc, g); E.dly.delayTime.value = 60 / l.bpm * 0.75;
  for (let s = 0; s < STEPS; s++) doStep(E, l, s, START + s * sd);
  return oc.startRendering();
}

function wav(buf) {
  const ch = buf.numberOfChannels, n = buf.length, dv = new DataView(new ArrayBuffer(44 + n * ch * 2));
  const w = (o, s) => { for (let i = 0; i < s.length; i++) dv.setUint8(o + i, s.charCodeAt(i)) };
  w(0, 'RIFF'); dv.setUint32(4, 36 + n * ch * 2, true); w(8, 'WAVE'); w(12, 'fmt '); dv.setUint32(16, 16, true); dv.setUint16(20, 1, true); dv.setUint16(22, ch, true);
  dv.setUint32(24, buf.sampleRate, true); dv.setUint32(28, buf.sampleRate * ch * 2, true); dv.setUint16(32, ch * 2, true); dv.setUint16(34, 16, true); w(36, 'data'); dv.setUint32(40, n * ch * 2, true);
  const data = []; for (let c = 0; c < ch; c++) data.push(buf.getChannelData(c)); let o = 44;
  for (let i = 0; i < n; i++) for (let c = 0; c < ch; c++) { const s = Math.max(-1, Math.min(1, data[c][i])); dv.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7FFF, true); o += 2 }
  return new Blob([dv], { type: 'audio/wav' });
}
