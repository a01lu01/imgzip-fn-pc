'use strict';
// Non-visual checks only. Browser appearance and interaction are reviewed by the user.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const M = require('./demo-model.js');
let checks = 0;
function check(name, run) { run(); checks++; console.log(`PASS ${name}`); }

check('JavaScript syntax', () => {
  for (const file of ['app.js', 'demo-model.js']) {
    const command = spawnSync(process.execPath, ['--check', path.join(__dirname, file)], { encoding: 'utf8' });
    assert.equal(command.status, 0, command.stderr);
  }
});
check('All HTML entry points use existing local resources', () => {
  for (const file of ['index.html', 'a.html', 'b.html', 'c.html']) {
    const html = fs.readFileSync(path.join(__dirname, file), 'utf8');
    const refs = [...html.matchAll(/(?:src|href)="([^"]+)"/g)].map(match => match[1]);
    assert.equal(refs.length, 3);
    for (const ref of refs) {
      assert(!/^(?:https?:)?\/\//.test(ref));
      assert(fs.existsSync(path.join(__dirname, ref)), `${file}: missing ${ref}`);
    }
    assert(html.indexOf('demo-model.js') < html.indexOf('app.js'));
  }
});
check('Theme restoration tolerates missing, malformed, or unrelated configuration', () => {
  for (const value of [null, '', '{', 'null', '{"theme":"unknown"}', '{"server":"unused"}']) assert.equal(M.parseTheme(value), 'system');
  for (const value of ['light', 'dark', 'system']) assert.equal(M.parseTheme(JSON.stringify({ theme: value })), value);
});
check('Presets reset conflicting custom options', () => {
  const state = M.createState();
  Object.assign(state, { lossless: true, maxSize: 999, resize: 'width', noUpscale: false });
  M.applyPreset(state, 'webp-share');
  assert.equal(state.quality, 80); assert.equal(state.pixels, 2560); assert.equal(state.format, 'webp');
  assert.equal(state.lossless, false); assert.equal(state.maxSize, 0); assert.equal(state.resize, 'long'); assert.equal(state.noUpscale, true);
  M.applyPreset(state, 'archive-webp'); assert.equal(state.quality, 90); assert.equal(state.pixels, 4000);
  M.applyPreset(state, 'archive-jpeg'); assert.equal(state.format, 'jpeg');
});
check('Invalid parameters cannot start a demo task; disabled fields are ignored', () => {
  const state = M.createState(); assert.equal(M.validate(state), '');
  for (const [field, value] of [['quality', -1], ['quality', 101], ['quality', NaN], ['pixels', 0], ['pixels', 1.2], ['threads', 0], ['threads', 65], ['maxSize', -1], ['maxSize', Number.MAX_SAFE_INTEGER + 1]]) {
    const invalid = { ...state, [field]: value }; assert.notEqual(M.validate(invalid), '', field);
  }
  assert.equal(M.validate({ ...state, quality: 0 }), '');
  assert.equal(M.validate({ ...state, lossless: true, quality: NaN, maxSize: NaN }), '');
  assert.equal(M.validate({ ...state, resize: 'none', pixels: NaN }), '');
  assert.notEqual(M.validate({ ...state, files: [] }), '');
  assert.notEqual(M.validate({ ...state, unavailable: true }), '');
});
check('Folder recursion changes the task scope without discarding nested files', () => {
  const files = [{ name: '街景.jpg', relative: '街景.jpg' }, { name: '夜色.png', relative: '子目录/夜色.png' }];
  assert.deepEqual(M.scopedFiles(files, 'folder', false), [files[0]]);
  assert.deepEqual(M.scopedFiles(files, 'folder', true), files);
  assert.deepEqual(M.scopedFiles(files, 'files', false), files);
  assert.equal(files.length, 2);
});
check('Partial results count only successful files in the size comparison', () => {
  const state = M.createState(); state.status = 'partial';
  const result = M.result(state);
  assert.equal(result.failed, 2); assert.equal(result.success, 6);
  assert.equal(result.before, state.files.slice(0, 6).reduce((sum, file) => sum + file.size, 0));
  assert.equal(result.saved + result.after, result.before);
  const onlyOne = M.result({ ...state, files: [state.files[0]] });
  assert.equal(onlyOne.success, 0); assert.equal(onlyOne.before, 0);
});
check('Output paths preserve Unicode and keep the source unchanged', () => {
  const state = M.createState();
  state.source = 'D:\\旅行\\한글 & 夏日';
  const source = state.source;
  assert.equal(M.outputPath(state), `${source}_compressed`);
  assert.equal(state.source, source);
  assert.equal(M.outputPath({ ...state, files: [] }), '选择来源后自动生成');
  assert.equal(M.completedCount({ ...state, progress: 100 }), state.files.length);
});
check('Both palettes contain all supplied color tokens with exact values', () => {
  const css = fs.readFileSync(path.join(__dirname, 'styles.css'), 'utf8');
  const light = css.split(':root {')[1].split('}')[0];
  const dark = css.split(':root[data-theme="dark"] {')[1].split('}')[0];
  const tokens = {
    'window-bg': ['#f3f3f3', '#202020'], 'card-bg': ['#ffffff', '#2b2b2b'], text: ['#1f1f1f', '#ffffff'],
    subtext: ['#5d5d5d', '#c7c7c7'], hover: ['#e9e9e9', '#333333'], selected: ['#e3e3e3', '#3a3a3a'],
    border: ['#e0e0e0', '#3c3c3c'], 'input-border': ['#c9c9c9', '#4a4a4a'],
    shadow: ['rgba(0,0,0,.08)', 'rgba(0,0,0,.35)'], accent: ['#6c357c', '#7d3d8e'], 'accent-text': ['#6c357c', '#a96bb8'],
    success: ['#107c10', '#6ccb5f'], warning: ['#9d5d00', '#fce100'], error: ['#c42b1c', '#ff99a4'], danger: ['#c42b1c', '#ff99a0'], info: ['#0f6cbd', '#6cb8ff'],
  };
  for (const [token, values] of Object.entries(tokens)) {
    assert(light.includes(`--${token}: ${values[0]};`), `light ${token}`);
    assert(dark.includes(`--${token}: ${values[1]};`), `dark ${token}`);
  }
});
console.log(`\n${checks} non-visual checks passed. Browser review remains with the user.`);
