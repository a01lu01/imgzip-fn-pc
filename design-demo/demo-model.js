/* Shared, browser-independent demo state. No compression, network, or persistence. */
(function (root) {
  'use strict';

  const presets = {
    'archive-jpeg': { name: '收藏 JPEG', format: 'jpeg', quality: 90, edge: 4000 },
    'archive-webp': { name: '收藏 WebP', format: 'webp', quality: 90, edge: 4000 },
    'webp-share': { name: '分享 WebP', format: 'webp', quality: 80, edge: 2560 },
  };
  const supported = /\.(jpe?g|png|webp|gif|bmp|heic)$/i;
  const sampleFiles = () => [
    ['海边的清晨.jpg', 12480000], ['沿海公路.jpg', 9830000],
    ['日落时分.png', 15720000], ['山间小屋.jpg', 8160000],
    ['雨后的街道.jpg', 11160000], ['森林漫步.webp', 6270000],
    ['窗边的光.jpg', 9540000], ['旅途的最后一天.jpg', 11980000],
  ].map(([name, size], i) => ({ name, size, relative: name, id: `sample-${i}` }));

  function createState() {
    return {
      files: sampleFiles(), source: 'D:\\照片\\夏日旅行', sourceKind: 'folder',
      sample: true, skipped: 0, preset: 'archive-jpeg', format: 'jpeg', quality: 90,
      resize: 'long', pixels: 4000, maxSize: 0, threads: 4,
      lossless: false, noUpscale: true, recurse: false, dryRun: false,
      engine: 'pc', unavailable: false, advanced: false, status: 'ready', progress: 0,
      completion: 'success', theme: 'system', notice: '', visibleFiles: 40,
    };
  }
  function applyPreset(state, preset) {
    if (!presets[preset]) return;
    Object.assign(state, { preset, format: presets[preset].format, quality: presets[preset].quality,
      resize: 'long', pixels: presets[preset].edge, noUpscale: true, lossless: false, maxSize: 0 });
  }
  function totalBytes(state) { return state.files.reduce((n, file) => n + file.size, 0); }
  function scopedFiles(files, sourceKind, recurse) {
    return sourceKind === 'folder' && !recurse ? files.filter(file => !file.relative.includes('/')) : files;
  }
  function parseTheme(raw) {
    try {
      const value = JSON.parse(raw)?.theme;
      return ['system', 'light', 'dark'].includes(value) ? value : 'system';
    } catch { return 'system'; }
  }
  function bytes(value) {
    if (!value) return '0 B';
    if (value < 1024) return `${value} B`;
    const unit = value >= 1024 ** 3 ? 'GB' : value >= 1024 ** 2 ? 'MB' : 'KB';
    const divisor = unit === 'GB' ? 1024 ** 3 : unit === 'MB' ? 1024 ** 2 : 1024;
    return `${(value / divisor).toFixed(1)} ${unit}`;
  }
  function outputPath(state) {
    if (!state.files.length) return '选择来源后自动生成';
    if (state.sourceKind === 'folder') return `${state.source.replace(/[\\/]$/, '')}_compressed`;
    if (state.sample) return 'D:\\照片\\夏日旅行_compressed';
    return '源文件所在目录_compressed（浏览器无法读取完整路径）';
  }
  function completedCount(state) {
    return Math.min(state.files.length, Math.floor(state.files.length * state.progress / 100));
  }
  function result(state) {
    const total = state.files.length;
    const failed = state.status === 'partial' ? Math.min(2, total) : 0;
    const success = total - failed;
    const processedBytes = state.files.slice(0, success).reduce((n, f) => n + f.size, 0);
    const after = Math.round(processedBytes * 0.58);
    return { total, failed, success, before: processedBytes, after, saved: processedBytes - after };
  }
  function validate(state) {
    if (!state.files.length) return '请先添加图片或文件夹。';
    if (state.unavailable) return '当前引擎不可用，请切换演示状态或选择另一个引擎。';
    if (!state.lossless && (!Number.isInteger(state.quality) || state.quality < 0 || state.quality > 100)) return '质量需为 0–100 的整数。';
    if (state.resize !== 'none' && (!Number.isInteger(state.pixels) || state.pixels < 1)) return '缩放尺寸需为正整数。';
    if (!state.lossless && (!Number.isSafeInteger(state.maxSize) || state.maxSize < 0)) return '目标体积需为大于或等于 0 的整数字节。';
    if (!Number.isInteger(state.threads) || state.threads < 1 || state.threads > 64) return '线程数需为 1–64 的整数。';
    return '';
  }
  const api = { presets, supported, sampleFiles, createState, applyPreset, totalBytes, scopedFiles, parseTheme, bytes, outputPath, completedCount, result, validate };
  root.ImgZipDemo = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
