// Apage WebMessage Spike — 注入脚本（AddScriptToExecuteOnDocumentCreatedAsync，document-start 执行）
// 协议（docs/product-design.md §4.4）：
//   JS → C# : { id, type, payload }
//   C# → JS : { id, ok:true, data } | { id, ok:false, error }
(function () {
    'use strict';
    if (window.apageBridge) return; // 防重复注入

    let seq = 0;
    const pending = new Map(); // id -> { resolve, reject }

    window.chrome.webview.addEventListener('message', function (e) {
        const msg = e.data;
        if (!msg || typeof msg.id === 'undefined') return;
        const entry = pending.get(msg.id);
        if (!entry) return; // 非本桥消息或已超时清理
        pending.delete(msg.id);
        if (msg.ok) entry.resolve(msg.data);
        else entry.reject(new Error(msg.error || 'backend error'));
    });

    window.apageBridge = {
        callBackend: function (type, payload) {
            return new Promise(function (resolve, reject) {
                const id = ++seq;
                pending.set(id, { resolve: resolve, reject: reject });
                window.chrome.webview.postMessage({ id: id, type: type, payload: payload || {} });
            });
        },
        // 测试辅助：观察 pending Map 是否有残留
        __pendingCount: function () { return pending.size; }
    };

    // 全部断言集中在 JS 侧执行，ExecuteScriptAsync 等待 Promise 并取回 JSON 结果
    window.__runSpikeTests = async function () {
        const results = { assertions: [], latency: null };
        const assert = function (name, cond, detail) {
            results.assertions.push({ name: name, pass: !!cond, detail: detail || '' });
        };
        const bridge = window.apageBridge;

        // A1 顺序往返：每个响应的 payload 与其请求一一对应
        try {
            let ok = true, bad = '';
            for (let i = 0; i < 5; i++) {
                const r = await bridge.callBackend('echo', { n: i, tag: 'a1-' + i });
                if (!r || r.n !== i || r.tag !== 'a1-' + i) { ok = false; bad = 'i=' + i + ' got ' + JSON.stringify(r); }
            }
            assert('A1 顺序往返：请求/响应 id 正确关联', ok, bad);
        } catch (e) { assert('A1 顺序往返：请求/响应 id 正确关联', false, String(e)); }

        // A2 并发 20：延迟递减使响应乱序到达，各自必须解析回自己的 n
        try {
            const N = 20;
            const resolutionOrder = [];
            const promises = [];
            for (let i = 0; i < N; i++) {
                const delayMs = (N - 1 - i) * 15; // 先发者延迟最长 → 响应逆序到达
                promises.push(bridge.callBackend('delayedEcho', { n: i, delayMs: delayMs })
                    .then(function (r) { resolutionOrder.push(r.n); return r; }));
            }
            const rs = await Promise.all(promises);
            const allMatched = rs.every(function (r, i) { return r && r.n === i; });
            const outOfOrder = resolutionOrder.some(function (v, idx) { return v !== idx; });
            assert('A2 并发 20 请求：乱序响应全部正确关联', allMatched && outOfOrder,
                'allMatched=' + allMatched + ' outOfOrder=' + outOfOrder +
                ' resolutionOrder=' + resolutionOrder.join(','));
        } catch (e) { assert('A2 并发 20 请求：乱序响应全部正确关联', false, String(e)); }

        // A3 document-start 可用：测试页 head 内联脚本在 DOM 就绪前已调用 callBackend
        try {
            for (let i = 0; i < 100; i++) { // 等待 document-start 发起的 ping 落定
                if (window.__docStartResult || window.__docStartError) break;
                await new Promise(function (r) { setTimeout(r, 20); });
            }
            const avail = window.__docStartAvailable === true;
            const pong = window.__docStartResult;
            const pass = avail && pong && pong.reply === 'pong' && !window.__docStartError;
            assert('A3 注入脚本 document-start 即可用', pass,
                'available=' + avail + ' readyState@call=' + window.__docStartReadyState +
                ' result=' + JSON.stringify(pong) + ' err=' + window.__docStartError);
        } catch (e) { assert('A3 注入脚本 document-start 即可用', false, String(e)); }

        // A4 错误路径：C# 抛异常 → JS reject，且通道不受影响可继续复用
        try {
            let rejected = false, msg = '';
            try { await bridge.callBackend('boom', {}); }
            catch (e) { rejected = true; msg = String((e && e.message) || e); }
            const after = await bridge.callBackend('ping', {});
            assert('A4 错误路径：C# 异常→JS reject 且通道可复用',
                rejected && msg.indexOf('spike-boom') >= 0 && after && after.reply === 'pong',
                'rejected=' + rejected + ' msg=' + msg);
        } catch (e) { assert('A4 错误路径：C# 异常→JS reject 且通道可复用', false, String(e)); }

        // A5 往返延迟实测：100 次顺序 ping
        try {
            const ds = [];
            for (let i = 0; i < 100; i++) {
                const t0 = performance.now();
                await bridge.callBackend('ping', { i: i });
                ds.push(performance.now() - t0);
            }
            ds.sort(function (a, b) { return a - b; });
            const sum = ds.reduce(function (a, b) { return a + b; }, 0);
            results.latency = {
                count: ds.length,
                avgMs: +(sum / ds.length).toFixed(2),
                minMs: +ds[0].toFixed(2),
                p50Ms: +ds[Math.floor(ds.length * 0.5)].toFixed(2),
                p95Ms: +ds[Math.min(ds.length - 1, Math.floor(ds.length * 0.95))].toFixed(2),
                maxMs: +ds[ds.length - 1].toFixed(2)
            };
            assert('A5 延迟实测：100 次 ping 全部成功', ds.length === 100, JSON.stringify(results.latency));
        } catch (e) { assert('A5 延迟实测：100 次 ping 全部成功', false, String(e)); }

        // A6 pending Map 无泄漏：所有已发请求都已结清
        try {
            assert('A6 pending Map 无泄漏', bridge.__pendingCount() === 0,
                'pending=' + bridge.__pendingCount());
        } catch (e) { assert('A6 pending Map 无泄漏', false, String(e)); }

        return results;
    };
})();
