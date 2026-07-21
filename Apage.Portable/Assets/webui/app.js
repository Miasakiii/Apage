/* ==========================================================================
   Apage 新标签页逻辑
   规范来源：docs/frontend-design.md §6
   桥接约定（与 C# 侧对齐）：channel 固定 'ntp'，经 window.chrome.webview
   postMessage 通讯；无桥环境（双击文件直接打开）全部降级为本地行为。
   ========================================================================== */

(function () {
  'use strict';

  /* ------------------------------------------------------------------------
     WebView2 消息桥
     ------------------------------------------------------------------------ */

  function bridgeSend(msg) {
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv && typeof wv.postMessage === 'function') {
        wv.postMessage(Object.assign({ channel: 'ntp' }, msg));
        return true;
      }
    } catch (_) { /* 无桥环境，忽略 */ }
    return false;
  }

  function bridgeListen(handler) {
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv && typeof wv.addEventListener === 'function') {
        wv.addEventListener('message', function (e) { handler(e && e.data); });
      }
    } catch (_) { /* 无桥环境，忽略 */ }
  }

  var reducedMotion = window.matchMedia
    && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ------------------------------------------------------------------------
     b) 时钟 + 日期：先透明占位，渲染后 300ms 淡入防闪烁
     ------------------------------------------------------------------------ */

  var WEEKDAYS = ['星期日', '星期一', '星期二', '星期三', '星期四', '星期五', '星期六'];
  var clockEl = document.getElementById('clock');
  var dateEl = document.getElementById('date');
  var clockWrap = document.getElementById('clock-wrap');

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function tickClock() {
    var now = new Date();
    clockEl.textContent = pad2(now.getHours()) + ':' + pad2(now.getMinutes());
    // 中文日期格式：「7月19日 星期六」
    dateEl.textContent = (now.getMonth() + 1) + '月' + now.getDate() + '日 ' + WEEKDAYS[now.getDay()];
  }

  tickClock();
  setInterval(tickClock, 1000);
  // 首帧渲染完成后再淡入，避免打开瞬间「0:00」闪烁
  requestAnimationFrame(function () {
    requestAnimationFrame(function () { clockWrap.classList.add('show'); });
  });

  /* ------------------------------------------------------------------------
     c) 搜索框：引擎循环切换（百度→必应→Google）+ Enter 跳转
     ------------------------------------------------------------------------ */

  var ENGINES = [
    { name: '百度',   icon: '度', url: 'https://www.baidu.com/s?wd=' },
    { name: '必应',   icon: '必', url: 'https://www.bing.com/search?q=' },
    { name: 'Google', icon: 'G',  url: 'https://www.google.com/search?q=' }
  ];
  var engineIndex = 0;
  var engineBtn = document.getElementById('engine-btn');
  var engineIcon = document.getElementById('engine-icon');
  var searchForm = document.getElementById('search-form');
  var searchInput = document.getElementById('search-input');

  function applyEngine() {
    var eng = ENGINES[engineIndex];
    engineIcon.textContent = eng.icon;
    engineBtn.title = '当前：' + eng.name + '（点击切换）';
    engineBtn.setAttribute('aria-label', '当前搜索引擎：' + eng.name + '，点击切换');
  }

  engineBtn.addEventListener('click', function () {
    engineIndex = (engineIndex + 1) % ENGINES.length;
    // 200ms 翻转动画：转到 90° 时换字再转回
    engineIcon.classList.add('flip');
    setTimeout(applyEngine, reducedMotion ? 0 : 100);
    setTimeout(function () { engineIcon.classList.remove('flip'); }, reducedMotion ? 0 : 200);
    searchInput.focus();
  });

  applyEngine();

  // 输入是 URL 则直接导航，否则用当前引擎搜索
  function resolveTarget(raw) {
    var text = raw.trim();
    if (!text) return null;
    if (/^https?:\/\//i.test(text)) return text;
    // 形如 example.com / example.com/path（无空格且含点）按网址处理
    if (/^[^\s]+\.[^\s]{2,}(\/\S*)?$/.test(text)) return 'https://' + text;
    return ENGINES[engineIndex].url + encodeURIComponent(text);
  }

  function navigate(url) {
    // 有桥时交给宿主导航，无桥时（双击文件打开）自行跳转
    if (!bridgeSend({ type: 'navigate', url: url })) {
      window.location.href = url;
    }
  }

  searchForm.addEventListener('submit', function (e) {
    e.preventDefault();
    var target = resolveTarget(searchInput.value);
    if (target) navigate(target);
  });

  /* ------------------------------------------------------------------------
     d) 快捷方式：桩数据 6 个，每行最多 6 个
     ------------------------------------------------------------------------ */

  var SHORTCUTS = [
    { name: 'Google', icon: 'G',  url: 'https://www.google.com' },
    { name: 'GitHub', icon: 'GH', url: 'https://github.com' },
    { name: 'B站',    icon: 'B',  url: 'https://www.bilibili.com' },
    { name: '知乎',   icon: '知', url: 'https://www.zhihu.com' },
    { name: '微博',   icon: '微', url: 'https://weibo.com' }
  ];

  var shortcutsEl = document.getElementById('shortcuts');

  SHORTCUTS.forEach(function (s) {
    var a = document.createElement('a');
    a.className = 'shortcut-item';
    a.href = s.url;
    a.innerHTML = '<span class="shortcut-tile">' + s.icon + '</span>' +
                  '<span class="shortcut-label">' + s.name + '</span>';
    a.addEventListener('click', function (e) {
      e.preventDefault();
      navigate(s.url);
    });
    shortcutsEl.appendChild(a);
  });

  // 「+」添加块：虚线占位，后续接自定义快捷方式管理
  var addBtn = document.createElement('button');
  addBtn.type = 'button';
  addBtn.className = 'shortcut-item add';
  addBtn.title = '添加快捷方式';
  addBtn.setAttribute('aria-label', '添加快捷方式');
  addBtn.innerHTML = '<span class="shortcut-tile">+</span>' +
                     '<span class="shortcut-label">添加</span>';
  addBtn.addEventListener('click', function () {
    bridgeSend({ type: 'addShortcut' });
  });
  shortcutsEl.appendChild(addBtn);

  /* ------------------------------------------------------------------------
     e) 隐私统计卡：桩数据 + count-up（800ms ease-out），预留 getStats 通道
     ------------------------------------------------------------------------ */

  var stats = { trackers: 1283, ads: 342 }; // 桩数据，有桥时由宿主真实数据覆盖
  var statTrackersEl = document.getElementById('stat-trackers');
  var statAdsEl = document.getElementById('stat-ads');

  function fmt(n) { return n.toLocaleString('en-US'); }

  function countUp(el, target, duration) {
    if (reducedMotion || duration <= 0) {
      el.textContent = fmt(target);
      return;
    }
    var t0 = performance.now();
    function tick(t) {
      var p = Math.min(1, (t - t0) / duration);
      var eased = 1 - Math.pow(1 - p, 3); // ease-out
      el.textContent = fmt(Math.round(target * eased));
      if (p < 1) requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
  }

  function animateStats() {
    countUp(statTrackersEl, stats.trackers, 800);
    countUp(statAdsEl, stats.ads, 800);
  }

  bridgeSend({ type: 'getStats' });
  bridgeListen(function (msg) {
    if (msg && msg.type === 'stats'
        && typeof msg.trackers === 'number' && typeof msg.ads === 'number') {
      stats.trackers = msg.trackers;
      stats.ads = msg.ads;
      animateStats();
    }
  });
  animateStats();

  /* ------------------------------------------------------------------------
     f) 壁纸：6 张内置 CSS 渐变 + 本地图片（file 选择 / 拖入），选择持久化
     ------------------------------------------------------------------------ */

  // 第 0 张与 style.css / index.html 内嵌关键 CSS 中的默认壁纸数值一致
  var WALLPAPERS = [
    { name: '晨曦蓝', css: 'radial-gradient(at 25% 20%, rgba(99,102,241,.90) 0%, rgba(99,102,241,0) 55%), radial-gradient(at 75% 15%, rgba(56,189,248,.70) 0%, rgba(56,189,248,0) 50%), radial-gradient(at 60% 85%, rgba(168,85,247,.65) 0%, rgba(168,85,247,0) 55%), linear-gradient(160deg, #1d2440 0%, #12172e 100%)' },
    { name: '落日橙', css: 'radial-gradient(at 30% 25%, rgba(251,146,60,.85) 0%, rgba(251,146,60,0) 55%), radial-gradient(at 75% 70%, rgba(244,63,94,.70) 0%, rgba(244,63,94,0) 55%), linear-gradient(160deg, #3b1d3a 0%, #1f1235 100%)' },
    { name: '薄荷绿', css: 'radial-gradient(at 25% 20%, rgba(52,211,153,.75) 0%, rgba(52,211,153,0) 55%), radial-gradient(at 75% 75%, rgba(45,212,191,.65) 0%, rgba(45,212,191,0) 55%), linear-gradient(160deg, #134e4a 0%, #0b2b33 100%)' },
    { name: '石墨夜', css: 'radial-gradient(at 30% 30%, rgba(100,116,139,.55) 0%, rgba(100,116,139,0) 60%), linear-gradient(160deg, #1e293b 0%, #0b1120 100%)' },
    { name: '雾灰蓝', css: 'radial-gradient(at 70% 25%, rgba(148,163,184,.65) 0%, rgba(148,163,184,0) 55%), radial-gradient(at 25% 75%, rgba(96,165,250,.55) 0%, rgba(96,165,250,0) 55%), linear-gradient(160deg, #334155 0%, #17202f 100%)' },
    { name: '樱花粉', css: 'radial-gradient(at 30% 25%, rgba(244,114,182,.75) 0%, rgba(244,114,182,0) 55%), radial-gradient(at 75% 70%, rgba(192,132,252,.65) 0%, rgba(192,132,252,0) 55%), linear-gradient(160deg, #3f1d44 0%, #211231 100%)' }
  ];

  var WALLPAPER_KEY = 'apage.ntp.wallpaper';
  var wallpaperEl = document.getElementById('wallpaper');
  var panel = document.getElementById('wallpaper-panel');
  var thumbsEl = document.getElementById('wallpaper-thumbs');
  var fileInput = document.getElementById('file-input');

  function applyWallpaper(css, persistValue) {
    wallpaperEl.style.background = css;
    wallpaperEl.style.backgroundSize = 'cover';
    wallpaperEl.style.backgroundPosition = 'center';
    if (persistValue !== undefined) {
      try { localStorage.setItem(WALLPAPER_KEY, persistValue); } catch (_) { /* 本地图过大时放弃持久化 */ }
    }
  }

  function markActiveThumb(index) {
    var thumbs = thumbsEl.children;
    for (var i = 0; i < thumbs.length; i++) {
      thumbs[i].classList.toggle('active', i === index);
    }
  }

  WALLPAPERS.forEach(function (w, i) {
    var thumb = document.createElement('button');
    thumb.type = 'button';
    thumb.className = 'wallpaper-thumb';
    thumb.style.background = w.css;
    thumb.style.backgroundSize = 'cover';
    thumb.title = w.name;
    thumb.setAttribute('aria-label', '壁纸：' + w.name);
    thumb.addEventListener('click', function () {
      applyWallpaper(w.css, String(i)); // 即点即换
      markActiveThumb(i);
    });
    thumbsEl.appendChild(thumb);
  });

  function setLocalImage(file) {
    if (!file || !/^image\//.test(file.type)) return;
    var reader = new FileReader();
    reader.onload = function () {
      var css = 'url("' + reader.result + '")';
      applyWallpaper(css, reader.result);
      markActiveThumb(-1);
    };
    reader.readAsDataURL(file);
  }

  document.getElementById('btn-local-image').addEventListener('click', function () {
    fileInput.click();
  });
  fileInput.addEventListener('change', function () {
    setLocalImage(fileInput.files && fileInput.files[0]);
    fileInput.value = '';
  });

  // 拖入图片即设为壁纸
  window.addEventListener('dragover', function (e) { e.preventDefault(); });
  window.addEventListener('drop', function (e) {
    e.preventDefault();
    var file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
    setLocalImage(file);
  });

  // 恢复上次选择：存的是索引用内置渐变，存的是 dataURL 用本地图
  (function restoreWallpaper() {
    var saved = null;
    try { saved = localStorage.getItem(WALLPAPER_KEY); } catch (_) { /* file:// 下可能禁用 */ }
    if (saved === null) { markActiveThumb(0); return; }
    var idx = parseInt(saved, 10);
    if (!isNaN(idx) && WALLPAPERS[idx]) {
      applyWallpaper(WALLPAPERS[idx].css);
      markActiveThumb(idx);
    } else if (saved.indexOf('data:image/') === 0) {
      applyWallpaper('url("' + saved + '")');
      markActiveThumb(-1);
    }
  })();

  // 面板开合：🖼 切换，点击面板外关闭
  document.getElementById('btn-wallpaper').addEventListener('click', function (e) {
    e.stopPropagation();
    panel.classList.toggle('open');
  });
  panel.addEventListener('click', function (e) { e.stopPropagation(); });
  document.addEventListener('click', function () { panel.classList.remove('open'); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') panel.classList.remove('open');
  });

  // ⚙ 设置：桥接给宿主打开设置窗口，无桥时无操作
  document.getElementById('btn-settings').addEventListener('click', function () {
    bridgeSend({ type: 'openSettings' });
  });
})();
