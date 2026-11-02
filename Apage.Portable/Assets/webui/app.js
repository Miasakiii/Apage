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
     c) 搜索框：引擎循环切换 + 持久化（宿主设置为真源，本地存储为离线回退）
     ------------------------------------------------------------------------ */

  var ENGINE_KEY = 'apage.ntp.engine';

  // host -> data URL，由宿主 getFavicons 回复填充（引擎徽标与快捷方式共用）
  var faviconMap = {};
  var ENGINES = [
    { id: 'baidu',      name: '百度',       icon: '度', url: 'https://www.baidu.com/s?wd=' },
    { id: 'bing',       name: '必应',       icon: '必', url: 'https://www.bing.com/search?q=' },
    { id: 'google',     name: 'Google',     icon: 'G',  url: 'https://www.google.com/search?q=' },
    { id: 'duckduckgo', name: 'DuckDuckGo', icon: 'D',  url: 'https://duckduckgo.com/?q=' }
  ];

  function engineIndexById(id) {
    for (var i = 0; i < ENGINES.length; i++) {
      if (ENGINES[i].id === id) return i;
    }
    return -1;
  }

  var engineIndex = engineIndexById('bing');
  try {
    var savedEngine = localStorage.getItem(ENGINE_KEY);
    var savedIndex = engineIndexById(savedEngine);
    if (savedIndex >= 0) engineIndex = savedIndex;
  } catch (_) { /* file:// 下存储可能不可用 */ }

  var engineBtn = document.getElementById('engine-btn');
  var engineIcon = document.getElementById('engine-icon');
  var searchForm = document.getElementById('search-form');
  var searchInput = document.getElementById('search-input');

  // 引擎徽标优先用站点自带图标（与快捷方式同一份本地缓存），取不到才回落字标。
  // 一个引擎可能有多个主机别名，任一命中即可。
  var ENGINE_HOSTS = {
    baidu:      ['www.baidu.com', 'baidu.com'],
    bing:       ['www.bing.com', 'cn.bing.com', 'bing.com'],
    google:     ['www.google.com', 'google.com'],
    duckduckgo: ['duckduckgo.com', 'www.duckduckgo.com']
  };

  function engineFaviconUrl(eng) {
    var hosts = ENGINE_HOSTS[eng.id] || [];
    for (var i = 0; i < hosts.length; i++) {
      var hit = faviconMap[hosts[i]];
      if (hit) return hit;
    }
    return '';
  }

  function applyEngine() {
    var eng = ENGINES[engineIndex];
    var iconUrl = engineFaviconUrl(eng);

    engineIcon.textContent = '';
    if (iconUrl) {
      var img = document.createElement('img');
      img.className = 'engine-favicon';
      img.alt = '';
      img.src = iconUrl;
      engineIcon.appendChild(img);
      engineBtn.classList.add('has-icon');
    } else {
      engineIcon.textContent = eng.icon;
      engineBtn.classList.remove('has-icon');
    }

    engineBtn.title = '当前：' + eng.name + '（点击切换）';
    engineBtn.setAttribute('aria-label', '当前搜索引擎：' + eng.name + '，点击切换');
    engineBtn.setAttribute('data-engine', eng.id);
  }

  function persistEngine() {
    var id = ENGINES[engineIndex].id;
    try { localStorage.setItem(ENGINE_KEY, id); } catch (_) { /* 仅影响离线回退 */ }
    bridgeSend({ type: 'setSearchEngine', engine: id });
  }

  engineBtn.addEventListener('click', function () {
    engineIndex = (engineIndex + 1) % ENGINES.length;
    // 200ms 翻转动画：转到 90° 时换字再转回
    engineIcon.classList.add('flip');
    setTimeout(function () {
      applyEngine();
      persistEngine();
      requestEngineIconIfMissing();
    }, reducedMotion ? 0 : 100);
    setTimeout(function () { engineIcon.classList.remove('flip'); }, reducedMotion ? 0 : 200);
    searchInput.focus();
  });

  applyEngine();
  bridgeSend({ type: 'getConfig' });
  bridgeListen(function (msg) {
    if (!msg || msg.type !== 'config' || typeof msg.searchEngine !== 'string') return;
    var index = engineIndexById(msg.searchEngine);
    if (index < 0) return;
    engineIndex = index;
    applyEngine();
    try { localStorage.setItem(ENGINE_KEY, msg.searchEngine); } catch (_) { }
    // R9：引导卡打开时用宿主返回的隐私现状刷新只读清单
    if (lastConfigPrivacy === null) renderOnboardingPrivacy(msg.privacy);
  });

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
     R9 首启引导卡：一次性；只读展示隐私现状 + 选默认搜索引擎。
     硬约束（红线 2）：不提供任何隐私默认值改写入口——引导过程只读展示，
     唯一可写项是默认搜索引擎（既有 setSearchEngine 桥，非隐私默认值）。
     措辞与 docs/product-design.md §5.2 能力边界声明保持一致。
     ------------------------------------------------------------------------ */

  var ONBOARDING_KEY = 'apage.ntp.onboardingSeen';
  var onboardingDialog = document.getElementById('onboarding-dialog');
  var onboardingEngines = document.getElementById('onboarding-engines');
  var onboardingPrivacy = document.getElementById('onboarding-privacy');
  var onboardingDone = document.getElementById('onboarding-done');
  var onboardingReopen = document.getElementById('btn-onboarding');
  var lastConfigPrivacy = null;

  var PRIVACY_LABELS = [
    { key: 'dnt',        on: '发送 Do Not Track：开（声明性请求头，多数站点忽略）' },
    { key: 'gpc',        on: '发送 Sec-GPC：开（声明性请求头，部分法域有法律效力）' },
    { key: 'webrtc',     on: 'WebRTC IP 泄露防护：开（防本地/公网 IP 泄露，不断连会议网站）' },
    { key: 'fetchIcons', on: '获取搜索引擎图标：开（仅白名单四引擎各抓一次，可关）',
                          off: '获取搜索引擎图标：关（不发起图标请求，回落字标）' }
  ];

  function onboardingSeen() {
    try { return localStorage.getItem(ONBOARDING_KEY) === '1'; } catch (_) { return false; }
  }

  function markOnboardingSeen() {
    try { localStorage.setItem(ONBOARDING_KEY, '1'); } catch (_) { /* file:// 下降级：每次启动展示 */ }
  }

  function renderOnboardingEngines() {
    onboardingEngines.textContent = '';
    ENGINES.forEach(function (engine, i) {
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'onboarding-engine' + (i === engineIndex ? ' selected' : '');
      btn.textContent = engine.name;
      btn.setAttribute('role', 'radio');
      btn.setAttribute('aria-checked', i === engineIndex ? 'true' : 'false');
      btn.addEventListener('click', function () {
        engineIndex = i;
        applyEngine();
        persistEngine();
        requestEngineIconIfMissing();
        renderOnboardingEngines();
      });
      onboardingEngines.appendChild(btn);
    });
  }

  function renderOnboardingPrivacy(privacy) {
    lastConfigPrivacy = privacy || null;
    onboardingPrivacy.textContent = '';
    PRIVACY_LABELS.forEach(function (item) {
      var value = privacy ? privacy[item.key] : true; // 配置未达时按隐私默认全开展示
      var li = document.createElement('li');
      var mark = document.createElement('span');
      mark.className = 'onboarding-mark';
      mark.textContent = value ? '✓' : '—';
      var text = document.createElement('span');
      text.textContent = value ? item.on : (item.off || item.on);
      li.appendChild(mark);
      li.appendChild(text);
      onboardingPrivacy.appendChild(li);
    });
  }

  function openOnboarding() {
    renderOnboardingEngines();
    renderOnboardingPrivacy(lastConfigPrivacy);
    if (typeof onboardingDialog.showModal === 'function') {
      onboardingDialog.showModal();
    } else {
      onboardingDialog.setAttribute('open', '');
    }
  }

  onboardingDone.addEventListener('click', function () {
    markOnboardingSeen();
    if (typeof onboardingDialog.close === 'function') onboardingDialog.close();
    else onboardingDialog.removeAttribute('open');
  });

  if (onboardingReopen) {
    onboardingReopen.addEventListener('click', openOnboarding);
  }

  // 首启展示：未看过引导卡时自动打开（localStorage 不可用时降级为每次展示）
  if (!onboardingSeen()) {
    requestAnimationFrame(function () {
      requestAnimationFrame(function () { openOnboarding(); });
    });
  }

  /* ------------------------------------------------------------------------
     d) 快捷方式：本地增删并持久化（最多 11 个，每行最多 6 个）
     ------------------------------------------------------------------------ */

  var SHORTCUT_KEY = 'apage.ntp.shortcuts';
  var MAX_SHORTCUTS = 11;
  var SHORTCUT_TONE_COUNT = 8;
  var DEFAULT_SHORTCUTS = [
    { name: 'Google', icon: 'G',  url: 'https://www.google.com', tone: 0 },
    { name: 'GitHub', icon: 'GH', url: 'https://github.com', tone: 1 },
    { name: 'B站',    icon: 'B',  url: 'https://www.bilibili.com', tone: 2 },
    { name: '知乎',   icon: '知', url: 'https://www.zhihu.com', tone: 3 },
    { name: '微博',   icon: '微', url: 'https://weibo.com', tone: 4 }
  ];

  function toneForUrl(url) {
    var hash = 0;
    for (var i = 0; i < url.length; i++) {
      hash = ((hash << 5) - hash + url.charCodeAt(i)) | 0;
    }
    return Math.abs(hash) % SHORTCUT_TONE_COUNT;
  }

  var shortcutsEl = document.getElementById('shortcuts');
  var shortcutDialog = document.getElementById('shortcut-dialog');
  var shortcutForm = document.getElementById('shortcut-form');
  var shortcutName = document.getElementById('shortcut-name');
  var shortcutUrl = document.getElementById('shortcut-url');
  var shortcutError = document.getElementById('shortcut-error');
  var shortcutCancel = document.getElementById('shortcut-cancel');

  function normalizeShortcutUrl(raw) {
    var text = String(raw || '').trim();
    if (!text) return null;
    if (!/^https?:\/\//i.test(text)) text = 'https://' + text;
    try {
      var parsed = new URL(text);
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
      return parsed.href;
    } catch (_) {
      return null;
    }
  }

  function loadShortcuts() {
    try {
      var saved = localStorage.getItem(SHORTCUT_KEY);
      if (saved !== null) {
        var parsed = JSON.parse(saved);
        if (Array.isArray(parsed)) {
          return parsed
            .map(function (item) {
              var url = item && normalizeShortcutUrl(item.url);
              if (!url) return null;
              var name = String(item.name || '').trim().slice(0, 24);
              return {
                name: name || new URL(url).hostname.replace(/^www\./i, ''),
                url: url,
                icon: String(item.icon || '').slice(0, 2),
                tone: Number.isInteger(item.tone) && item.tone >= 0 && item.tone < SHORTCUT_TONE_COUNT
                  ? item.tone
                  : toneForUrl(url)
              };
            })
            .filter(Boolean)
            .slice(0, MAX_SHORTCUTS);
        }
      }
    } catch (_) { /* 损坏或不可用时回落默认值 */ }

    return DEFAULT_SHORTCUTS.map(function (item) {
      return { name: item.name, url: item.url, icon: item.icon, tone: item.tone };
    });
  }

  var SHORTCUTS = loadShortcuts();

  function persistShortcuts() {
    try { localStorage.setItem(SHORTCUT_KEY, JSON.stringify(SHORTCUTS)); } catch (_) { /* 本地存储不可用时仅当前会话生效 */ }
  }

  function shortcutIcon(name) {
    var clean = String(name || '').trim();
    return clean ? clean.slice(0, 1).toUpperCase() : '·';
  }

  function renderShortcuts() {
    shortcutsEl.textContent = '';

    SHORTCUTS.forEach(function (s, index) {
      var wrap = document.createElement('div');
      wrap.className = 'shortcut-wrap';

      var link = document.createElement('a');
      link.className = 'shortcut-item';
      link.href = s.url;
      link.title = s.name + ' — ' + s.url;
      link.setAttribute('aria-label', s.name);

      var tile = document.createElement('span');
      tile.className = 'shortcut-tile tone-' + (s.tone == null ? toneForUrl(s.url) : s.tone);
      tile.textContent = s.icon || shortcutIcon(s.name);
      var host = hostOfUrl(s.url);
      if (host) tile.setAttribute('data-host', host);
      var label = document.createElement('span');
      label.className = 'shortcut-label';
      label.textContent = s.name;
      link.appendChild(tile);
      link.appendChild(label);
      link.addEventListener('click', function (e) {
        e.preventDefault();
        navigate(s.url);
      });

      var remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'shortcut-remove';
      remove.textContent = '×';
      remove.title = '删除 ' + s.name;
      remove.setAttribute('aria-label', '删除快捷方式：' + s.name);
      remove.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        SHORTCUTS.splice(index, 1);
        persistShortcuts();
        renderShortcuts();
      });

      wrap.appendChild(link);
      wrap.appendChild(remove);
      shortcutsEl.appendChild(wrap);
    });

    if (SHORTCUTS.length < MAX_SHORTCUTS) {
      var addBtn = document.createElement('button');
      addBtn.type = 'button';
      addBtn.className = 'shortcut-item add';
      addBtn.title = '添加快捷方式';
      addBtn.setAttribute('aria-label', '添加快捷方式');
      addBtn.innerHTML = '<span class="shortcut-tile">+</span>' +
                         '<span class="shortcut-label">添加</span>';
      addBtn.addEventListener('click', openShortcutDialog);
      shortcutsEl.appendChild(addBtn);
    }

    // 渲染完就顺带向宿主索要已缓存的站点图标（拿不到的主机保持字标）
    requestFavicons();
  }

  /* ------------------------------------------------------------------------
     d2) 站点图标：只读宿主本地缓存（WebView2 已取回的 favicon）
     不请求 favicon.ico、不走第三方图标服务——访问过的站才有图，这是设计结果。
     ------------------------------------------------------------------------ */

  function hostOfUrl(url) {
    try {
      return new URL(url).hostname.toLowerCase();
    } catch (_) {
      return '';
    }
  }

  function requestFavicons() {
    var hosts = [];
    function add(host) {
      if (host && hosts.indexOf(host) < 0 && hosts.length < 32) hosts.push(host);
    }

    SHORTCUTS.forEach(function (s) { add(hostOfUrl(s.url)); });
    Object.keys(ENGINE_HOSTS).forEach(function (id) {
      ENGINE_HOSTS[id].forEach(add);
    });

    if (!hosts.length) return;
    bridgeSend({ type: 'getFavicons', hosts: hosts });
  }

  // 引擎图标缺失时向宿主申请一次性抓取；同一主机本页只申请一次，失败不重试。
  // 真正的开关与白名单在宿主侧判定，页面无法越权指定主机。
  var engineIconRequested = {};

  function requestEngineIconIfMissing() {
    var eng = ENGINES[engineIndex];
    if (engineFaviconUrl(eng)) return;

    var host = (ENGINE_HOSTS[eng.id] || [])[0];
    if (!host || engineIconRequested[host]) return;
    engineIconRequested[host] = true;
    bridgeSend({ type: 'fetchEngineIcon', host: host });
  }

  function applyFavicons(icons) {
    if (!icons) return;

    Object.keys(icons).forEach(function (host) {
      if (typeof icons[host] === 'string') faviconMap[host] = icons[host];
    });
    applyEngine(); // 引擎徽标与快捷方式同源，一次回复同时刷新
    requestEngineIconIfMissing();

    var tiles = document.querySelectorAll('.shortcut-tile[data-host]');
    Array.prototype.forEach.call(tiles, function (tile) {
      var src = icons[tile.getAttribute('data-host')];
      if (!src || tile.getAttribute('data-icon-src') === src) return;
      tile.setAttribute('data-icon-src', src);

      var img = document.createElement('img');
      img.className = 'shortcut-favicon';
      img.alt = '';
      img.src = src;
      tile.textContent = '';
      tile.appendChild(img);
    });
  }

  function openShortcutDialog() {
    if (!shortcutDialog || typeof shortcutDialog.showModal !== 'function') return;
    shortcutForm.reset();
    shortcutError.textContent = '';
    shortcutDialog.showModal();
    shortcutName.focus();
  }

  if (shortcutForm) {
    shortcutForm.addEventListener('submit', function (e) {
      e.preventDefault();
      var url = normalizeShortcutUrl(shortcutUrl.value);
      if (!url) {
        shortcutError.textContent = '请输入有效的 http/https 网址。';
        shortcutUrl.focus();
        return;
      }

      if (SHORTCUTS.some(function (s) { return s.url === url; })) {
        shortcutError.textContent = '该网址已在快捷方式中。';
        shortcutUrl.focus();
        return;
      }

      var name = shortcutName.value.trim().slice(0, 24);
      if (!name) name = new URL(url).hostname.replace(/^www\./i, '');
      SHORTCUTS.push({
        name: name,
        url: url,
        icon: shortcutIcon(name),
        tone: toneForUrl(url)
      });
      persistShortcuts();
      renderShortcuts();
      shortcutDialog.close();
    });

    shortcutCancel.addEventListener('click', function () { shortcutDialog.close(); });
    shortcutDialog.addEventListener('click', function (e) {
      if (e.target === shortcutDialog) shortcutDialog.close();
    });
    shortcutDialog.addEventListener('close', function () { shortcutError.textContent = ''; });
  }

  bridgeListen(function (msg) {
    if (msg && msg.type === 'favicons') applyFavicons(msg.icons);
  });

  renderShortcuts();

  /* ------------------------------------------------------------------------
     e) 壁纸：6 张内置 CSS 渐变 + 本地图片（file 选择 / 拖入），选择持久化
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
