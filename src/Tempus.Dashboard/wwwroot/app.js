/* Tempus dashboard — vanilla JS, hash-routed, offline. */
(function () {
  'use strict';

  const BASE = (window.TEMPUS_BASE || '').replace(/\/$/, '');
  const API = BASE + '/api';

  // ------------------------------------------------------------ state
  const state = {
    meta: null,
    view: null,          // { refresh(), interval }
    modalOpen: 0,
    lastRefresh: 0,
    functions: null,
    policies: null,
    timezones: null,
  };

  // ------------------------------------------------------------ helpers
  const $ = (sel, root) => (root || document).querySelector(sel);
  const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const h = (html) => { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild; };

  function storage(key, value) {
    try {
      if (value === undefined) return localStorage.getItem(key);
      if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value);
    } catch (e) { return null; }
  }

  async function api(path, opts) {
    opts = opts || {};
    const init = { method: opts.method || 'GET', cache: 'no-store', headers: { 'Accept': 'application/json' } };
    if (opts.body !== undefined) { init.headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(opts.body); }
    let res;
    try { res = await fetch(API + path, init); }
    catch (e) { throw new Error('Network error: ' + e.message); }
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = text; }
    if (res.status === 401) { throw new Error('Not authenticated. Reload the page to sign in.'); }
    if (res.status === 403) { throw new Error((data && data.error) || 'The dashboard is read-only for this user.'); }
    if (res.status === 429) { throw new Error('Too many failed sign-in attempts. Try again later.'); }
    if (!res.ok) { throw new Error((data && (data.error || data.title)) || ('HTTP ' + res.status)); }
    if (data && data.ok === false) throw new Error(data.error || 'Operation failed.');
    return data;
  }

  async function mutate(path, opts, okMsg) {
    try {
      const r = await api(path, Object.assign({ method: 'POST' }, opts || {}));
      if (okMsg) toast(okMsg, 'ok');
      refreshNow();
      return r;
    } catch (e) { toast(e.message, 'error'); return null; }
  }

  function toast(msg, kind) {
    let box = $('.toasts');
    if (!box) { box = h('<div class="toasts"></div>'); document.body.appendChild(box); }
    const t = h(`<div class="toast ${kind || ''}">${esc(msg)}</div>`);
    box.appendChild(t);
    setTimeout(() => { t.style.opacity = '0'; t.style.transition = 'opacity .3s'; setTimeout(() => t.remove(), 300); }, kind === 'error' ? 6000 : 3000);
  }

  function confirmDialog(message, label) {
    return new Promise(resolve => {
      const m = modal({ title: 'Confirm', body: `<p>${esc(message)}</p>`, footer: `<button class="btn" data-act="no">Cancel</button><button class="btn ${label === 'Delete' ? 'danger' : 'primary'}" data-act="yes">${esc(label || 'Confirm')}</button>` });
      m.el.addEventListener('click', e => {
        const act = e.target.getAttribute && e.target.getAttribute('data-act');
        if (act === 'yes') { m.close(); resolve(true); }
        if (act === 'no') { m.close(); resolve(false); }
      });
      m.onClose = () => resolve(false);
    });
  }

  function promptDialog(title, fieldsHtml, label) {
    return new Promise(resolve => {
      const m = modal({ title, body: `<form class="form">${fieldsHtml}</form>`, footer: `<button class="btn" data-act="no">Cancel</button><button class="btn primary" data-act="yes">${esc(label || 'OK')}</button>` });
      m.el.addEventListener('click', e => {
        const act = e.target.getAttribute && e.target.getAttribute('data-act');
        if (act === 'yes') { const data = formData($('form', m.el)); m.close(); resolve(data); }
        if (act === 'no') { m.close(); resolve(null); }
      });
      m.onClose = () => resolve(null);
    });
  }

  function modal(opts) {
    state.modalOpen++;
    const el = h(`<div class="modal-backdrop"><div class="modal ${opts.wide ? 'wide' : ''}"><header><h2>${esc(opts.title)}</h2><button class="close" title="Close">&times;</button></header><div class="body">${opts.body || ''}</div>${opts.footer !== undefined ? `<footer>${opts.footer}</footer>` : ''}</div></div>`);
    document.body.appendChild(el);
    let closed = false;
    const m = {
      el,
      onClose: null,
      close() { if (closed) return; closed = true; state.modalOpen--; el.remove(); document.removeEventListener('keydown', onKey); if (m.onClose) m.onClose(); },
      body: $('.body', el),
      footer: $('footer', el),
    };
    const onKey = e => { if (e.key === 'Escape') m.close(); };
    document.addEventListener('keydown', onKey);
    $('.close', el).addEventListener('click', () => m.close());
    el.addEventListener('mousedown', e => { if (e.target === el) m.close(); });
    return m;
  }

  function formData(form) {
    const data = {};
    $$('input,select,textarea', form).forEach(el => {
      if (!el.name) return;
      if (el.type === 'checkbox') data[el.name] = el.checked;
      else if (el.type === 'number') data[el.name] = el.value === '' ? null : Number(el.value);
      else data[el.name] = el.value === '' ? null : el.value;
    });
    return data;
  }

  // ---- date/time
  const dtf = {};
  function formatter(opts) {
    const tz = (state.meta && state.meta.timeZone) || 'UTC';
    const key = tz + JSON.stringify(opts);
    if (!dtf[key]) {
      try { dtf[key] = new Intl.DateTimeFormat(undefined, Object.assign({ timeZone: tz }, opts)); }
      catch (e) { dtf[key] = new Intl.DateTimeFormat(undefined, Object.assign({ timeZone: 'UTC' }, opts)); }
    }
    return dtf[key];
  }
  function fmtDate(iso) {
    if (!iso) return '<span class="faint">—</span>';
    const d = new Date(iso);
    if (isNaN(d)) return esc(iso);
    return `<span class="nowrap" title="${esc(d.toISOString())}">${esc(formatter({ year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(d))}</span>`;
  }
  function fmtRel(iso) {
    if (!iso) return '';
    const diff = (new Date(iso) - Date.now()) / 1000;
    const abs = Math.abs(diff);
    const unit = abs < 60 ? [abs, 's'] : abs < 3600 ? [abs / 60, 'm'] : abs < 86400 ? [abs / 3600, 'h'] : [abs / 86400, 'd'];
    const n = Math.round(unit[0]);
    return diff < 0 ? `${n}${unit[1]} ago` : `in ${n}${unit[1]}`;
  }
  function fmtMs(ms) {
    if (ms == null) return '<span class="faint">—</span>';
    if (ms < 1000) return ms + ' ms';
    if (ms < 60000) return (ms / 1000).toFixed(1) + ' s';
    return (ms / 60000).toFixed(1) + ' min';
  }
  function fmtTs(iso) {
    const d = new Date(iso);
    return isNaN(d) ? '' : formatter({ hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(d) + '.' + String(d.getMilliseconds()).padStart(3, '0');
  }
  function toLocalInput(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }
  function fromLocalInput(v) { return v ? new Date(v).toISOString() : null; }

  // ---- json
  function pretty(json) {
    if (json == null || json === '') return '<span class="faint">none</span>';
    try { return `<pre class="json">${esc(JSON.stringify(JSON.parse(json), null, 2))}</pre>`; }
    catch (e) { return `<pre class="json">${esc(json)}</pre>`; }
  }
  function validJson(s) { if (!s || !s.trim()) return true; try { JSON.parse(s); return true; } catch (e) { return false; } }

  // ---- badges
  const STATUS_KIND = { Idle: '', Queued: 'info', InProgress: 'info pulse', Done: 'ok', DueDone: 'ok', Failed: 'danger', Cancelled: 'warn', Skipped: '' };
  const STATUS_NAMES = ['Idle', 'Queued', 'InProgress', 'Done', 'DueDone', 'Failed', 'Cancelled', 'Skipped'];
  const TERMINAL = ['Done', 'DueDone', 'Failed', 'Cancelled', 'Skipped'];
  function statusName(s) { return typeof s === 'number' ? STATUS_NAMES[s] || String(s) : s; }
  function badge(status, text) {
    const s = statusName(status);
    const label = text || (s === 'InProgress' ? 'Running' : s === 'Idle' ? 'Waiting' : s === 'DueDone' ? 'Done (late)' : s);
    return `<span class="badge ${STATUS_KIND[s] || ''}"><span class="dot"></span>${esc(label)}</span>`;
  }
  function chainBadge(summary, rootId) {
    if (!summary) return '';
    const kind = summary.label === 'Done' ? 'ok' : summary.label === 'Running' ? 'info pulse' : summary.label === 'Finished with errors' ? 'danger' : summary.label === 'Waiting' ? '' : 'warn';
    const inner = `${esc(summary.label)} ${summary.finished}/${summary.total}`;
    return rootId ? `<a class="badge purple ${kind}" href="#/chains/${rootId}" title="Open chain">⛓ ${inner}</a>` : `<span class="badge purple">${inner}</span>`;
  }
  function tag(text, cls) { return `<span class="tag ${cls || ''}">${esc(text)}</span>`; }

  function readOnly() { return !!(state.meta && state.meta.readOnly); }
  function wbtn(html) { return readOnly() ? '' : html; }

  // ---- lookups
  async function loadFunctions() { if (!state.functions) state.functions = await api('/functions'); return state.functions; }
  async function loadPolicies() { state.policies = await api('/policies'); return state.policies; }
  async function loadTimezones() { if (!state.timezones) state.timezones = await api('/timezones'); return state.timezones; }
  function options(list, selected, labelFn, valueFn) {
    return list.map(x => { const v = valueFn ? valueFn(x) : x; return `<option value="${esc(v)}" ${v === selected ? 'selected' : ''}>${esc(labelFn ? labelFn(x) : x)}</option>`; }).join('');
  }
  const enumOpts = (values, sel) => options(values, sel || values[0]);
  const PRIORITIES = ['Normal', 'High', 'Low', 'LongRunning'];
  const RUN_CONDITIONS = ['OnSuccess', 'OnFailure', 'OnCancelled', 'OnFailureOrCancelled', 'OnAnyCompletedStatus', 'OnAnyTerminalStatus', 'InProgress'];
  const STALE = ['Restart', 'Cancel'];
  const BLOCKED = ['Reject', 'Skip', 'MoveForward'];
  const CATCHUP = ['Skip', 'RunLatest'];

  // ------------------------------------------------------------ shell
  const ICONS = {
    overview: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="9"/><rect x="14" y="3" width="7" height="5"/><rect x="14" y="12" width="7" height="9"/><rect x="3" y="16" width="7" height="5"/></svg>',
    time: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>',
    cron: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/></svg>',
    exec: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 6h16M4 12h16M4 18h10"/></svg>',
    policy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="17" rx="2"/><path d="M3 9h18M8 2v4M16 2v4"/></svg>',
    nodes: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="4" width="20" height="6" rx="1"/><rect x="2" y="14" width="20" height="6" rx="1"/><path d="M6 7h.01M6 17h.01"/></svg>',
    diag: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 12h4l3-8 4 16 3-8h4"/></svg>',
    about: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 8h.01M11 12h1v4h1"/></svg>',
    sun: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>',
    moon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>',
  };
  const NAV = [
    { group: 'Monitor' },
    { href: '#/', label: 'Overview', icon: 'overview', match: /^#\/?$/ },
    { href: '#/executions', label: 'Executions', icon: 'exec', match: /^#\/executions/ },
    { group: 'Jobs' },
    { href: '#/time-tickers', label: 'Time tickers', icon: 'time', match: /^#\/(time-tickers|chains)/ },
    { href: '#/cron-tickers', label: 'Cron tickers', icon: 'cron', match: /^#\/(cron-tickers|occurrences)/ },
    { href: '#/policies', label: 'Schedule policies', icon: 'policy', match: /^#\/policies/ },
    { group: 'Cluster' },
    { href: '#/nodes', label: 'Nodes', icon: 'nodes', match: /^#\/nodes/ },
    { href: '#/diagnostics', label: 'Diagnostics', icon: 'diag', match: /^#\/diagnostics/ },
    { href: '#/about', label: 'About', icon: 'about', match: /^#\/about/ },
  ];

  function applyTheme() {
    const t = storage('tempus.theme');
    if (t === 'dark' || t === 'light') document.documentElement.setAttribute('data-theme', t); else document.documentElement.removeAttribute('data-theme');
  }
  function toggleTheme() {
    const cur = document.documentElement.getAttribute('data-theme') || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    storage('tempus.theme', cur === 'dark' ? 'light' : 'dark');
    applyTheme();
    renderTopbar();
  }

  function renderShell() {
    const m = state.meta;
    const app = $('#app');
    app.innerHTML = '';
    app.appendChild(h(`<div class="layout">
      <aside class="sidebar">
        <div class="brand">${m.logoUrl ? `<img src="${esc(m.logoUrl)}" alt="">` : '<div class="logo">T</div>'}<div><div class="title">${esc(m.title)}</div><div class="sub">${esc(m.namespace)}${m.environment ? ' · ' + esc(m.environment) : ''}</div></div></div>
        <nav class="nav">${NAV.map(n => n.group ? `<div class="group">${esc(n.group)}</div>` : `<a href="${n.href}" data-match="${n.match.source}">${ICONS[n.icon]}<span>${esc(n.label)}</span></a>`).join('')}</nav>
        <div class="footer">Tempus ${esc(m.version || '')}<br>${esc(m.node)}${m.user ? '<br>Signed in as ' + esc(m.user) : ''}</div>
      </aside>
      <div class="main"><div class="topbar"></div><div class="content" id="content"></div></div>
    </div>`));
    renderTopbar();
  }

  function renderTopbar() {
    const bar = $('.topbar');
    if (!bar || !state.meta) return;
    const m = state.meta;
    const dark = document.documentElement.getAttribute('data-theme') === 'dark' || (!document.documentElement.getAttribute('data-theme') && matchMedia('(prefers-color-scheme: dark)').matches);
    bar.innerHTML = `
      <span class="badge ${m.schedulerRunning ? 'ok' : 'warn'}"><span class="dot"></span>Scheduler ${m.schedulerRunning ? 'running' : 'stopped'}</span>
      ${wbtn(m.schedulerRunning ? '<button class="btn sm" data-act="stop">Stop</button>' : '<button class="btn sm primary" data-act="start">Start</button>')}
      ${m.readOnly ? '<span class="badge warn">Read-only</span>' : ''}
      ${m.environment ? `<span class="badge accent">${esc(m.environment)}</span>` : ''}
      <span class="spacer"></span>
      <span class="small muted">Times in ${esc(m.timeZone)}</span>
      <button class="theme-btn" title="Toggle theme">${dark ? ICONS.sun : ICONS.moon}</button>`;
    $('.theme-btn', bar).addEventListener('click', toggleTheme);
    bar.addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act');
      if (act === 'stop') { if (await confirmDialog('Stop the scheduler on this host? Queued work stays in the store; running jobs drain within the shutdown timeout.', 'Stop')) { await mutate('/scheduler/stop', {}, 'Scheduler stopped'); await loadMeta(); } }
      if (act === 'start') { await mutate('/scheduler/start', {}, 'Scheduler started'); await loadMeta(); }
    });
  }

  function highlightNav() {
    const hash = location.hash || '#/';
    $$('.nav a').forEach(a => { const re = new RegExp(a.getAttribute('data-match')); a.classList.toggle('active', re.test(hash)); });
  }

  async function loadMeta() {
    const prev = state.meta;
    state.meta = await api('/meta');
    if (!prev) renderShell(); else renderTopbar();
  }

  // ------------------------------------------------------------ router
  const routes = [
    [/^#\/?$/, viewOverview],
    [/^#\/time-tickers\/?$/, viewTimeTickers],
    [/^#\/time-tickers\/([0-9a-f-]{36})$/i, viewTimeTickerDetail],
    [/^#\/chains\/([0-9a-f-]{36})(?:\/([0-9a-f-]{36}))?$/i, viewChain],
    [/^#\/cron-tickers\/?$/, viewCronTickers],
    [/^#\/cron-tickers\/([0-9a-f-]{36})$/i, viewCronDetail],
    [/^#\/occurrences\/([0-9a-f-]{36})$/i, viewOccurrenceDetail],
    [/^#\/executions\/?$/, viewExecutions],
    [/^#\/policies\/?$/, viewPolicies],
    [/^#\/nodes\/?$/, viewNodes],
    [/^#\/diagnostics\/?$/, viewDiagnostics],
    [/^#\/about\/?$/, viewAbout],
  ];

  async function route() {
    const hash = location.hash || '#/';
    highlightNav();
    const content = $('#content');
    for (const [re, fn] of routes) {
      const m = hash.match(re);
      if (m) {
        state.view = null;
        content.innerHTML = '<div class="loading">Loading…</div>';
        try { await fn(content, ...m.slice(1)); }
        catch (e) { content.innerHTML = `<div class="err-box">${esc(e.message)}</div>`; console.error(e); }
        return;
      }
    }
    content.innerHTML = `<div class="empty">Page not found. <a href="#/">Go to the overview</a>.</div>`;
  }

  function setView(refresh, interval) {
    state.view = { refresh, interval: interval || 5000 };
    state.lastRefresh = Date.now();
  }
  function refreshNow() { if (state.view) { state.lastRefresh = 0; } }
  setInterval(async () => {
    if (!state.view || state.modalOpen > 0 || document.hidden) return;
    if (Date.now() - state.lastRefresh < state.view.interval) return;
    state.lastRefresh = Date.now();
    try { await state.view.refresh(); } catch (e) { console.warn('refresh failed', e); }
  }, 500);
  setInterval(() => { if (state.modalOpen === 0 && !document.hidden) loadMeta().catch(() => {}); }, 15000);

  // ------------------------------------------------------------ views: overview
  async function viewOverview(content) {
    async function render() {
      const o = await api('/overview');
      const counts = (d) => STATUS_NAMES.map(s => { const n = (d && (d[s] != null ? d[s] : d[STATUS_NAMES.indexOf(s)])) || 0; return n ? `<span class="badge ${STATUS_KIND[s] ? STATUS_KIND[s].replace(' pulse', '') : ''}">${esc(s === 'InProgress' ? 'Running' : s)} ${n}</span>` : ''; }).filter(Boolean).join(' ') || '<span class="faint">none</span>';
      const list = (rows, upcoming) => rows.length ? `<ul class="mini-list">${rows.map(r => `<li>${badge(r.status)}<a class="fn" href="${linkFor(r)}">${esc(r.function)}</a><span class="small muted nowrap">${upcoming ? fmtDate(r.executionTime) + ' <span class="faint">' + esc(fmtRel(r.executionTime)) + '</span>' : fmtDate(r.executedAt) + (r.elapsedMs != null ? ' · ' + fmtMs(r.elapsedMs) : '')}</span></li>`).join('')}</ul>` : '<div class="empty">Nothing yet</div>';
      content.innerHTML = `
        <div class="page-head"><div><h1>Overview</h1><div class="muted small">Namespace ${esc(state.meta.namespace)} · node ${esc(state.meta.node)}</div></div></div>
        <div class="grid cols-4">
          <div class="card stat"><span class="label">Scheduler</span><span class="value">${o.schedulerRunning ? 'Running' : 'Stopped'}</span><span class="hint">${o.nextWakeUp ? 'next wake ' + esc(fmtRel(o.nextWakeUp)) : ''}</span></div>
          <div class="card stat"><span class="label">Active nodes</span><span class="value">${o.nodes}</span><span class="hint"><a href="#/nodes">view cluster</a></span></div>
          <div class="card stat"><span class="label">Running / queued (this host)</span><span class="value">${o.running} / ${o.queued}</span><span class="hint">queues H ${o.queues.high} · N ${o.queues.normal} · L ${o.queues.low}</span></div>
          <div class="card stat"><span class="label">Cron definitions</span><span class="value">${o.crons.enabled}<span class="muted" style="font-size:14px"> / ${o.crons.total} enabled</span></span><span class="hint"><a href="#/cron-tickers">manage</a></span></div>
        </div>
        <div class="grid cols-3" style="margin-top:14px">
          <div class="card"><h3>Since start (this host)</h3><dl class="kv"><dt>Completed</dt><dd>${o.totals.completed}</dd><dt>Failed</dt><dd>${o.totals.failed}</dd><dt>Cancelled</dt><dd>${o.totals.cancelled}</dd><dt>Skipped</dt><dd>${o.totals.skipped}</dd><dt>Retried</dt><dd>${o.totals.retried}</dd><dt>Stale recovered</dt><dd>${o.totals.staleRecovered}</dd></dl></div>
          <div class="card"><h3>Time tickers</h3><div class="tags">${counts(o.timeTickers)}</div><div style="margin-top:10px"><a href="#/time-tickers">Open time tickers →</a></div></div>
          <div class="card"><h3>Cron occurrences</h3><div class="tags">${counts(o.occurrences)}</div><div style="margin-top:10px"><a href="#/executions">Open executions →</a></div></div>
        </div>
        <div class="grid cols-2" style="margin-top:14px">
          <div class="card"><h3>Recent executions</h3>${list(o.recent, false)}</div>
          <div class="card"><h3>Upcoming</h3>${list(o.upcoming, true)}</div>
        </div>`;
    }
    await render();
    setView(render, 5000);
  }

  function linkFor(s) { return s.type === 'CronTickerOccurrence' ? `#/occurrences/${s.id}` : `#/time-tickers/${s.id}`; }

  // ------------------------------------------------------------ views: time tickers
  async function viewTimeTickers(content) {
    const filters = { function: '', status: '', kind: '' };
    content.innerHTML = `
      <div class="page-head"><div><h1>Time tickers</h1><div class="muted small">One-time jobs and their chains</div></div><span class="spacer"></span>
        <div class="actions">${wbtn('<button class="btn primary" id="create">+ Create job</button>')}</div></div>
      <div class="card">
        <div class="filters">
          <input type="search" id="f-fn" placeholder="Search function…" style="min-width:220px">
          <select id="f-status"><option value="">Any status</option>${STATUS_NAMES.map(s => `<option value="${s}">${s}</option>`).join('')}</select>
          <select id="f-kind"><option value="">All jobs</option><option value="standalone">Standalone</option><option value="chains">Chains</option></select>
          <span class="spacer"></span><span class="small muted" id="count"></span>
        </div>
        <div class="table-wrap" id="tbl"></div>
      </div>`;
    const tbl = $('#tbl', content);
    async function render() {
      const q = new URLSearchParams();
      if (filters.function) q.set('function', filters.function);
      if (filters.status) q.set('status', filters.status);
      if (filters.kind) q.set('kind', filters.kind);
      const rows = await api('/time-tickers?' + q.toString());
      $('#count', content).textContent = rows.length + ' job(s)';
      tbl.innerHTML = rows.length ? `<table><thead><tr><th>Function</th><th>Status</th><th>Scheduled</th><th>Executed</th><th>Duration</th><th>Retries</th><th>Chain</th><th>Origin</th></tr></thead><tbody>
        ${rows.map(r => `<tr class="clickable" data-href="#/time-tickers/${r.id}"><td><div>${esc(r.function)}</div>${r.description ? `<div class="small muted">${esc(r.description)}</div>` : ''}</td>
          <td>${badge(r.status)}${r.status === 'InProgress' && r.progressPercent != null ? `<div class="small muted">${r.progressPercent}% ${esc(r.progressMessage || '')}</div>` : ''}</td>
          <td>${fmtDate(r.executionTime)}${r.adjustmentReason ? ' <span class="badge warn" title="' + esc(r.adjustmentReason) + '">moved</span>' : ''}</td><td>${fmtDate(r.executedAt)}</td><td>${fmtMs(r.elapsedMilliseconds)}</td>
          <td>${r.retryCount}/${r.retries}</td><td>${r.isChain ? chainBadge(r.chain, r.id) : '<span class="faint">—</span>'}</td>
          <td>${r.origin !== 'Manual' ? `<span class="badge accent">${esc(r.origin)}</span>` : ''}${r.seedKey ? ` <span class="badge" title="Seed key ${esc(r.seedKey)}">From code</span>` : ''}</td></tr>`).join('')}
        </tbody></table>` : '<div class="empty">No time tickers match.</div>';
    }
    tbl.addEventListener('click', e => { const tr = e.target.closest('tr[data-href]'); if (tr && !e.target.closest('a')) location.hash = tr.getAttribute('data-href'); });
    let deb;
    $('#f-fn', content).addEventListener('input', e => { filters.function = e.target.value.trim(); clearTimeout(deb); deb = setTimeout(render, 250); });
    $('#f-status', content).addEventListener('change', e => { filters.status = e.target.value; render(); });
    $('#f-kind', content).addEventListener('change', e => { filters.kind = e.target.value; render(); });
    const create = $('#create', content);
    if (create) create.addEventListener('click', () => openJobForm(null));
    await render();
    setView(render, 5000);
  }

  function stepRow(step) {
    step = step || {};
    return `<div class="step">
      <select name="function">${options(state.functions, step.function || '', f => f.name, f => f.name)}</select>
      <textarea name="request" placeholder='{"json": "request"}'>${esc(step.request || '')}</textarea>
      <select name="runCondition">${enumOpts(RUN_CONDITIONS, step.runCondition)}</select>
      <input type="number" name="retries" min="0" value="${step.retries || 0}" title="Retries">
      <button type="button" class="btn sm" data-act="remove-step" title="Remove">✕</button>
    </div>`;
  }
  function readSteps(container) {
    return $$('.step', container).map(s => ({
      function: $('[name=function]', s).value, request: $('[name=request]', s).value || null, runCondition: $('[name=runCondition]', s).value, retries: Number($('[name=retries]', s).value || 0),
      retryIntervals: [], isGroup: false,
    }));
  }

  async function openJobForm(existing) {
    await loadFunctions();
    await loadPolicies();
    const e = existing || {};
    const m = modal({
      title: existing ? 'Edit job' : 'Create job', wide: true,
      body: `<form class="form" id="job-form">
        <div class="row">
          <div class="field"><label>Function</label><select name="function">${options(state.functions, e.function || (state.functions[0] && state.functions[0].name), f => f.name + (f.requestType ? ' (' + f.requestType + ')' : ''), f => f.name)}</select></div>
          <div class="field"><label>Execution time (${esc(Intl.DateTimeFormat().resolvedOptions().timeZone)} local)</label><input type="datetime-local" name="executionTime" value="${toLocalInput(e.executionTime)}"><span class="help">Leave empty to run as soon as possible.</span></div>
        </div>
        <div class="field"><label>Request (JSON)</label><textarea name="request" placeholder='{"orderId": "…"}'>${esc(e.request || '')}</textarea><span class="help">Validated against the function's request type when it declares one.</span></div>
        <div class="field"><label>Description</label><input name="description" value="${esc(e.description || '')}"></div>
        <div class="row cols-3">
          <div class="field"><label>Retries</label><input type="number" name="retries" min="0" value="${e.retries || 0}"></div>
          <div class="field"><label>Retry intervals (seconds, comma-separated)</label><input name="retryIntervals" value="${esc((e.retryIntervals || []).join(', '))}" placeholder="10, 30, 60"></div>
          <div class="field"><label>Timeout (seconds)</label><input type="number" name="timeoutSeconds" min="0" value="${e.timeoutSeconds != null ? e.timeoutSeconds : ''}" placeholder="scheduler default"></div>
        </div>
        <details class="adv"><summary>Advanced settings</summary>
          <div class="form">
            <div class="row cols-3">
              <div class="field"><label>Priority</label><select name="priority">${enumOpts(PRIORITIES, e.priority)}</select></div>
              <div class="field"><label>Schedule policy</label><select name="schedulePolicyName"><option value="">none</option>${options(state.policies.map(p => p.policy.name), e.schedulePolicyName || '')}</select></div>
              <div class="field"><label>When blocked</label><select name="whenBlocked">${enumOpts(BLOCKED, e.whenBlocked)}</select></div>
            </div>
            <div class="row cols-3">
              <div class="field"><label>Skip after (seconds late)</label><input type="number" name="skipAfterSeconds" min="1" value="${e.skipAfterSeconds != null ? e.skipAfterSeconds : ''}" placeholder="run whenever"></div>
              <div class="field"><label>On stale lease</label><select name="onStale">${enumOpts(STALE, e.onStale)}</select></div>
              <div class="field"><label>Max stale restarts</label><input type="number" name="maxStaleRestarts" min="0" value="${e.maxStaleRestarts != null ? e.maxStaleRestarts : ''}" placeholder="scheduler default"></div>
            </div>
            <div class="row"><div class="field"><label>Chain delete behavior</label><select name="deleteBehavior">${enumOpts(['Cascade', 'Restrict'], e.deleteBehavior)}</select></div></div>
          </div>
        </details>
        ${existing ? '' : `<fieldset><legend>Chain steps (optional, run one after another)</legend><div class="steps" id="steps"></div><div style="margin-top:8px"><button type="button" class="btn sm" data-act="add-step">+ Add step</button></div></fieldset>`}
        <div class="test-result" id="test-result"></div>
      </form>`,
      footer: `<button type="button" class="btn" data-act="test">Test once</button><span class="spacer" style="flex:1"></span><button type="button" class="btn" data-act="cancel">Cancel</button><button type="button" class="btn primary" data-act="save">${existing ? 'Save' : 'Create'}</button>`,
    });
    const form = $('#job-form', m.el);
    const reqEl = $('[name=request]', form);
    reqEl.addEventListener('input', () => reqEl.classList.toggle('invalid', !validJson(reqEl.value)));
    function collect() {
      const d = formData(form);
      if (!validJson(d.request || '')) { toast('Request is not valid JSON.', 'error'); return null; }
      const body = {
        function: d.function, request: d.request, executionTime: fromLocalInput(d.executionTime), description: d.description, retries: d.retries || 0,
        retryIntervals: (d.retryIntervals || '').split(',').map(s => s.trim()).filter(Boolean).map(Number).filter(n => !isNaN(n)),
        timeoutSeconds: d.timeoutSeconds, priority: d.priority, schedulePolicyName: d.schedulePolicyName, whenBlocked: d.whenBlocked, skipAfterSeconds: d.skipAfterSeconds,
        onStale: d.onStale, maxStaleRestarts: d.maxStaleRestarts, deleteBehavior: d.deleteBehavior, isGroup: false,
      };
      if (!existing) {
        const steps = readSteps($('#steps', form));
        for (const s of steps) if (!validJson(s.request || '')) { toast('A chain step request is not valid JSON.', 'error'); return null; }
        // nest linear chain
        let cursor = body;
        for (const s of steps) { cursor.children = [s]; cursor = s; }
      }
      return body;
    }
    m.el.addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act');
      if (!act) return;
      if (act === 'cancel') m.close();
      if (act === 'add-step') $('#steps', form).insertAdjacentHTML('beforeend', stepRow());
      if (act === 'remove-step') e.target.closest('.step').remove();
      if (act === 'test') {
        const body = collect(); if (!body) return;
        const box = $('#test-result', form);
        box.innerHTML = '<div class="loading">Running test…</div>';
        try {
          const r = await api('/test-run', { method: 'POST', body: { function: body.function, request: body.request, timeoutSeconds: body.timeoutSeconds || 60 } });
          box.innerHTML = `<div class="card"><h3>Test run ${r.succeeded ? '<span class="badge ok">succeeded</span>' : '<span class="badge danger">failed</span>'} · ${fmtMs(r.elapsedMilliseconds)}</h3>
            ${r.error ? `<div class="err-box">${esc(r.error)}</div>` : ''}${r.resultJson ? `<h3 style="margin-top:10px">Result</h3>${pretty(r.resultJson)}` : ''}
            <h3 style="margin-top:10px">Logs</h3>${renderLogs(r.logs || [])}</div>`;
        } catch (err) { box.innerHTML = `<div class="err-box">${esc(err.message)}</div>`; }
      }
      if (act === 'save') {
        const body = collect(); if (!body) return;
        const r = existing ? await mutate(`/time-tickers/${existing.id}`, { method: 'PUT', body }, 'Job updated') : await mutate('/time-tickers', { body }, 'Job created');
        if (r) { m.close(); if (!existing && r.result && r.result.id) location.hash = `#/time-tickers/${r.result.id}`; }
      }
    });
  }

  // ------------------------------------------------------------ logs
  function renderLogs(lines, truncated) {
    if (!lines || !lines.length) return '<div class="empty small">No log lines captured.</div>';
    return `<div class="logs">${truncated ? '<div class="log-line lifecycle"><span></span><span></span><span>… earlier lines were dropped (tail limit)</span></div>' : ''}${lines.map(l => `<div class="log-line ${l.lifecycle ? 'lifecycle' : ''} ${esc(l.level)}"><span class="ts">${esc(fmtTs(l.timestamp))}</span><span class="lvl">${esc(l.level === 'Information' ? 'info' : l.level.toLowerCase())}</span><span>${esc(l.message)}${l.exception ? `<span class="ex">${esc(l.exception)}</span>` : ''}</span></div>`).join('')}</div>`;
  }
  async function logPanel(container, id) {
    const r = await api(`/executions/${id}/logs`);
    container.innerHTML = `<div class="log-head"><h3 style="margin:0">Logs</h3>${r.live ? '<span class="badge info pulse"><span class="dot"></span>live</span>' : ''}<span class="small faint">${r.lines.length} line(s)${r.truncated ? ' · truncated' : ''}</span></div>${renderLogs(r.lines, r.truncated)}`;
    return r.live;
  }

  // ------------------------------------------------------------ views: time ticker detail
  function execFields(t) {
    return `<dl class="kv">
      <dt>Id</dt><dd class="mono">${esc(t.id)} <span class="copy" data-copy="${esc(t.id)}">copy</span></dd>
      <dt>Function</dt><dd>${esc(t.function)}</dd>
      ${t.description ? `<dt>Description</dt><dd>${esc(t.description)}</dd>` : ''}
      <dt>Status</dt><dd>${badge(t.status)}${t.cancellationRequested && t.status === 'InProgress' ? ' <span class="badge warn">cancellation requested</span>' : ''}</dd>
      <dt>Scheduled for</dt><dd>${fmtDate(t.executionTime)} <span class="faint small">${esc(fmtRel(t.executionTime))}</span></dd>
      ${t.requestedExecutionTime && t.adjustmentReason ? `<dt>Requested</dt><dd>${fmtDate(t.requestedExecutionTime)}<div class="small muted">${esc(t.adjustmentReason)}</div></dd>` : ''}
      <dt>Started</dt><dd>${fmtDate(t.startedAt)}</dd>
      <dt>Executed</dt><dd>${fmtDate(t.executedAt)}${t.elapsedMilliseconds != null ? ` · ${fmtMs(t.elapsedMilliseconds)}` : ''}</dd>
      <dt>Attempts</dt><dd>${t.retryCount + 1} of ${t.retries + 1}${t.retryIntervals && t.retryIntervals.length ? ` <span class="muted small">(intervals ${esc(t.retryIntervals.join(', '))}s)</span>` : ''}</dd>
      <dt>Timeout</dt><dd>${t.timeoutSeconds == null ? '<span class="faint">scheduler default</span>' : t.timeoutSeconds <= 0 ? 'disabled' : t.timeoutSeconds + ' s'}</dd>
      <dt>Priority</dt><dd>${esc(t.priority)}</dd>
      ${t.skipAfterSeconds != null ? `<dt>Skip after</dt><dd>${t.skipAfterSeconds} s late</dd>` : ''}
      <dt>Stale policy</dt><dd>${esc(t.onStale)}${t.staleRestartCount ? ` · restarted ${t.staleRestartCount}×` : ''}</dd>
      ${t.lockHolder ? `<dt>Owner</dt><dd class="mono small">${esc(t.lockHolder)}<div class="faint">lease until ${fmtDate(t.leaseUntil)}</div></dd>` : ''}
      <dt>Generation</dt><dd>${t.generation}</dd>
      <dt>Created</dt><dd>${fmtDate(t.createdAt)}</dd>
    </dl>`;
  }
  function progressBar(t) {
    if (t.status !== 'InProgress') return '';
    const p = t.progressPercent == null ? null : t.progressPercent;
    return `<div class="section"><h3>Progress</h3><div class="progress"><div style="width:${p == null ? 0 : p}%"></div></div><div class="small muted" style="margin-top:4px">${p == null ? 'running…' : p + '%'}${t.progressMessage ? ' · ' + esc(t.progressMessage) : ''}${t.progressUpdatedAt ? ' · ' + fmtDate(t.progressUpdatedAt) : ''}</div></div>`;
  }
  function outcomePanels(t, parentResult, parentFunction) {
    return `
      <div class="card"><h3>Request</h3>${pretty(t.request)}</div>
      ${parentFunction ? `<div class="card"><h3>Parent input (${esc(parentFunction)} result)</h3>${parentResult != null ? pretty(parentResult) : '<span class="faint">parent published no result</span>'}</div>` : ''}
      ${t.hasResult ? `<div class="card"><h3>Result</h3>${pretty(t.result)}</div>` : ''}
      ${t.skipReason ? `<div class="card"><h3>Skip reason</h3><div class="err-box" style="background:var(--warn-soft);color:var(--warn)">${esc(t.skipReason)}</div></div>` : ''}
      ${t.exception ? `<div class="card"><h3>Error${t.exceptionType ? ` <span class="mono muted">${esc(t.exceptionType)}</span>` : ''}</h3><pre>${esc(t.exception)}</pre></div>` : ''}`;
  }
  function bindCopy(root) {
    root.addEventListener('click', e => { const c = e.target.getAttribute && e.target.getAttribute('data-copy'); if (c && navigator.clipboard) { navigator.clipboard.writeText(c).then(() => toast('Copied')); } });
  }

  async function viewTimeTickerDetail(content, id) {
    let liveLogs = false;
    async function render() {
      const d = await api(`/time-tickers/${id}`);
      const t = d.ticker;
      const pending = t.status === 'Idle' || t.status === 'Queued';
      const terminal = TERMINAL.includes(t.status);
      const inChain = !!(t.chainRootId || t.parentId);
      const actions = readOnly() ? '' : `
        ${!t.parentId && t.status !== 'InProgress' && t.status !== 'Queued' ? '<button class="btn primary" data-act="run-now">Run now</button>' : ''}
        ${inChain && terminal ? '<button class="btn" data-act="retry-from">Retry from here</button>' : ''}
        ${pending ? '<button class="btn" data-act="cancel">Cancel</button>' : ''}
        ${t.status === 'InProgress' || t.status === 'Queued' ? '<button class="btn danger" data-act="request-cancellation">Request cancellation</button>' : ''}
        ${!t.parentId && t.status === 'Idle' ? '<button class="btn" data-act="reschedule">Reschedule</button>' : ''}
        ${!t.parentId && t.status === 'Idle' ? '<button class="btn" data-act="edit">Edit</button>' : ''}
        ${t.status !== 'InProgress' && t.status !== 'Queued' ? '<button class="btn danger" data-act="delete">Delete</button>' : ''}`;
      content.innerHTML = `
        <div class="page-head"><div><div class="small muted"><a href="#/time-tickers">Time tickers</a> / ${esc(t.function)}</div><h1>${esc(t.function)} ${badge(t.status)}</h1>
          ${inChain ? `<div class="small"><a href="#/chains/${t.chainRootId || t.id}/${t.id}">⛓ Open chain view</a>${t.parentId ? ` · parent <a href="#/time-tickers/${t.parentId}" class="mono">${esc(t.parentId.slice(0, 8))}…</a>` : ''}</div>` : ''}
          ${t.cronOccurrenceId ? `<div class="small">Launched by cron occurrence <a href="#/occurrences/${t.cronOccurrenceId}" class="mono">${esc(t.cronOccurrenceId.slice(0, 8))}…</a></div>` : ''}</div>
          <span class="spacer"></span><div class="actions" id="actions">${actions}</div></div>
        <div class="grid cols-2">
          <div><div class="card">${execFields(t)}${progressBar(t)}${t.schedulePolicyName ? `<div class="section small muted">Schedule policy <b>${esc(t.schedulePolicyName)}</b> rev ${t.schedulePolicyRevision || '?'} · when blocked: ${esc(t.whenBlocked)}</div>` : ''}</div><div class="card" id="logs" style="margin-top:14px"><div class="loading">Loading logs…</div></div></div>
          <div>${outcomePanels(t, d.parentResult, d.parentFunction)}</div>
        </div>`;
      bindCopy(content);
      $('#actions', content).addEventListener('click', async e => {
        const act = e.target.getAttribute && e.target.getAttribute('data-act');
        if (!act) return;
        if (act === 'run-now') await mutate(`/time-tickers/${id}/run-now`, {}, 'Run requested');
        if (act === 'retry-from') { if (await confirmDialog('Retry this step and its downstream path? Completed ancestors are not re-run.', 'Retry')) await mutate(`/time-tickers/${id}/retry-from`, {}, 'Retry started'); }
        if (act === 'cancel') { if (await confirmDialog('Cancel this pending job (and its pending children)?', 'Cancel job')) await mutate(`/time-tickers/${id}/cancel`, {}, 'Cancelled'); }
        if (act === 'request-cancellation') { if (await confirmDialog('Signal the running execution to cancel? The handler must cooperate with its cancellation token.', 'Request')) await mutate(`/time-tickers/${id}/request-cancellation`, {}, 'Cancellation requested'); }
        if (act === 'reschedule') { const d2 = await promptDialog('Reschedule', `<div class="field"><label>New execution time (local)</label><input type="datetime-local" name="when" value="${toLocalInput(t.executionTime)}"></div>`, 'Reschedule'); if (d2 && d2.when) await mutate(`/time-tickers/${id}/reschedule`, { body: { executionTime: fromLocalInput(d2.when) } }, 'Rescheduled'); }
        if (act === 'edit') openJobForm(t);
        if (act === 'delete') { if (await confirmDialog('Permanently delete this job' + (inChain ? ' and its descendants' : '') + '?', 'Delete')) { const r = await mutate(`/time-tickers/${id}`, { method: 'DELETE' }, 'Deleted'); if (r) location.hash = '#/time-tickers'; } }
      });
      try { liveLogs = await logPanel($('#logs', content), id); } catch (err) { $('#logs', content).innerHTML = `<div class="err-box">${esc(err.message)}</div>`; }
      if (state.view) state.view.interval = liveLogs || t.status === 'InProgress' || t.status === 'Queued' ? 2000 : 5000;
    }
    await render();
    setView(render, 5000);
  }

  // ------------------------------------------------------------ views: occurrence detail
  async function viewOccurrenceDetail(content, id) {
    async function render() {
      const d = await api(`/occurrences/${id}`);
      const o = d.occurrence, cron = d.cron;
      const pending = o.status === 'Idle' || o.status === 'Queued';
      const terminal = TERMINAL.includes(o.status);
      const actions = readOnly() ? '' : `
        ${terminal ? '<button class="btn primary" data-act="rerun">Re-run</button>' : ''}
        ${pending ? '<button class="btn" data-act="cancel">Cancel</button>' : ''}
        ${o.status === 'InProgress' || o.status === 'Queued' ? '<button class="btn danger" data-act="request-cancellation">Request cancellation</button>' : ''}
        ${o.status !== 'InProgress' && o.status !== 'Queued' ? '<button class="btn danger" data-act="delete">Delete</button>' : ''}`;
      content.innerHTML = `
        <div class="page-head"><div><div class="small muted"><a href="#/cron-tickers">Cron tickers</a> / ${cron ? `<a href="#/cron-tickers/${cron.id}">${esc(cron.function)}</a>` : esc(o.function)} / occurrence</div>
          <h1>${esc(o.function)} ${badge(o.status)} ${o.isManual ? '<span class="badge accent">manual</span>' : ''}</h1>
          ${cron ? `<div class="small muted">Cron <code>${esc(cron.expression)}</code> · definition v${o.definitionVersion}</div>` : ''}
          ${o.chainRootId ? `<div class="small"><a href="#/chains/${o.chainRootId}">⛓ Open launched chain</a></div>` : ''}</div>
          <span class="spacer"></span><div class="actions" id="actions">${actions}</div></div>
        <div class="grid cols-2">
          <div><div class="card">${execFields(o)}${progressBar(o)}</div><div class="card" id="logs" style="margin-top:14px"><div class="loading">Loading logs…</div></div></div>
          <div>${outcomePanels(o, null, null)}</div>
        </div>`;
      bindCopy(content);
      $('#actions', content).addEventListener('click', async e => {
        const act = e.target.getAttribute && e.target.getAttribute('data-act');
        if (!act) return;
        if (act === 'rerun') { if (await confirmDialog('Re-run this occurrence in place (same id)?', 'Re-run')) await mutate(`/occurrences/${id}/rerun`, {}, 'Re-run requested'); }
        if (act === 'cancel') { if (await confirmDialog('Cancel this pending occurrence?', 'Cancel occurrence')) await mutate(`/occurrences/${id}/cancel`, {}, 'Cancelled'); }
        if (act === 'request-cancellation') { if (await confirmDialog('Signal the running execution to cancel?', 'Request')) await mutate(`/occurrences/${id}/request-cancellation`, {}, 'Cancellation requested'); }
        if (act === 'delete') { if (await confirmDialog('Delete this occurrence? A launched chain is kept.', 'Delete')) { const r = await mutate(`/occurrences/${id}`, { method: 'DELETE' }, 'Deleted'); if (r) location.hash = cron ? `#/cron-tickers/${cron.id}` : '#/cron-tickers'; } }
      });
      let live = false;
      try { live = await logPanel($('#logs', content), id); } catch (err) { $('#logs', content).innerHTML = `<div class="err-box">${esc(err.message)}</div>`; }
      if (state.view) state.view.interval = live || !terminal ? 2000 : 5000;
    }
    await render();
    setView(render, 5000);
  }

  // ------------------------------------------------------------ views: chain
  async function viewChain(content, rootId, selectedId) {
    let selected = selectedId || null;
    let data = null;
    content.innerHTML = `
      <div class="page-head"><div><div class="small muted"><a href="#/time-tickers">Time tickers</a> / chain</div><h1 id="chain-title">Chain</h1><div id="chain-sub" class="small muted"></div></div><span class="spacer"></span><div class="actions" id="chain-actions"></div></div>
      <div class="chain-layout"><div class="card chain-canvas" id="canvas"></div><div class="card chain-side" id="side"><div class="empty">Select a job to see its details.</div></div></div>`;
    const canvas = $('#canvas', content), side = $('#side', content);

    function nodeCard(n) {
      const cls = n.status === 'InProgress' ? 'running' : n.status === 'Failed' ? 'failed' : '';
      return `<div class="cnode-card ${cls} ${n.id === selected ? 'selected' : ''}" data-id="${n.id}">
        <div class="fn">${esc(n.function)}</div>${n.description ? `<div class="desc">${esc(n.description)}</div>` : ''}
        <div class="meta">${badge(n.status)}${n.elapsedMilliseconds != null ? `<span>${fmtMs(n.elapsedMilliseconds)}</span>` : ''}${n.retryCount ? `<span>retry ${n.retryCount}/${n.retries}</span>` : ''}${n.status === 'InProgress' && n.progressPercent != null ? `<span>${n.progressPercent}%</span>` : ''}</div>
      </div>`;
    }
    function edge(label, cls) { return `<div class="cedge ${cls || ''}">${label ? `<span class="lbl">${esc(label)}</span>` : ''}</div>`; }
    function renderNode(n, byParent, isRoot) {
      const kids = byParent[n.id] || [];
      if (n.isGroup) {
        const branches = kids.filter(k => k.joinKind === 'None');
        const joins = kids.filter(k => k.joinKind !== 'None');
        return `<div class="cnode">${isRoot ? '' : edge('group', '')}<div class="cgroup ${n.id === selected ? 'selected' : ''}" data-id="${n.id}"><div class="ghead">⫶ Parallel group ${badge(n.status)}</div>
          <div class="cchildren">${branches.map(b => renderNode(b, byParent, true)).join('')}</div></div>
          ${joins.length ? `<div class="cjoins">${joins.map(j => `<div class="cnode cjoin">${edge(j.joinKind.replace('After', 'after ').replace(/([A-Z])/g, ' $1').toLowerCase(), '')}${renderNode(j, byParent, true)}</div>`).join('')}</div>` : ''}</div>`;
      }
      return `<div class="cnode">${isRoot ? '' : edge(n.runCondition === 'OnSuccess' ? '' : n.runCondition.replace(/([A-Z])/g, ' $1').trim().toLowerCase(), 'cond-' + n.runCondition)}${nodeCard(n)}
        ${kids.length ? `<div class="cchildren">${kids.map(k => renderNode(k, byParent, false)).join('')}</div>` : ''}</div>`;
    }
    function renderSide() {
      const n = data.nodes.find(x => x.id === selected);
      if (!n) { side.innerHTML = '<div class="empty">Select a job to see its details.</div>'; return; }
      const settled = data.nodes.every(x => TERMINAL.includes(x.status));
      const parent = n.parentId ? data.nodes.find(x => x.id === n.parentId) : null;
      let upstream = parent;
      while (upstream && upstream.isGroup) upstream = upstream.parentId ? data.nodes.find(x => x.id === upstream.parentId) : null;
      side.innerHTML = `
        <h2>${n.isGroup ? 'Parallel group' : esc(n.function)} ${badge(n.status)}</h2>
        ${n.description ? `<div class="muted small">${esc(n.description)}</div>` : ''}
        <div class="actions" style="margin:10px 0" id="side-actions">
          ${n.isGroup ? '' : `<a class="btn sm" href="#/time-tickers/${n.id}">Open job</a>`}
          ${!readOnly() && settled ? `<button class="btn sm primary" data-act="retry">${n.isGroup ? 'Retry this group' : 'Retry from this job'}</button>` : ''}
          ${!readOnly() && !n.isGroup && n.status === 'InProgress' ? '<button class="btn sm danger" data-act="cancel-running">Request cancellation</button>' : ''}
        </div>
        <dl class="kv small">
          <dt>Id</dt><dd class="mono">${esc(n.id)}</dd>
          ${n.isGroup ? '' : `<dt>Condition</dt><dd>${esc(n.runCondition)}${n.joinKind !== 'None' ? ' · join ' + esc(n.joinKind) : ''}</dd>`}
          <dt>Scheduled</dt><dd>${fmtDate(n.executionTime)}</dd><dt>Started</dt><dd>${fmtDate(n.startedAt)}</dd><dt>Executed</dt><dd>${fmtDate(n.executedAt)}${n.elapsedMilliseconds != null ? ' · ' + fmtMs(n.elapsedMilliseconds) : ''}</dd>
          <dt>Attempts</dt><dd>${n.retryCount + 1}/${n.retries + 1}</dd><dt>Generation</dt><dd>${n.generation}</dd>${n.lockHolder ? `<dt>Owner</dt><dd class="mono">${esc(n.lockHolder)}</dd>` : ''}
        </dl>
        ${n.status === 'InProgress' && n.progressPercent != null ? `<div class="progress" style="margin:8px 0"><div style="width:${n.progressPercent}%"></div></div><div class="small muted">${n.progressPercent}% ${esc(n.progressMessage || '')}</div>` : ''}
        ${n.isGroup ? '' : `<div class="section"><h3>Request</h3>${pretty(n.request)}</div>`}
        ${upstream ? `<div class="section"><h3>Parent input (${esc(upstream.function)})</h3>${upstream.hasResult ? pretty(upstream.result) : '<span class="faint">no result published</span>'}</div>` : ''}
        ${n.hasResult ? `<div class="section"><h3>Result</h3>${pretty(n.result)}</div>` : ''}
        ${n.skipReason ? `<div class="section"><h3>Skip reason</h3><div class="err-box" style="background:var(--warn-soft);color:var(--warn)">${esc(n.skipReason)}</div></div>` : ''}
        ${n.exception ? `<div class="section"><h3>Error</h3><pre>${esc(n.exception)}</pre></div>` : ''}
        ${n.isGroup ? '' : `<div class="section" id="side-logs"><h3>Logs</h3><div class="loading small">Loading…</div></div>`}`;
      $('#side-actions', side).addEventListener('click', async e => {
        const act = e.target.getAttribute && e.target.getAttribute('data-act');
        if (act === 'retry') { if (await confirmDialog(n.isGroup ? 'Re-run every branch of this group and re-evaluate its outcomes?' : 'Retry this step and its downstream path under a new generation?', 'Retry')) await mutate(`/time-tickers/${n.id}/retry-from`, {}, 'Retry started'); }
        if (act === 'cancel-running') await mutate(`/time-tickers/${n.id}/request-cancellation`, {}, 'Cancellation requested');
      });
      if (!n.isGroup) logPanel($('#side-logs', side), n.id).catch(err => { $('#side-logs', side).innerHTML = `<div class="err-box">${esc(err.message)}</div>`; });
    }
    async function render() {
      data = await api(`/time-tickers/${rootId}/chain`);
      const byParent = {};
      data.nodes.forEach(n => { if (n.parentId) (byParent[n.parentId] = byParent[n.parentId] || []).push(n); });
      const root = data.nodes.find(n => !n.parentId) || data.nodes[0];
      const s = data.summary;
      $('#chain-title', content).innerHTML = `${esc(root ? root.function : 'Chain')} ${chainBadge(s)}`;
      $('#chain-sub', content).innerHTML = `${s.total} job(s) · ${s.finished} finished · ${s.failed} failed · ${s.cancelled} cancelled · ${s.skipped} skipped · ${s.running} running${root && root.description ? ' · ' + esc(root.description) : ''}`;
      const settled = data.nodes.every(x => TERMINAL.includes(x.status));
      $('#chain-actions', content).innerHTML = readOnly() ? '' : `${settled && root ? `<button class="btn primary" data-act="rerun-root">Run chain again</button>` : ''}${root && root.status === 'Idle' ? `<button class="btn" data-act="cancel-root">Cancel chain</button>` : ''}`;
      canvas.innerHTML = root ? renderNode(root, byParent, true) : '<div class="empty">Chain unavailable</div>';
      renderSide();
    }
    canvas.addEventListener('click', e => {
      const card = e.target.closest('[data-id]');
      if (!card) return;
      e.stopPropagation();
      selected = card.getAttribute('data-id');
      history.replaceState(null, '', `#/chains/${rootId}/${selected}`);
      $$('[data-id]', canvas).forEach(c => c.classList.toggle('selected', c.getAttribute('data-id') === selected));
      renderSide();
    });
    $('#chain-actions', content).addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act');
      if (act === 'rerun-root') { if (await confirmDialog('Run the whole chain again from the root?', 'Run')) await mutate(`/time-tickers/${rootId}/run-now`, {}, 'Chain restarted'); }
      if (act === 'cancel-root') { if (await confirmDialog('Cancel the pending chain?', 'Cancel chain')) await mutate(`/time-tickers/${rootId}/cancel`, {}, 'Chain cancelled'); }
    });
    await render();
    setView(render, 2000);
  }

  // ------------------------------------------------------------ views: cron tickers
  async function viewCronTickers(content) {
    content.innerHTML = `
      <div class="page-head"><div><h1>Cron tickers</h1><div class="muted small">Recurring definitions. Each run creates an occurrence.</div></div><span class="spacer"></span>
        <div class="actions">${wbtn('<button class="btn primary" id="create">+ Create cron</button>')}</div></div>
      <div class="card"><div class="filters"><input type="search" id="f-fn" placeholder="Search function…" style="min-width:220px"><label class="small muted"><input type="checkbox" id="f-retired"> show retired</label></div><div class="table-wrap" id="tbl"></div></div>`;
    const tbl = $('#tbl', content);
    let search = '', showRetired = false;
    async function render() {
      let rows = await api('/cron-tickers');
      if (search) rows = rows.filter(r => r.function.toLowerCase().includes(search) || (r.description || '').toLowerCase().includes(search));
      if (!showRetired) rows = rows.filter(r => !r.retired);
      tbl.innerHTML = rows.length ? `<table><thead><tr><th>Function</th><th>Schedule</th><th>Enabled</th><th>Next run</th><th>Last run</th><th>Options</th><th></th></tr></thead><tbody>${rows.map(r => `
        <tr class="clickable" data-href="#/cron-tickers/${r.id}"><td><div>${esc(r.function)} ${r.isChain ? '<span class="badge purple">⛓ Chain</span>' : ''}${r.fromCode ? ' <span class="badge accent" title="Seeded from code (' + esc(r.seedKey) + '); dashboard edits are overwritten at startup">From code</span>' : ''}${r.retired ? ' <span class="badge warn">retired</span>' : ''}</div>${r.description ? `<div class="small muted">${esc(r.description)}</div>` : ''}</td>
          <td><code>${esc(r.expression)}</code><div class="small muted">${esc(r.readable)}${r.timeZoneId ? ' · ' + esc(r.timeZoneId) : ''}</div></td>
          <td><label class="toggle" title="${r.enabled ? 'Disable' : 'Enable'}"><input type="checkbox" data-act="toggle" data-id="${r.id}" ${r.enabled ? 'checked' : ''} ${readOnly() || r.retired ? 'disabled' : ''}><span class="knob"></span></label></td>
          <td>${r.enabled ? fmtDate(r.nextRunAt) + ` <div class="small faint">${esc(fmtRel(r.nextRunAt))}</div>` : '<span class="faint">paused</span>'}</td>
          <td>${r.lastStatus ? badge(r.lastStatus) : '<span class="faint">never</span>'}<div class="small muted">${r.lastRunAt ? fmtDate(r.lastRunAt) : ''}</div></td>
          <td class="small">${r.skipIfAlreadyRunning ? tag('skip if running') : ''} ${r.schedulePolicyName ? tag('policy: ' + r.schedulePolicyName) : ''} ${r.skipAfterSeconds != null ? tag('skip after ' + r.skipAfterSeconds + 's') : ''} ${r.catchUpMode === 'RunLatest' ? tag('catch-up latest') : ''} ${r.retries ? tag(r.retries + ' retries') : ''}</td>
          <td><div class="actions">${wbtn(`<button class="btn sm" data-act="run" data-id="${r.id}">Run now</button><button class="btn sm" data-act="edit" data-id="${r.id}">Edit</button><button class="btn sm danger" data-act="delete" data-id="${r.id}">Delete</button>`)}</div></td></tr>`).join('')}</tbody></table>` : '<div class="empty">No cron tickers yet.</div>';
    }
    tbl.addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act');
      const id = e.target.getAttribute && e.target.getAttribute('data-id');
      if (act === 'toggle') { e.stopPropagation(); await mutate(`/cron-tickers/${id}/enabled`, { body: { enabled: e.target.checked } }, e.target.checked ? 'Enabled' : 'Disabled'); return; }
      if (act === 'run') { e.stopPropagation(); await mutate(`/cron-tickers/${id}/run-now`, {}, 'Occurrence created'); return; }
      if (act === 'edit') { e.stopPropagation(); const d = await api(`/cron-tickers/${id}`); openCronForm(d.cron); return; }
      if (act === 'delete') { e.stopPropagation(); if (await confirmDialog('Delete this cron definition and all its occurrences?', 'Delete')) await mutate(`/cron-tickers/${id}`, { method: 'DELETE' }, 'Deleted'); return; }
      if (e.target.closest('label.toggle')) { e.stopPropagation(); return; }
      const tr = e.target.closest('tr[data-href]'); if (tr) location.hash = tr.getAttribute('data-href');
    });
    let deb;
    $('#f-fn', content).addEventListener('input', e => { search = e.target.value.trim().toLowerCase(); clearTimeout(deb); deb = setTimeout(render, 200); });
    $('#f-retired', content).addEventListener('change', e => { showRetired = e.target.checked; render(); });
    const create = $('#create', content); if (create) create.addEventListener('click', () => openCronForm(null));
    await render();
    setView(render, 5000);
  }

  async function openCronForm(existing) {
    await loadFunctions(); await loadPolicies(); await loadTimezones();
    const e = existing || {};
    const chainChildren = (e.chain && e.chain.children) || (e.chainTemplateJson ? (JSON.parse(e.chainTemplateJson).children || []) : []);
    const linear = [];
    (function walk(list) { if (list && list.length) { linear.push(list[0]); walk(list[0].children); } })(chainChildren);
    const m = modal({
      title: existing ? 'Edit cron ticker' : 'Create cron ticker', wide: true,
      body: `<form class="form" id="cron-form">
        <div class="row">
          <div class="field"><label>Function</label><select name="function">${options(state.functions, e.function || (state.functions[0] && state.functions[0].name), f => f.name + (f.requestType ? ' (' + f.requestType + ')' : ''), f => f.name)}</select></div>
          <div class="field"><label>Cron expression (5 or 6 parts, seconds first)</label><input name="expression" value="${esc(e.expression || '')}" placeholder="0 9 * * 1-5" class="mono"><span class="help" id="cron-help">—</span></div>
        </div>
        <div class="row">
          <div class="field"><label>Time zone</label><select name="timeZoneId"><option value="">scheduler default</option>${options(state.timezones, e.timeZoneId || '')}</select></div>
          <div class="field"><label>Description</label><input name="description" value="${esc(e.description || '')}"></div>
        </div>
        <div class="field"><label>Request (JSON)</label><textarea name="request">${esc(e.request || '')}</textarea></div>
        <div class="row cols-3">
          <div class="field"><label>Retries</label><input type="number" name="retries" min="0" value="${e.retries || 0}"></div>
          <div class="field"><label>Retry intervals (seconds)</label><input name="retryIntervals" value="${esc((e.retryIntervals || []).join(', '))}" placeholder="10, 30"></div>
          <div class="field"><label>Timeout (seconds)</label><input type="number" name="timeoutSeconds" min="0" value="${e.timeoutSeconds != null ? e.timeoutSeconds : ''}" placeholder="scheduler default"></div>
        </div>
        <div class="row cols-3">
          <div class="field check"><input type="checkbox" name="enabled" id="c-enabled" ${e.enabled !== false ? 'checked' : ''}><label for="c-enabled">Enabled</label></div>
          <div class="field check"><input type="checkbox" name="skipIfAlreadyRunning" id="c-skip" ${e.skipIfAlreadyRunning ? 'checked' : ''}><label for="c-skip">Skip if already running</label></div>
          <div class="field"><label>Priority</label><select name="priority">${enumOpts(PRIORITIES, e.priority)}</select></div>
        </div>
        <details class="adv"><summary>Advanced settings</summary><div class="form">
          <div class="row cols-3">
            <div class="field"><label>Schedule policy</label><select name="schedulePolicyName"><option value="">none</option>${options(state.policies.map(p => p.policy.name), e.schedulePolicyName || '')}</select></div>
            <div class="field"><label>Skip after (seconds late)</label><input type="number" name="skipAfterSeconds" min="1" value="${e.skipAfterSeconds != null ? e.skipAfterSeconds : ''}" placeholder="run whenever"></div>
            <div class="field"><label>Catch-up (missed slots while down)</label><select name="catchUpMode">${enumOpts(CATCHUP, e.catchUpMode)}</select><span class="help">RunLatest requires a skip-after window.</span></div>
          </div>
          <div class="row"><div class="field"><label>On stale lease</label><select name="onStale">${enumOpts(STALE, e.onStale)}</select></div><div class="field"><label>Max stale restarts</label><input type="number" name="maxStaleRestarts" min="0" value="${e.maxStaleRestarts != null ? e.maxStaleRestarts : ''}" placeholder="scheduler default"></div></div>
        </div></details>
        <fieldset><legend>Recurring chain steps (optional, after the root function)</legend><div class="steps" id="steps">${linear.map(stepRow).join('')}</div><div style="margin-top:8px"><button type="button" class="btn sm" data-act="add-step">+ Add step</button></div></fieldset>
      </form>`,
      footer: `<button type="button" class="btn" data-act="cancel">Cancel</button><button type="button" class="btn primary" data-act="save">${existing ? 'Save' : 'Create'}</button>`,
    });
    const form = $('#cron-form', m.el);
    const exprEl = $('[name=expression]', form), help = $('#cron-help', form);
    let deb;
    async function describe() {
      const v = exprEl.value.trim();
      if (!v) { help.textContent = '—'; return; }
      try { const r = await api('/cron/describe?expression=' + encodeURIComponent(v)); help.textContent = r.readable; exprEl.classList.toggle('invalid', !r.valid); } catch (err) { help.textContent = err.message; }
    }
    exprEl.addEventListener('input', () => { clearTimeout(deb); deb = setTimeout(describe, 250); });
    describe();
    m.el.addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act');
      if (!act) return;
      if (act === 'cancel') m.close();
      if (act === 'add-step') $('#steps', form).insertAdjacentHTML('beforeend', stepRow());
      if (act === 'remove-step') e.target.closest('.step').remove();
      if (act === 'save') {
        const d = formData(form);
        if (!validJson(d.request || '')) { toast('Request is not valid JSON.', 'error'); return; }
        const steps = readSteps($('#steps', form));
        for (const s of steps) if (!validJson(s.request || '')) { toast('A chain step request is not valid JSON.', 'error'); return; }
        let chain = null;
        if (steps.length) { chain = { children: [] }; let cursor = chain; for (const s of steps) { const node = Object.assign({}, s, { children: [] }); cursor.children = [node]; cursor = node; } }
        const body = {
          function: d.function, expression: d.expression || '', description: d.description, request: d.request, retries: d.retries || 0,
          retryIntervals: (d.retryIntervals || '').split(',').map(s => s.trim()).filter(Boolean).map(Number).filter(n => !isNaN(n)),
          timeoutSeconds: d.timeoutSeconds, priority: d.priority, enabled: !!d.enabled, skipIfAlreadyRunning: !!d.skipIfAlreadyRunning, timeZoneId: d.timeZoneId,
          schedulePolicyName: d.schedulePolicyName, skipAfterSeconds: d.skipAfterSeconds, catchUpMode: d.catchUpMode, onStale: d.onStale, maxStaleRestarts: d.maxStaleRestarts, chain,
        };
        const r = existing ? await mutate(`/cron-tickers/${existing.id}`, { method: 'PUT', body }, 'Cron updated') : await mutate('/cron-tickers', { body }, 'Cron created');
        if (r) { m.close(); if (!existing && r.result && r.result.id) location.hash = `#/cron-tickers/${r.result.id}`; }
      }
    });
  }

  async function viewCronDetail(content, id) {
    async function render() {
      const d = await api(`/cron-tickers/${id}`);
      const c = d.cron;
      const occ = await api(`/cron-tickers/${id}/occurrences?take=100`);
      content.innerHTML = `
        <div class="page-head"><div><div class="small muted"><a href="#/cron-tickers">Cron tickers</a> / ${esc(c.function)}</div>
          <h1>${esc(c.function)} ${c.enabled ? '<span class="badge ok">enabled</span>' : '<span class="badge warn">paused</span>'} ${c.chainTemplateJson ? '<span class="badge purple">⛓ Chain</span>' : ''} ${c.seedKey ? '<span class="badge accent">From code</span>' : ''} ${c.retired ? '<span class="badge warn">retired</span>' : ''}</h1>
          <div class="muted small"><code>${esc(c.expression)}</code> · ${esc(d.readable)}${c.timeZoneId ? ' · ' + esc(c.timeZoneId) : ''}${c.description ? ' · ' + esc(c.description) : ''}</div></div>
          <span class="spacer"></span><div class="actions" id="actions">${wbtn(`<button class="btn primary" data-act="run">Run now</button><button class="btn" data-act="toggle">${c.enabled ? 'Disable' : 'Enable'}</button><button class="btn" data-act="edit">Edit</button><button class="btn danger" data-act="delete">Delete</button>`)}</div></div>
        <div class="grid cols-3">
          <div class="card"><h3>Definition</h3><dl class="kv small">
            <dt>Id</dt><dd class="mono">${esc(c.id)}</dd><dt>Version</dt><dd>${c.definitionVersion}</dd>
            <dt>Retries</dt><dd>${c.retries}${c.retryIntervals && c.retryIntervals.length ? ' (' + esc(c.retryIntervals.join(', ')) + 's)' : ''}</dd>
            <dt>Timeout</dt><dd>${c.timeoutSeconds == null ? 'scheduler default' : c.timeoutSeconds + ' s'}</dd><dt>Priority</dt><dd>${esc(c.priority)}</dd>
            <dt>Overlap</dt><dd>${c.skipIfAlreadyRunning ? 'skip if already running' : 'allowed'}</dd>
            <dt>Policy</dt><dd>${c.schedulePolicyName ? esc(c.schedulePolicyName) : '<span class="faint">none</span>'}</dd>
            <dt>Late start</dt><dd>${c.skipAfterSeconds != null ? 'skip after ' + c.skipAfterSeconds + ' s' : 'run when available'}</dd>
            <dt>Catch-up</dt><dd>${esc(c.catchUpMode)}</dd><dt>Stale</dt><dd>${esc(c.onStale)}</dd>
            ${c.seedKey ? `<dt>Seed key</dt><dd class="mono">${esc(c.seedKey)}</dd>` : ''}
            <dt>Last run</dt><dd>${c.lastStatus ? badge(c.lastStatus) : '<span class="faint">never</span>'} ${c.lastRunAt ? fmtDate(c.lastRunAt) : ''}</dd>
            <dt>Created</dt><dd>${fmtDate(c.createdAt)}</dd><dt>Updated</dt><dd>${fmtDate(c.updatedAt)}</dd></dl></div>
          <div class="card"><h3>Next occurrences</h3>${d.next && d.next.length ? `<ul class="mini-list">${d.next.map(n => `<li><span>${fmtDate(n)}</span><span class="small faint">${esc(fmtRel(n))}</span></li>`).join('')}</ul>` : '<div class="empty small">No upcoming occurrences</div>'}</div>
          <div class="card"><h3>Request</h3>${pretty(c.request)}${d.chain ? `<h3 style="margin-top:12px">Chain template</h3><pre class="json">${esc(describeTemplate(d.chain.children, 0))}</pre>` : ''}</div>
        </div>
        <div class="card section"><h2>Occurrence history</h2><div class="table-wrap">${occ.length ? `<table><thead><tr><th>Status</th><th>Scheduled</th><th>Executed</th><th>Duration</th><th>Attempts</th><th>Chain</th><th></th></tr></thead><tbody>${occ.map(({ occurrence: o, chainSummary }) => `
          <tr class="clickable" data-href="#/occurrences/${o.id}"><td>${badge(o.status)} ${o.isManual ? '<span class="badge accent">manual</span>' : ''}${o.skipReason ? `<div class="small muted">${esc(o.skipReason)}</div>` : ''}</td>
            <td>${fmtDate(o.executionTime)}</td><td>${fmtDate(o.executedAt)}</td><td>${fmtMs(o.elapsedMilliseconds)}</td><td>${o.retryCount + 1}/${o.retries + 1}</td>
            <td>${o.chainRootId ? chainBadge(chainSummary || { label: 'Chain', total: '?', finished: '?' }, o.chainRootId) : '<span class="faint">—</span>'}</td>
            <td><div class="actions">${wbtn(`${TERMINAL.includes(o.status) ? `<button class="btn sm" data-act="rerun" data-id="${o.id}">Re-run</button>` : ''}${o.status === 'Idle' || o.status === 'Queued' ? `<button class="btn sm" data-act="cancel" data-id="${o.id}">Cancel</button>` : ''}${o.status === 'InProgress' ? `<button class="btn sm danger" data-act="reqcancel" data-id="${o.id}">Request cancellation</button>` : ''}${o.status !== 'InProgress' && o.status !== 'Queued' ? `<button class="btn sm danger" data-act="delocc" data-id="${o.id}">Delete</button>` : ''}`)}</div></td></tr>`).join('')}</tbody></table>` : '<div class="empty">No occurrences yet.</div>'}</div></div>`;
      content.addEventListener('click', async e => {
        const act = e.target.getAttribute && e.target.getAttribute('data-act');
        const oid = e.target.getAttribute && e.target.getAttribute('data-id');
        if (act) e.stopPropagation();
        if (act === 'run') await mutate(`/cron-tickers/${id}/run-now`, {}, 'Occurrence created');
        if (act === 'toggle') await mutate(`/cron-tickers/${id}/enabled`, { body: { enabled: !c.enabled } }, c.enabled ? 'Disabled' : 'Enabled');
        if (act === 'edit') openCronForm(c);
        if (act === 'delete') { if (await confirmDialog('Delete this cron definition and all its occurrences?', 'Delete')) { const r = await mutate(`/cron-tickers/${id}`, { method: 'DELETE' }, 'Deleted'); if (r) location.hash = '#/cron-tickers'; } }
        if (act === 'rerun') { if (await confirmDialog('Re-run this occurrence in place?', 'Re-run')) await mutate(`/occurrences/${oid}/rerun`, {}, 'Re-run requested'); }
        if (act === 'cancel') await mutate(`/occurrences/${oid}/cancel`, {}, 'Cancelled');
        if (act === 'reqcancel') await mutate(`/occurrences/${oid}/request-cancellation`, {}, 'Cancellation requested');
        if (act === 'delocc') { if (await confirmDialog('Delete this occurrence?', 'Delete')) await mutate(`/occurrences/${oid}`, { method: 'DELETE' }, 'Deleted'); }
        if (!act) { const tr = e.target.closest('tr[data-href]'); if (tr && !e.target.closest('a')) location.hash = tr.getAttribute('data-href'); }
      }, { once: true });
    }
    await render();
    setView(render, 5000);
  }
  function describeTemplate(children, depth) {
    return (children || []).map(n => '  '.repeat(depth) + (n.isGroup ? '[group]' : n.function) + (n.joinKind && n.joinKind !== 'None' ? ' (' + n.joinKind + ')' : n.runCondition && n.runCondition !== 'OnSuccess' ? ' (' + n.runCondition + ')' : '') + (n.retries ? ' retries=' + n.retries : '') + '\n' + describeTemplate(n.children, depth + 1)).join('');
  }

  // ------------------------------------------------------------ views: executions
  async function viewExecutions(content) {
    const filters = { status: '', function: '' };
    content.innerHTML = `<div class="page-head"><div><h1>Executions</h1><div class="muted small">History across time tickers and cron occurrences</div></div></div>
      <div class="card"><div class="filters"><input type="search" id="f-fn" placeholder="Search function…" style="min-width:220px"><select id="f-status"><option value="">Any status</option>${STATUS_NAMES.map(s => `<option value="${s}">${s}</option>`).join('')}</select><span class="spacer"></span><span class="small muted" id="count"></span></div><div class="table-wrap" id="tbl"></div></div>`;
    const tbl = $('#tbl', content);
    async function render() {
      const q = new URLSearchParams(); if (filters.status) q.set('status', filters.status); if (filters.function) q.set('function', filters.function);
      const rows = await api('/executions?' + q.toString());
      $('#count', content).textContent = rows.length + ' execution(s)';
      tbl.innerHTML = rows.length ? `<table><thead><tr><th>Type</th><th>Function</th><th>Status</th><th>Scheduled</th><th>Executed</th><th>Duration</th><th>Attempts</th><th>Owner</th></tr></thead><tbody>${rows.map(r => `
        <tr class="clickable" data-href="${linkFor(r)}"><td><span class="badge">${r.type === 'CronTickerOccurrence' ? 'cron' : 'time'}</span>${r.isManual ? ' <span class="badge accent">manual</span>' : ''}</td><td>${esc(r.function)}${r.description ? `<div class="small muted">${esc(r.description)}</div>` : ''}</td>
          <td>${badge(r.status)}${r.exceptionType ? `<div class="small muted mono">${esc(r.exceptionType.split('.').pop())}</div>` : ''}${r.skipReason ? `<div class="small muted">${esc(r.skipReason)}</div>` : ''}${r.status === 'InProgress' && r.progressPercent != null ? `<div class="small muted">${r.progressPercent}%</div>` : ''}</td>
          <td>${fmtDate(r.executionTime)}</td><td>${fmtDate(r.executedAt)}</td><td>${fmtMs(r.elapsedMs)}</td><td>${r.retryCount + 1}/${r.retries + 1}</td><td class="small mono muted">${r.lockHolder ? esc(r.lockHolder.split(':')[0]) : ''}</td></tr>`).join('')}</tbody></table>` : '<div class="empty">No executions match.</div>';
    }
    tbl.addEventListener('click', e => { const tr = e.target.closest('tr[data-href]'); if (tr) location.hash = tr.getAttribute('data-href'); });
    let deb;
    $('#f-fn', content).addEventListener('input', e => { filters.function = e.target.value.trim(); clearTimeout(deb); deb = setTimeout(render, 250); });
    $('#f-status', content).addEventListener('change', e => { filters.status = e.target.value; render(); });
    await render();
    setView(render, 5000);
  }

  // ------------------------------------------------------------ views: policies
  const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  function fmtTimeOnly(t) { return t ? String(t).slice(0, 5) : ''; }
  function annual(n) { const m = Math.floor(n / 100), d = n % 100; return `${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`; }

  async function viewPolicies(content) {
    content.innerHTML = `<div class="page-head"><div><h1>Schedule policies</h1><div class="muted small">Reusable business calendars that decide when work may start</div></div><span class="spacer"></span><div class="actions">${wbtn('<button class="btn primary" id="create">+ New policy</button>')}</div></div>
      <div class="grid auto" id="cards"></div>
      <div class="card section"><h2>Preview</h2><div class="filters"><select id="p-name" style="min-width:200px"></select><input id="p-expr" class="mono" placeholder="cron expression, e.g. 0 9 * * *" style="min-width:220px"><input id="p-tz" placeholder="time zone (optional)" list="tz-list" style="min-width:180px"><datalist id="tz-list"></datalist><button class="btn" id="p-run">Preview next 20</button></div><div id="p-out"></div></div>`;
    const cards = $('#cards', content);
    async function render() {
      const list = await loadPolicies();
      cards.innerHTML = list.length ? list.map(({ policy: p, references }) => `<div class="card policy-card">
        <div style="display:flex;align-items:flex-start;gap:8px"><div style="flex:1"><h2 style="margin:0">${esc(p.name)}</h2><div class="small muted">rev ${p.revision} · ${esc(p.timeZoneId)}${p.isSeeded ? ' · <span class="badge accent">From code</span>' : ''}</div></div><span class="badge" title="jobs, crons and policies referencing this">${references} ref${references === 1 ? '' : 's'}</span></div>
        ${p.description ? `<div class="small" style="margin-top:6px">${esc(p.description)}</div>` : ''}
        <div class="days">${DAYS.map((d, i) => `<span class="${(p.allowedWeekdays & (1 << i)) ? '' : 'off'}">${d}</span>`).join('')}</div>
        <dl class="kv small">
          <dt>Window</dt><dd>${p.windowStart && p.windowEnd ? esc(fmtTimeOnly(p.windowStart) + ' – ' + fmtTimeOnly(p.windowEnd)) : 'whole day'}</dd>
          ${p.excludedDates && p.excludedDates.length ? `<dt>Dates</dt><dd class="tags">${p.excludedDates.map(d => tag(d)).join('')}</dd>` : ''}
          ${p.excludedAnnualDates && p.excludedAnnualDates.length ? `<dt>Yearly</dt><dd class="tags">${p.excludedAnnualDates.map(d => tag(annual(d))).join('')}</dd>` : ''}
          ${p.dateProviders && p.dateProviders.length ? `<dt>Providers</dt><dd class="tags">${p.dateProviders.map(d => tag(d)).join('')}</dd>` : ''}
          ${(p.requires && p.requires.length) || (p.exceptWhen && p.exceptWhen.length) ? `<dt>Combine</dt><dd class="tags">${(p.requires || []).map(d => tag('requires ' + d, 'req')).join('')}${(p.exceptWhen || []).map(d => tag('except ' + d, 'exc')).join('')}</dd>` : ''}
        </dl>
        <div class="actions" style="margin-top:8px">${wbtn(`<button class="btn sm" data-act="edit" data-name="${esc(p.name)}">Edit</button><button class="btn sm danger" data-act="delete" data-name="${esc(p.name)}" ${references ? 'disabled title="Referenced; cannot delete"' : ''}>Delete</button>`)}<button class="btn sm" data-act="preview" data-name="${esc(p.name)}">Preview</button></div>
      </div>`).join('') : '<div class="card empty">No schedule policies. Create one or seed them with SeedSchedulePolicies.</div>';
      $('#p-name', content).innerHTML = options(list.map(x => x.policy.name), $('#p-name', content).value || '');
    }
    cards.addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act'); const name = e.target.getAttribute && e.target.getAttribute('data-name');
      if (act === 'edit') { const p = state.policies.find(x => x.policy.name === name); openPolicyForm(p.policy); }
      if (act === 'delete') { if (await confirmDialog(`Delete policy "${name}"?`, 'Delete')) await mutate('/policies/' + encodeURIComponent(name), { method: 'DELETE' }, 'Policy deleted'); }
      if (act === 'preview') { $('#p-name', content).value = name; $('#p-expr', content).focus(); }
    });
    const create = $('#create', content); if (create) create.addEventListener('click', () => openPolicyForm(null));
    loadTimezones().then(z => { $('#tz-list', content).innerHTML = z.map(x => `<option value="${esc(x)}">`).join(''); }).catch(() => {});
    $('#p-run', content).addEventListener('click', async () => {
      const name = $('#p-name', content).value, expr = $('#p-expr', content).value.trim(), tz = $('#p-tz', content).value.trim();
      const out = $('#p-out', content);
      if (!name || !expr) { toast('Pick a policy and enter a cron expression.', 'error'); return; }
      out.innerHTML = '<div class="loading">Evaluating…</div>';
      try {
        const rows = await api(`/policies/${encodeURIComponent(name)}/preview?expression=${encodeURIComponent(expr)}${tz ? '&timeZone=' + encodeURIComponent(tz) : ''}`);
        out.innerHTML = rows.length ? `<table><thead><tr><th>#</th><th>Candidate</th><th>Result</th><th>Reason</th></tr></thead><tbody>${rows.map((r, i) => `<tr><td>${i + 1}</td><td>${fmtDate(r.utc)}</td><td>${r.allowed ? '<span class="badge ok">allowed</span>' : '<span class="badge danger">blocked</span>'}</td><td class="small muted">${esc(r.reason || '')}</td></tr>`).join('')}</tbody></table>` : '<div class="empty">No candidates.</div>';
      } catch (err) { out.innerHTML = `<div class="err-box">${esc(err.message)}</div>`; }
    });
    await render();
    setView(render, 10000);
  }

  async function openPolicyForm(existing) {
    await loadTimezones(); await loadPolicies();
    let providers = []; try { providers = await api('/policies/providers'); } catch (e) { }
    const p = existing || { allowedWeekdays: 127, timeZoneId: 'UTC', excludedDates: [], excludedAnnualDates: [], dateProviders: [], requires: [], exceptWhen: [] };
    const others = state.policies.map(x => x.policy.name).filter(n => !existing || n !== existing.name);
    const m = modal({
      title: existing ? `Edit policy "${existing.name}"` : 'New schedule policy', wide: true,
      body: `<form class="form" id="pol-form">
        <div class="row"><div class="field"><label>Name${existing ? ' (immutable)' : ''}</label><input name="name" value="${esc(p.name || '')}" ${existing ? 'readonly' : ''}></div><div class="field"><label>Time zone</label><select name="timeZoneId">${options(['UTC'].concat(state.timezones.filter(z => z !== 'UTC')), p.timeZoneId || 'UTC')}</select></div></div>
        <div class="field"><label>Description</label><input name="description" value="${esc(p.description || '')}"></div>
        <div class="field"><label>Allowed weekdays</label><div class="tags" style="gap:10px">${DAYS.map((d, i) => `<label class="small"><input type="checkbox" name="day${i}" ${(p.allowedWeekdays & (1 << i)) ? 'checked' : ''}> ${d}</label>`).join('')}</div></div>
        <div class="row"><div class="field"><label>Daily window start (inclusive)</label><input type="time" name="windowStart" value="${esc(fmtTimeOnly(p.windowStart))}"></div><div class="field"><label>Window end (exclusive; earlier than start crosses midnight)</label><input type="time" name="windowEnd" value="${esc(fmtTimeOnly(p.windowEnd))}"></div></div>
        <div class="row"><div class="field"><label>Excluded dates (yyyy-mm-dd, comma-separated)</label><input name="excludedDates" value="${esc((p.excludedDates || []).join(', '))}"></div><div class="field"><label>Excluded annual dates (mm-dd, comma-separated)</label><input name="excludedAnnualDates" value="${esc((p.excludedAnnualDates || []).map(annual).join(', '))}"></div></div>
        <div class="field"><label>Date providers (registered on every node)</label><div class="tags" style="gap:10px">${providers.length ? providers.map(n => `<label class="small"><input type="checkbox" name="dp:${esc(n)}" ${(p.dateProviders || []).includes(n) ? 'checked' : ''}> ${esc(n)}</label>`).join('') : '<span class="faint small">none registered</span>'}</div></div>
        ${others.length ? `<fieldset><legend>Combine policies</legend><div class="form">${others.map(n => `<div class="row cols-3" style="align-items:center"><span>${esc(n)}</span><label class="small"><input type="radio" name="rel:${esc(n)}" value="" ${!(p.requires || []).includes(n) && !(p.exceptWhen || []).includes(n) ? 'checked' : ''}> Not used</label><span><label class="small" style="color:var(--ok)"><input type="radio" name="rel:${esc(n)}" value="req" ${(p.requires || []).includes(n) ? 'checked' : ''}> Require</label> &nbsp; <label class="small" style="color:var(--danger)"><input type="radio" name="rel:${esc(n)}" value="exc" ${(p.exceptWhen || []).includes(n) ? 'checked' : ''}> Exclude</label></span></div>`).join('')}</div></fieldset>` : ''}
      </form>`,
      footer: `<button type="button" class="btn" data-act="cancel">Cancel</button><button type="button" class="btn primary" data-act="save">${existing ? 'Save (rev ' + p.revision + ')' : 'Create'}</button>`,
    });
    m.el.addEventListener('click', async e => {
      const act = e.target.getAttribute && e.target.getAttribute('data-act');
      if (act === 'cancel') m.close();
      if (act === 'save') {
        const form = $('#pol-form', m.el);
        const d = formData(form);
        let mask = 0; DAYS.forEach((_, i) => { if (d['day' + i]) mask |= (1 << i); });
        const requires = [], exceptWhen = [];
        others.forEach(n => { const v = ($(`input[name="rel:${CSS.escape(n)}"]:checked`, form) || {}).value; if (v === 'req') requires.push(n); if (v === 'exc') exceptWhen.push(n); });
        const dateProviders = providers.filter(n => d['dp:' + n]);
        const ann = (d.excludedAnnualDates || '').split(',').map(s => s.trim()).filter(Boolean).map(s => { const [mm, dd] = s.split('-').map(Number); return mm * 100 + dd; }).filter(n => !isNaN(n));
        const body = Object.assign({}, existing || {}, {
          name: d.name, description: d.description, timeZoneId: d.timeZoneId || 'UTC', allowedWeekdays: mask,
          windowStart: d.windowStart ? d.windowStart + ':00' : null, windowEnd: d.windowEnd ? d.windowEnd + ':00' : null,
          excludedDates: (d.excludedDates || '').split(',').map(s => s.trim()).filter(Boolean), excludedAnnualDates: ann, dateProviders, requires, exceptWhen,
          revision: existing ? existing.revision : 0,
        });
        if (!body.name) { toast('Name is required.', 'error'); return; }
        const r = await mutate('/policies', { body }, existing ? 'Policy updated' : 'Policy created');
        if (r) m.close();
      }
    });
  }

  // ------------------------------------------------------------ views: nodes
  async function viewNodes(content) {
    content.innerHTML = `<div class="page-head"><div><h1>Nodes</h1><div class="muted small">Scheduler replicas in namespace ${esc(state.meta.namespace)}</div></div></div><div class="card"><div class="table-wrap" id="tbl"></div></div>`;
    const tbl = $('#tbl', content);
    const open = new Set();
    async function render() {
      const nodes = await api('/nodes');
      tbl.innerHTML = nodes.length ? `<table><thead><tr><th>Node</th><th>State</th><th>Scheduler</th><th>Capacity</th><th>Running / queued</th><th>Heartbeat</th><th>Started</th><th>Version</th><th>Functions</th></tr></thead><tbody>${nodes.map(n => `
        <tr style="${n.online ? '' : 'opacity:.55'}"><td><div>${esc(n.displayName || n.nodeIdentifier)}${n.ownerId === state.meta.ownerId ? ' <span class="badge accent">this host</span>' : ''}</div><div class="small muted mono">${esc(n.machineName || '')} · pid ${n.processId}</div>${n.advertisedDashboardUrl ? `<div class="small"><a href="${esc(n.advertisedDashboardUrl)}" target="_blank" rel="noopener">dashboard ↗</a></div>` : ''}</td>
          <td>${n.online ? '<span class="badge ok"><span class="dot"></span>online</span>' : '<span class="badge"><span class="dot"></span>offline</span>'}</td><td>${n.schedulerRunning ? '<span class="badge ok">running</span>' : '<span class="badge warn">stopped</span>'}</td>
          <td>${n.maxConcurrency}</td><td>${n.running} / ${n.queued}</td><td>${fmtDate(n.lastHeartbeatAt)}<div class="small faint">${esc(fmtRel(n.lastHeartbeatAt))}</div></td><td>${fmtDate(n.startedAt)}</td><td class="mono small">${esc(n.version || '')}</td>
          <td><button class="btn sm" data-act="fns" data-id="${esc(n.ownerId)}">${(n.functions || []).length} function(s)</button>${open.has(n.ownerId) ? `<div class="tags" style="margin-top:6px">${(n.functions || []).map(f => tag(f)).join('')}</div>` : ''}</td></tr>`).join('')}</tbody></table>` : '<div class="empty">No nodes have registered yet. Heartbeats appear within one heartbeat interval.</div>';
    }
    tbl.addEventListener('click', e => { const id = e.target.getAttribute && e.target.getAttribute('data-id'); if (e.target.getAttribute('data-act') === 'fns') { if (open.has(id)) open.delete(id); else open.add(id); render(); } });
    await render();
    setView(render, 5000);
  }

  // ------------------------------------------------------------ views: diagnostics
  async function viewDiagnostics(content) {
    async function render() {
      const d = await api('/diagnostics');
      const light = ok => `<span class="light ${ok === true ? '' : ok === false ? 'bad' : 'warn'}"></span>`;
      content.innerHTML = `<div class="page-head"><div><h1>Diagnostics</h1><div class="muted small">Health of the responding host (${esc(state.meta.ownerId)}). Counters reset on restart.</div></div></div>
        <div class="grid cols-4">
          <div class="card"><h3>Scheduler</h3><div class="health">${light(d.scheduler.running)}<b>${d.scheduler.running ? 'Running' : 'Stopped'}</b></div><dl class="kv small" style="margin-top:8px"><dt>Next wake-up</dt><dd>${fmtDate(d.scheduler.nextWakeUp)}</dd><dt>Last acquisition</dt><dd>${fmtDate(d.scheduler.lastAcquisition)}</dd><dt>Active</dt><dd>${d.scheduler.activeExecutions}</dd><dt>Queued</dt><dd>${d.scheduler.queued}</dd></dl></div>
          <div class="card"><h3>Persistence</h3><div class="health">${light(d.persistence.healthy)}<b>${d.persistence.healthy ? 'Reachable' : 'Unreachable'}</b></div><dl class="kv small" style="margin-top:8px">${d.persistence.error ? `<dt>Error</dt><dd class="err-box">${esc(d.persistence.error)}</dd>` : ''}<dt>Store errors</dt><dd>${d.persistence.errors}</dd><dt>Last error</dt><dd>${fmtDate(d.persistence.lastError)}${d.persistence.lastErrorMessage ? `<div class="small muted">${esc(d.persistence.lastErrorMessage)}</div>` : ''}</dd></dl></div>
          <div class="card"><h3>Fallback scans</h3><div class="health">${light(d.fallback.failures === 0 ? true : null)}<b>${d.fallback.failures} failure(s)</b></div><dl class="kv small" style="margin-top:8px"><dt>Last success</dt><dd>${fmtDate(d.fallback.lastSuccess)}</dd></dl></div>
          <div class="card"><h3>Execution-log store</h3><div class="health">${light(!d.executionLogs.persistEnabled ? null : d.executionLogs.writeFailures === 0)}<b>${d.executionLogs.persistEnabled ? 'Persisting' : 'In-memory only'}</b></div><dl class="kv small" style="margin-top:8px"><dt>Pending writes</dt><dd>${d.executionLogs.pendingWrites}</dd><dt>Write failures</dt><dd>${d.executionLogs.writeFailures}</dd><dt>Dropped</dt><dd>${d.executionLogs.dropped}</dd><dt>Last flush</dt><dd>${fmtDate(d.executionLogs.lastFlush)}</dd></dl></div>
        </div>
        <div class="card section"><h2>Warnings</h2>${d.warnings && d.warnings.length ? `<ul class="warn-list" style="list-style:none;padding:0;margin:0">${d.warnings.map(w => `<li><span class="small faint nowrap">${fmtDate(w.at)}</span> &nbsp; ${esc(w.message)}</li>`).join('')}</ul>` : '<div class="empty small">No runtime warnings recorded.</div>'}
        <div class="small muted" style="margin-top:10px">If jobs are not running: check that the scheduler is running on at least one node, that the function is registered on that node (About → functions), and that the persistence store is reachable.</div></div>`;
    }
    await render();
    setView(render, 5000);
  }

  // ------------------------------------------------------------ views: about
  async function viewAbout(content) {
    const a = await api('/about');
    const kv = obj => `<dl class="kv small">${Object.keys(obj).map(k => `<dt>${esc(k)}</dt><dd>${obj[k] == null ? '<span class="faint">—</span>' : Array.isArray(obj[k]) ? esc(obj[k].join(', ')) : typeof obj[k] === 'object' ? esc(JSON.stringify(obj[k])) : esc(String(obj[k]))}</dd>`).join('')}</dl>`;
    content.innerHTML = `<div class="page-head"><div><h1>About</h1><div class="muted small">Tempus ${esc(a.version || '')} · open source background job scheduler for .NET</div></div></div>
      <div class="grid cols-2">
        <div class="card"><h3>General</h3>${kv({ namespace: a.namespace, store: a.store, node: a.node.nodeIdentifier, 'display name': a.node.nodeDisplayName, 'owner id': a.node.ownerId, machine: a.node.machine, pid: a.node.pid, started: a.node.startedAt ? new Date(a.node.startedAt).toISOString() : null, dashboard: state.meta.basePath, 'display time zone': state.meta.timeZone })}</div>
        <div class="card"><h3>Scheduler</h3>${kv(a.scheduler)}</div>
        <div class="card"><h3>Retention</h3>${kv(a.retention)}</div>
        <div class="card"><h3>Execution logs</h3>${kv(a.executionLogs)}</div>
      </div>
      <div class="card section"><h2>Registered functions (${a.functions.length})</h2><div class="table-wrap"><table><thead><tr><th>Name</th><th>Request</th><th>Result</th><th>Max concurrency</th><th>Priority</th><th>Cron</th><th>Source</th></tr></thead><tbody>${a.functions.map(f => `<tr><td><b>${esc(f.name)}</b></td><td class="mono small">${esc(f.requestType || '')}</td><td class="mono small">${esc(f.resultType || '')}</td><td>${f.maxConcurrency || '<span class="faint">unlimited</span>'}</td><td>${esc(f.priority)}</td><td class="mono small">${esc(f.cron || '')}</td><td class="small muted">${esc(f.source || '')}</td></tr>`).join('')}</tbody></table></div></div>`;
    setView(async () => {}, 60000);
  }

  // ------------------------------------------------------------ boot
  async function boot() {
    applyTheme();
    try {
      await loadMeta();
    } catch (e) {
      $('#app').innerHTML = `<div style="padding:40px;max-width:560px;margin:auto;font-family:system-ui"><h2>Tempus dashboard</h2><div class="err-box">${esc(e.message)}</div><p class="small muted">If you use basic authentication, reload the page and enter your credentials. API keys must be sent as a header.</p><button class="btn" onclick="location.reload()">Reload</button></div>`;
      return;
    }
    window.addEventListener('hashchange', route);
    await route();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
