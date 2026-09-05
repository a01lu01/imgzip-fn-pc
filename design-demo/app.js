(function () {
  'use strict';
  const M = window.ImgZipDemo;
  const state = M.createState();
  state.allFiles = state.files;
  const configKey = 'imgzip-demo-config';
  try { state.theme = M.parseTheme(localStorage.getItem(configKey)); } catch { /* Storage may be blocked for file URLs. */ }
  let timer = null;
  let importToken = 0;
  let importing = false;
  const root = document.getElementById('app');
  const variants = {
    a: { name: '轻量工具窗', note: '打开，即刻开始。', size: '760 × 640', description: '紧凑单页，把注意力留给眼前这一次压缩。' },
    b: { name: 'Windows 设置风格', note: '每一个选项，都井然有序。', size: '960 × 720', description: '熟悉的设置分组，让偶尔使用也不需要重新学习。' },
    c: { name: '双栏工作台', note: '文件与参数，一眼看全。', size: '1120 × 760', description: '图片列表与参数并排，适合仔细确认每一次批量处理。' },
  };
  const readLayout = () => new URLSearchParams(location.search).get('layout') || location.pathname.match(/\/([abc])\.html$/i)?.[1].toLowerCase();
  let variant = readLayout();
  if (!variants[variant]) variant = null;
  const icons = {
    app: '<rect x="4" y="4" width="16" height="16" rx="4"/><path d="m7 16 4-5 3 3 2-2 2 4"/><circle cx="15.5" cy="8.5" r="1"/>',
    folder: '<path d="M3 7V5a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v12H3Z"/><path d="M3 9h18"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="3"/><path d="m3 17 5-5 4 4 3-3 6 5"/><circle cx="15.5" cy="8" r="1.5"/>',
    settings: '<path d="m9 3-1 3-3 1 1 3-2 2 2 2-1 3 3 1 1 3h6l1-3 3-1-1-3 2-2-2-2 1-3-3-1-1-3Z"/><circle cx="12" cy="12" r="3"/>',
    chevron: '<path d="m9 5 7 7-7 7"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    pc: '<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8m-4-4v4"/>',
    nas: '<rect x="4" y="3" width="16" height="18" rx="3"/><path d="M4 10h16M4 16h16M8 7h.01M8 13h.01M8 19h.01"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    arrow: '<path d="M4 12h15m-6-6 6 6-6 6"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    close: '<path d="m6 6 12 12M6 18 18 6"/>',
    shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Z"/><path d="m8 12 3 3 5-6"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6m0-10v1"/>',
    warn: '<path d="m12 3 10 18H2Z"/><path d="M12 9v5m0 3v1"/>',
    resize: '<path d="M8 3H3v5m13 13h5v-5M3 3l6 6m12 12-6-6"/>',
  };
  const icon = (name, cls = '') => `<svg class="icon ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${icons[name] || icons.image}</svg>`;
  const esc = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const selected = condition => condition ? ' selected' : '';
  const checked = condition => condition ? ' checked' : '';
  const disabled = condition => condition ? ' disabled' : '';
  const busy = () => state.status === 'running';
  function navigate(url) {
    try { history.pushState({}, '', url); }
    catch { location.assign(url); } // Some browsers restrict history changes for file: URLs.
  }

  function theme() {
    const dark = state.theme === 'dark' || state.theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches;
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  }
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', theme);

  function mini(layout) {
    return `<div class="mini mini-${layout}" aria-hidden="true"><div class="mini-title"><i></i><span></span><b>− &nbsp; □ &nbsp; ×</b></div><div class="mini-body"><div class="mini-heading"></div><div class="mini-source">${icon('folder')}<span></span></div><div class="mini-options"><i></i><i></i><i></i></div><div class="mini-lines"><i></i><i></i><i></i></div><div class="mini-button"></div></div></div>`;
  }
  function landing() {
    return `<main class="gallery"><header class="gallery-header"><a class="brand" href="index.html">${icon('app')}<span>ImgZip <em>Design studio</em></span></a><label class="theme-label">外观 <select data-control="theme" aria-label="外观主题"><option value="system"${selected(state.theme === 'system')}>跟随系统</option><option value="light"${selected(state.theme === 'light')}>浅色</option><option value="dark"${selected(state.theme === 'dark')}>深色</option></select></label></header><section class="gallery-intro"><h1>让每一次压缩，<br>更轻一点。</h1><p>同一套功能，三种工作方式。<br>打开演示，找到最顺手的那一种。</p></section><div class="design-grid">${Object.entries(variants).map(([key, value]) => `<a class="design-card" data-layout="${key}" href="${key}.html">${mini(key)}<div class="design-copy"><span class="design-letter">${key.toUpperCase()}</span><div><h2>${value.name}</h2><p>${value.description}</p><span class="design-size">${value.size} · 浅色 / 深色</span></div>${icon('arrow')}</div></a>`).join('')}</div><footer class="gallery-footer"><span>${icon('info')} 可交互 HTML 原型 · 所有压缩结果均为模拟</span><span>WinUI 3 + C# / 原生实现前的布局选型</span></footer></main>`;
  }
  function studio() {
    const value = variants[variant];
    const mode = state.unavailable ? 'unavailable' : state.status;
    return `<header class="studio-header"><a class="brand" href="index.html" data-home>${icon('app')}<span>ImgZip <em>Design studio</em></span></a><nav aria-label="切换设计方案">${Object.entries(variants).map(([key, item]) => `<a data-layout="${key}" href="${key}.html"${key === variant ? ' aria-current="page"' : ''}><b>${key.toUpperCase()}</b>${item.name}</a>`).join('')}</nav><label class="theme-label">外观 <select data-control="theme" aria-label="外观主题"><option value="system"${selected(state.theme === 'system')}>跟随系统</option><option value="light"${selected(state.theme === 'light')}>浅色</option><option value="dark"${selected(state.theme === 'dark')}>深色</option></select></label></header><main class="studio"><div class="studio-caption"><div><span class="caption-letter">${variant.toUpperCase()}</span><h1>${value.name}</h1><span>${value.size}</span></div><p>${value.note}</p></div><div class="demo-controls"><span class="demo-marker">交互演示</span><label>预览状态 <select data-control="scenario" aria-label="预览状态">${[['ready','待开始'],['empty','空状态'],['running','压缩中'],['success','成功'],['partial','部分失败'],['failed','失败'],['cancelled','已取消'],['unavailable','引擎不可用']].map(([k,v]) => `<option value="${k}"${selected(k === mode)}>${v}</option>`).join('')}</select></label><label>模拟结束 <select data-control="completion" aria-label="模拟结束结果">${[['success','成功'],['partial','部分失败'],['failed','失败']].map(([k,v]) => `<option value="${k}"${selected(k === state.completion)}>${v}</option>`).join('')}</select></label><button class="text-button" data-action="reset"${disabled(busy())}>恢复示例</button><span class="simulation-note">不压缩文件 · 不连接 NAS</span></div>${appWindow()}<p class="preview-footnote">当前方案 ${variant.toUpperCase()} · 控件与布局将映射为原生 XAML；背景材质为浏览器近似效果。${variant === 'c' ? ' 列表仅展示当前任务。' : ''}</p></main>`;
  }
  function appWindow() {
    return `<section class="app-window layout-${variant}" aria-label="ImgZip ${variants[variant].name}"><header class="titlebar"><div>${icon('app', 'app-logo')}<span>ImgZip</span></div><div class="titlebar-actions"><button class="icon-button" data-action="settings" aria-label="打开设置" title="设置"${disabled(busy())}>${icon('settings')}</button><span class="window-decoration" aria-hidden="true">— &nbsp; □ &nbsp; ×</span></div></header><div class="window-content"><div class="page-heading"><div><h2>图片压缩</h2><p>保留珍贵瞬间，腾出更多空间。</p></div>${variant !== 'a' ? `<span class="heading-symbol">${icon('image')}</span>` : ''}</div><div id="notice" class="notice${state.notice ? '' : ' hidden'}" role="status">${icon('info')}<span>${esc(state.notice)}</span></div><form id="compression-form" novalidate><fieldset class="main-fields"${disabled(busy() || importing)}><div class="workspace"><div class="source-column">${sourceSection()}${variant === 'c' ? fileList() : ''}</div><div class="options-column">${presetSection()}${engineSection()}${advancedSection()}${outputSection()}</div></div></fieldset></form></div>${taskFooter()}</section><input id="file-input" type="file" accept=".jpg,.jpeg,.png,.webp,.gif,.bmp,.heic" multiple hidden><input id="folder-input" type="file" webkitdirectory multiple hidden>`;
  }
  function sourceSection() {
    const hasSource = state.allFiles.length > 0;
    return `<section class="source-section"><div class="section-heading"><h3>${variant === 'c' ? '当前文件' : '来源'}</h3>${hasSource ? `<button type="button" class="text-button" data-action="clear">清空</button>` : ''}</div><div class="source-drop${hasSource ? ' has-source' : ''}" id="drop-zone" aria-label="图片或文件夹拖放区域"><div class="source-illustration">${icon(hasSource ? 'folder' : 'image')}</div><div class="source-info"><h4>${hasSource ? esc(sourceName()) : '将图片或文件夹拖到这里'}</h4><p>${hasSource ? `${state.files.length} 张图片 <span class="dot">·</span> ${M.bytes(M.totalBytes(state))}${state.sample ? ' <span class="sample-label">示例</span>' : ''}` : '支持 JPG、PNG、WebP 等图片格式'}</p>${hasSource ? `<span class="source-path" title="${esc(state.source)}">${esc(state.source)}</span>` : ''}</div><div class="source-buttons"><button type="button" data-action="pick-files">${icon('plus')}添加图片</button><button type="button" data-action="pick-folder">${icon('folder')}选择文件夹</button></div></div><label class="check-row recurse-row"><input type="checkbox" data-field="recurse"${checked(state.recurse)}>包含子文件夹<span class="muted">默认仅处理当前文件夹</span></label>${state.skipped ? `<p class="import-note">已忽略 ${state.skipped} 个非图片文件；格式最终支持情况以压缩引擎为准。</p>` : ''}</section>`;
  }
  function sourceName() {
    if (state.sourceKind === 'files') return state.files.length === 1 ? state.files[0].name : '已选择的图片';
    return state.source.split(/[\\/]/).filter(Boolean).pop() || '图片文件夹';
  }
  function fileList() {
    if (!state.files.length) return `<div class="empty-files">${icon('image')}<p>图片会显示在这里</p><span>添加来源后，检查当前任务中的文件。</span></div>`;
    return `<section class="file-list" aria-label="当前任务图片列表"><div class="list-head"><span>文件名</span><span>原始大小</span><span>状态</span></div>${state.files.slice(0, state.visibleFiles).map((file, index) => `<div class="file-row"><div class="file-name"><span class="file-tile tile-${index % 4}">${icon('image')}</span><span title="${esc(file.relative || file.name)}"><strong>${esc(file.name)}</strong><small>${esc(file.name.split('.').pop().toUpperCase())} 图片</small></span></div><span class="file-size">${M.bytes(file.size)}</span><span class="file-status" data-file-index="${index}">${fileStatus(index)}</span></div>`).join('')}${state.files.length > state.visibleFiles ? `<button type="button" class="load-more" data-action="more">继续显示（共 ${state.files.length} 张）</button>` : ''}</section>`;
  }
  function fileStatus(index) {
    if (state.status === 'success') return `<span class="status-good">${state.dryRun ? '已预演' : '已完成'}</span>`;
    if (state.status === 'partial') return index >= M.result(state).success ? '<span class="status-bad">读取失败</span>' : '<span class="status-good">已完成</span>';
    if (state.status === 'failed') return '<span class="status-bad">未完成</span>';
    const done = M.completedCount(state);
    if ((busy() || state.status === 'cancelled') && index < done) return '<span class="status-good">已处理</span>';
    if (state.status === 'cancelled') return '已取消';
    if (busy() && index === done) return '<span class="status-active">处理中</span>';
    return '待处理';
  }
  function presetSection() {
    return `<section class="preset-section"><div class="section-heading"><h3>压缩预设</h3><span class="muted">${state.preset === 'custom' ? '已自定义' : '为不同用途准备'}</span></div><div class="preset-grid" role="group" aria-label="压缩预设">${Object.entries(M.presets).map(([key,preset]) => `<button type="button" class="preset${state.preset === key ? ' active' : ''}" data-preset="${key}" aria-pressed="${state.preset === key}"><span class="preset-top"><span class="format-mark">${preset.format === 'jpeg' ? 'JPG' : 'WEBP'}</span><span class="selected-mark">${icon('check')}</span></span><strong>${preset.name}</strong><small>${preset.quality} 质量 · ${preset.edge} px</small></button>`).join('')}</div></section>`;
  }
  function engineSection() {
    return `<section class="engine-section setting-surface"><div class="setting-main"><span class="setting-icon">${icon('pc')}</span><div><h3>执行位置</h3><p>${state.engine === 'nas' ? '在 NAS 上处理共享目录内的图片' : '使用本机处理，无需网络连接'}</p></div></div><div class="engine-controls"><div class="segmented" role="group" aria-label="压缩引擎"><button type="button" data-engine="pc" aria-pressed="${state.engine === 'pc'}" class="${state.engine === 'pc' ? 'active' : ''}">${icon('pc')}本机</button><button type="button" data-engine="nas" aria-pressed="${state.engine === 'nas'}" class="${state.engine === 'nas' ? 'active' : ''}">${icon('nas')}NAS</button></div><span class="engine-state${state.unavailable ? ' unavailable' : ''}"><i></i>${state.unavailable ? '引擎不可用' : '就绪'} <span>· 模拟</span></span></div></section>`;
  }
  function advancedSection() {
    return `<section class="advanced-section setting-surface"><button type="button" class="disclosure" data-action="advanced" aria-expanded="${state.advanced}" aria-controls="advanced-panel"><span class="setting-main"><span class="setting-icon">${icon('resize')}</span><span><strong>高级设置</strong><small>${state.lossless ? '无损' : `${state.quality} 质量`} · ${state.resize === 'none' ? '原始尺寸' : `${state.pixels} px`} · ${state.noUpscale ? '不放大小图' : '允许放大'}</small></span></span>${icon('down', state.advanced ? 'rotated' : '')}</button><div id="advanced-panel" class="advanced-panel"${state.advanced ? '' : ' hidden'}><div class="fields-grid"><label>输出格式<select data-field="format"><option value="keep"${selected(state.format === 'keep')}>保持原格式</option><option value="jpeg"${selected(state.format === 'jpeg')}>JPEG</option><option value="webp"${selected(state.format === 'webp')}>WebP</option><option value="png"${selected(state.format === 'png')}>PNG</option></select></label><label>质量 <span class="muted">0–100</span><input type="number" data-field="quality" value="${state.quality}" min="0" max="100" step="1" required${disabled(state.lossless)}></label><label>缩放方式<select data-field="resize">${[['none','不缩放'],['long','按长边'],['short','按短边'],['width','固定宽度'],['height','固定高度']].map(([key,value]) => `<option value="${key}"${selected(state.resize === key)}>${value}</option>`).join('')}</select></label><label>尺寸 <span class="muted">px · 等比缩放</span><input type="number" data-field="pixels" value="${state.pixels}" min="1" step="1" required${disabled(state.resize === 'none')}></label><label>目标体积 <span class="muted">字节 · 0 为不限</span><input type="number" data-field="maxSize" value="${state.maxSize}" min="0" step="1" required${disabled(state.lossless)}></label><label>并行线程<input type="number" data-field="threads" value="${state.threads}" min="1" max="64" step="1" required></label></div><div class="advanced-checks"><label class="check-row"><input type="checkbox" data-field="lossless"${checked(state.lossless)}>无损压缩</label><label class="check-row"><input type="checkbox" data-field="noUpscale"${checked(state.noUpscale)}${disabled(state.resize === 'none')}>不放大小图</label><label class="check-row"><input type="checkbox" data-field="dryRun"${checked(state.dryRun)}>仅预演</label></div><p class="field-help">${state.lossless ? '无损模式不使用质量和目标体积参数；格式转换与缩放仍会改变图片。' : '仅启用一种缩放方式；固定宽高也保持原始比例。'} 仅预演不会生成压缩文件。</p></div></section>`;
  }
  function outputSection() {
    return `<section class="output-section"><span>${icon('folder')}</span><div><h3>输出到新文件夹</h3><p title="${esc(M.outputPath(state))}">${esc(M.outputPath(state))}</p></div><span class="safe-mark" title="原始文件保持不变">${icon('shield')}</span></section>`;
  }
  function taskFooter() {
    let contents;
    if (busy()) {
      contents = `<div class="running-info"><div class="progress-heading"><strong>${state.dryRun ? '正在预演' : '正在压缩'}<span class="muted"> · ${state.engine === 'pc' ? '本机' : 'NAS'} · 模拟</span></strong><span id="progress-text">${M.completedCount(state)} / ${state.files.length}</span></div><progress id="task-progress" max="100" value="${state.progress}" aria-label="模拟压缩进度"></progress><span id="progress-file" class="muted">${esc(state.files[Math.min(M.completedCount(state), state.files.length - 1)]?.name || '')}</span></div><button data-action="cancel">取消</button>`;
    } else if (state.status === 'success' || state.status === 'partial') {
      const r = M.result(state);
      contents = `<div class="result-summary"><span class="result-symbol ${r.failed ? 'warning' : ''}">${icon(r.failed ? 'warn' : 'check')}</span><div><strong>${state.dryRun ? '预演完成' : r.failed ? '部分图片未完成' : '压缩完成'}<span class="muted"> · 模拟结果</span></strong><p>${state.dryRun ? `已检查 ${r.total} 张图片，未生成文件` : `${r.success} 张成功${r.failed ? `，${r.failed} 张失败` : ''} · ${M.bytes(r.before)} → ${M.bytes(r.after)}`}</p>${!state.dryRun ? `<span class="saved">成功项节省 ${M.bytes(r.saved)} · 42%</span>` : ''}</div></div><div class="result-actions">${r.failed ? '<button data-action="errors">查看详情</button>' : ''}<button data-action="ready">继续压缩</button>${!state.dryRun ? '<button class="primary" data-action="output">打开输出文件夹</button>' : ''}</div>`;
    } else if (state.status === 'failed' || state.status === 'cancelled') {
      contents = `<div class="result-summary"><span class="result-symbol warning">${icon(state.status === 'failed' ? 'warn' : 'info')}</span><div><strong>${state.status === 'failed' ? '任务未完成' : '任务已取消'} <span class="muted">· 模拟</span></strong><p>${state.status === 'failed' ? '无法读取源文件，请检查路径和访问权限。' : '模拟执行已停止，没有修改实际文件。'}</p></div></div><button class="primary" data-action="ready">返回设置</button>`;
    } else {
      contents = `<div class="ready-summary">${icon(state.unavailable ? 'warn' : 'shield')}<div><strong>${state.unavailable ? '当前引擎不可用' : state.files.length ? `${state.files.length} 张图片准备就绪` : '添加图片即可开始'}</strong><p>${state.unavailable ? '切换另一引擎，或在设置中查看配置。' : '原始图片保持不变'}</p></div></div><button class="primary start-button" data-action="start"${disabled(!state.files.length || state.unavailable || importing)}>${state.dryRun ? '开始预演' : '开始压缩'}${icon('arrow')}</button>`;
    }
    return `<footer class="task-footer" aria-label="任务操作与结果">${contents}</footer>`;
  }
  function render() {
    const scrollPositions = ['.window-content', '.source-column', '.options-column', '.file-list'].map(selector => [selector, document.querySelector(selector)?.scrollTop || 0]);
    theme();
    root.innerHTML = variant ? studio() : landing();
    document.title = variant ? `ImgZip · ${variant.toUpperCase()} ${variants[variant].name}` : 'ImgZip · Windows 11 设计选型';
    if (variant) {
      document.getElementById('file-input').addEventListener('change', e => importFiles(Array.from(e.target.files), false));
      document.getElementById('folder-input').addEventListener('change', e => importFiles(Array.from(e.target.files), true));
      const form = document.getElementById('compression-form');
      form.addEventListener('submit', e => { e.preventDefault(); start(); });
      const drop = document.getElementById('drop-zone');
      drop.addEventListener('dragover', e => { e.preventDefault(); if (!busy()) drop.classList.add('dragging'); });
      drop.addEventListener('dragleave', () => drop.classList.remove('dragging'));
      drop.addEventListener('drop', dropFiles);
      for (const [selector, position] of scrollPositions) {
        const element = document.querySelector(selector);
        if (element) element.scrollTop = position;
      }
    }
  }
  function announce(message) {
    state.notice = message;
    const region = document.getElementById('notice');
    if (region) { region.classList.remove('hidden'); region.innerHTML = `${icon('info')}<span>${esc(message)}</span>`; }
  }
  function clearTimer() { if (timer) clearInterval(timer); timer = null; }
  function start() {
    if (busy() || importing) return;
    const error = M.validate(state);
    if (error) { state.advanced = true; state.notice = error; render(); return; }
    state.status = 'running'; state.progress = 0; state.notice = '';
    render();
    tick();
  }
  function tick() {
    clearTimer();
    timer = setInterval(() => {
      state.progress = Math.min(100, state.progress + 4);
      if (state.progress === 100) {
        clearTimer(); state.status = state.completion;
        if (state.dryRun && state.status === 'partial') state.status = 'failed';
        render();
        return;
      }
      const bar = document.getElementById('task-progress');
      if (!bar) return;
      bar.value = state.progress;
      document.getElementById('progress-text').textContent = `${M.completedCount(state)} / ${state.files.length}`;
      document.getElementById('progress-file').textContent = state.files[M.completedCount(state)]?.name || '';
      document.querySelectorAll('[data-file-index]').forEach(el => { el.innerHTML = fileStatus(Number(el.dataset.fileIndex)); });
    }, 400);
  }
  function resetSource() {
    state.allFiles = M.sampleFiles(); state.files = state.allFiles;
    state.source = 'D:\\照片\\夏日旅行'; state.sourceKind = 'folder'; state.sample = true;
    state.skipped = 0; state.notice = ''; state.visibleFiles = 40;
  }
  function scenario(value) {
    clearTimer(); importToken++; importing = false;
    state.notice = ''; state.unavailable = value === 'unavailable'; state.progress = 0;
    if (value === 'empty') { state.files = []; state.allFiles = []; state.source = ''; state.status = 'empty'; }
    else {
      if (!state.files.length) resetSource();
      state.status = value === 'unavailable' ? 'ready' : value;
      if (['success', 'partial', 'failed'].includes(value)) { state.progress = 100; state.dryRun = false; }
      if (value === 'running' || value === 'cancelled') state.progress = 36;
    }
    render();
    if (value === 'running') tick();
  }
  function updateScope() {
    state.files = M.scopedFiles(state.allFiles, state.sourceKind, state.recurse);
    state.status = state.files.length ? 'ready' : 'empty';
    state.progress = 0;
  }
  function importFiles(files, folder) {
    if (busy() || !files.length) return;
    const supported = files.filter(f => M.supported.test(f.name));
    state.skipped = files.length - supported.length;
    state.sample = false;
    state.sourceKind = folder ? 'folder' : 'files';
    state.source = folder ? (files[0].webkitRelativePath || files[0].relative || '').split('/')[0] || '拖入的文件夹' : '浏览器选择的图片（完整路径不可见）';
    state.allFiles = supported.map((file, i) => {
      let relative = file.webkitRelativePath || file.relative || file.name;
      if (folder && relative.includes('/')) relative = relative.split('/').slice(1).join('/');
      return { id: `file-${i}`, name: file.name, size: file.size, relative };
    });
    state.visibleFiles = 40;
    updateScope();
    state.notice = '仅在内存中读取文件名与大小，不读取图片内容、不上传、不修改文件。';
    if (!state.files.length && state.allFiles.length) state.notice += ' 图片位于子文件夹，请勾选“包含子文件夹”。';
    if (!state.allFiles.length) state.notice += ' 本次未发现可演示的图片格式。';
    render();
  }
  async function walkEntry(entry, prefix = '') {
    if (entry.isFile) return new Promise((resolve, reject) => entry.file(file => resolve([{ name: file.name, size: file.size, relative: prefix + file.name }]), reject));
    if (!entry.isDirectory) return [];
    const reader = entry.createReader();
    const entries = [];
    let batch;
    do {
      batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
      entries.push(...batch);
    } while (batch.length);
    const result = [];
    for (const child of entries) result.push(...await walkEntry(child, `${prefix}${entry.name}/`));
    return result;
  }
  async function dropFiles(event) {
    event.preventDefault();
    if (busy() || importing) return;
    const token = ++importToken;
    const fallback = Array.from(event.dataTransfer.files);
    const items = Array.from(event.dataTransfer.items || []);
    const entries = items.map(item => item.webkitGetAsEntry?.()).filter(Boolean);
    importing = true;
    render(); announce('正在读取文件列表…');
    try {
      const directories = entries.filter(entry => entry.isDirectory);
      if (directories.length && entries.length !== 1) throw new Error('请每次拖入一个文件夹，或拖入多张图片。');
      const files = entries.length ? (await Promise.all(entries.map(entry => walkEntry(entry)))).flat() : fallback;
      if (token !== importToken) return;
      importing = false;
      if (!files.length) { render(); announce('文件夹为空或浏览器未提供文件列表，请使用“选择文件夹”。'); return; }
      importFiles(files, directories.length === 1);
    } catch (error) {
      if (token !== importToken) return;
      importing = false; render(); announce(error.message || '无法读取拖入的目录，请使用“选择文件夹”。');
    }
  }
  function dialog(title, body, extra = '') {
    const opener = document.activeElement;
    const element = document.createElement('dialog');
    element.className = 'settings-dialog';
    element.setAttribute('aria-labelledby', 'dialog-title');
    element.innerHTML = `<header><div><h2 id="dialog-title">${title}</h2><p>ImgZip · 设计演示</p></div><button class="icon-button" data-close aria-label="关闭对话框">${icon('close')}</button></header>${body}<footer>${extra}<button class="primary" data-close>完成</button></footer>`;
    document.body.append(element);
    element.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => element.close()));
    element.addEventListener('close', () => { element.remove(); opener?.focus(); });
    element.showModal();
    return element;
  }
  function settings() {
    const element = dialog('设置', `<div class="dialog-body"><div class="dialog-section-heading">${icon('pc')}<h3>本机引擎</h3><span class="status-good">已就绪 · 模拟</span></div><p class="engine-path">bin\\caesiumclt.exe</p><div class="dialog-section-heading">${icon('nas')}<h3>NAS 连接</h3></div><p class="field-help">以下字段只用于演示；关闭后清空，不连接服务器。</p><form id="nas-settings" autocomplete="off"><div class="fields-grid"><label>服务器地址<input name="server" placeholder="192.168.1.10" required autocomplete="off"></label><label>SSH 端口<input name="port" type="number" value="22" min="1" max="65535" step="1" required></label><label class="full-width">SSH 用户<input name="user" placeholder="your_nas_user" required autocomplete="off" spellcheck="false"></label><label class="full-width">私钥路径<input name="key" placeholder="C:\\Users\\you\\.ssh\\id_ed25519_nas" required autocomplete="off" spellcheck="false"></label></div><div class="connection-test"><button type="submit">模拟测试连接</button><span id="connection-status" role="status">未测试</span></div></form><div class="dialog-info">${icon('info')}<span>真实应用将使用 SSH 公钥认证。演示无需填写真实账号或私钥路径。</span></div></div>`);
    element.querySelector('form').addEventListener('submit', event => {
      event.preventDefault();
      element.querySelector('#connection-status').textContent = '连接正常 · 模拟，未发起网络请求';
      element.querySelector('#connection-status').className = 'status-good';
    });
  }
  document.addEventListener('click', event => {
    const target = event.target.closest('button, a');
    if (!target || target.disabled) return;
    if (target.hasAttribute('data-home')) {
      event.preventDefault(); clearTimer(); importToken++; importing = false; if (busy()) state.status = 'cancelled';
      variant = null; navigate('index.html'); render(); return;
    }
    if (target.dataset.layout) {
      event.preventDefault(); variant = target.dataset.layout;
      navigate(`${variant}.html`); render(); return;
    }
    if (target.dataset.preset && !busy()) { M.applyPreset(state, target.dataset.preset); state.status = state.files.length ? 'ready' : 'empty'; render(); return; }
    if (target.dataset.engine && !busy()) { state.engine = target.dataset.engine; state.unavailable = false; state.status = state.files.length ? 'ready' : 'empty'; render(); return; }
    switch (target.dataset.action) {
      case 'pick-files': document.getElementById('file-input').click(); break;
      case 'pick-folder': document.getElementById('folder-input').click(); break;
      case 'clear': importToken++; state.files = []; state.allFiles = []; state.source = ''; state.status = 'empty'; state.notice = ''; state.skipped = 0; render(); break;
      case 'advanced': {
        state.advanced = !state.advanced; render(); document.querySelector('[data-action="advanced"]').focus(); break;
      }
      case 'start': start(); break;
      case 'cancel': clearTimer(); state.status = 'cancelled'; render(); break;
      case 'ready': state.status = state.files.length ? 'ready' : 'empty'; state.progress = 0; state.notice = ''; render(); break;
      case 'reset': clearTimer(); importToken++; importing = false; Object.assign(state, M.createState(), { theme: state.theme }); state.allFiles = state.files; render(); break;
      case 'settings': settings(); break;
      case 'more': state.visibleFiles += 40; render(); break;
      case 'output': dialog('输出文件夹', `<div class="dialog-body"><div class="dialog-info">${icon('folder')}<span>${esc(M.outputPath(state))}</span></div><p>这是模拟结果，没有生成实际文件。原生应用将在此打开 Windows 资源管理器。</p></div>`); break;
      case 'errors': {
        const failedFiles = state.files.slice(M.result(state).success);
        dialog('未完成的图片', `<div class="dialog-body"><p>以下为预设的模拟错误，不代表实际文件损坏。</p><ul class="error-list">${failedFiles.map(file => `<li><strong>${esc(file.name)}</strong><span>无法解码图片（模拟）</span></li>`).join('')}</ul><p class="field-help">真实应用会保留已成功生成的文件，并提供实际错误信息。</p></div>`); break;
      }
    }
  });
  document.addEventListener('change', event => {
    const el = event.target;
    if (el.dataset.control === 'theme') {
      state.theme = el.value;
      theme();
      try { localStorage.setItem(configKey, JSON.stringify({ theme: state.theme })); }
      catch { announce('浏览器不允许保存本地配置；主题已在当前页面生效。'); }
    }
    if (el.dataset.control === 'scenario') scenario(el.value);
    if (el.dataset.control === 'completion') state.completion = el.value;
    const field = el.dataset.field;
    if (!field || busy()) return;
    state[field] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? (el.value === '' ? NaN : Number(el.value)) : el.value;
    if (['format', 'quality', 'resize', 'pixels', 'noUpscale', 'lossless', 'maxSize'].includes(field)) state.preset = 'custom';
    if (field === 'recurse') updateScope();
    state.status = state.files.length ? 'ready' : 'empty';
    // Preserve numeric input focus while editing; commit other controls immediately.
    if (el.type !== 'number') { render(); document.querySelector(`[data-field="${field}"]`)?.focus(); }
    else {
      document.querySelectorAll('[data-preset]').forEach(button => {
        button.classList.toggle('active', button.dataset.preset === state.preset);
        button.setAttribute('aria-pressed', String(button.dataset.preset === state.preset));
      });
      document.querySelector('.preset-section .muted').textContent = state.preset === 'custom' ? '已自定义' : '为不同用途准备';
      document.querySelector('.disclosure small').textContent = `${state.lossless ? '无损' : `${Number.isFinite(state.quality) ? state.quality : '—'} 质量`} · ${state.resize === 'none' ? '原始尺寸' : `${Number.isFinite(state.pixels) ? state.pixels : '—'} px`} · ${state.noUpscale ? '不放大小图' : '允许放大'}`;
    }
  });
  window.addEventListener('popstate', () => {
    const next = readLayout();
    variant = variants[next] ? next : null;
    if (!variant) { clearTimer(); if (busy()) state.status = 'cancelled'; }
    render();
  });
  // Prevent the browser from navigating to an image dropped outside the drop zone.
  window.addEventListener('dragover', e => e.preventDefault());
  window.addEventListener('drop', e => e.preventDefault());
  window.addEventListener('pagehide', clearTimer);
  render();
})();
