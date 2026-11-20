/* RyvenPanel Web – Aussehen wie die Desktop-App. Spricht nur mit dem Gateway (/panel/*, /api/v2/*):
   gleiche Accounts, Rechte, IP-Freigaben und Protokollierung wie die App. */
'use strict';

const VERSION = 'web 1.8';
const $ = (sel, el = document) => el.querySelector(sel);
const $$ = (sel, el = document) => [...el.querySelectorAll(sel)];
const app = $('#app');
let me = null;
let timers = [];

// ───────────────────────────── Icons (Linien wie in der App) ─────────────────────────────
const P = {
  server: '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>',
  shield: '<path d="M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6z"/>',
  chart: '<path d="M3 3v18h18"/><path d="M7 15l4-5 3 3 5-7"/>',
  radar: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="5"/><path d="M12 12l6-6"/>',
  flag: '<path d="M5 21V4h11l-1.5 4L16 12H5"/>',
  ban: '<circle cx="12" cy="12" r="9"/><path d="M5.6 5.6l12.8 12.8"/>',
  lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V8a4 4 0 018 0v3"/>',
  link: '<path d="M10 14a5 5 0 007 0l3-3a5 5 0 00-7-7l-1 1"/><path d="M14 10a5 5 0 00-7 0l-3 3a5 5 0 007 7l1-1"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  minus: '<circle cx="12" cy="12" r="9"/><path d="M8 12h8"/>',
  pulse: '<path d="M3 12h4l3-8 4 16 3-8h4"/>',
  box: '<path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M3 7l9 4 9-4M12 11v10"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3"/>',
  card: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 10h18"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0113 0"/><path d="M16 4.5a3.5 3.5 0 010 7M21.5 20a6.5 6.5 0 00-4-6"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  alert: '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18h.01"/>',
  check: '<path d="M4 12l5 5L20 6"/>',
  refresh: '<path d="M20 11a8 8 0 10-2.3 6.3"/><path d="M20 4v7h-7"/>',
  menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
  bot: '<rect x="4" y="8" width="16" height="11" rx="3"/><path d="M12 4v4M9 13h.01M15 13h.01M9 16.5h6"/>',
};
const ic = (n, cls = '') => `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${P[n] || ''}</svg>`;

// ───────────────────────────── Hilfen ─────────────────────────────
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const can = p => !!me && (me.perms || []).includes(p);
const fmt = (n, d = 0) => Number(n || 0).toLocaleString('de-DE', { maximumFractionDigits: d });
function kurz(n) {
  const a = Math.abs(n || 0), s = n < 0 ? '-' : '';
  if (a >= 1e9) return s + fmt(a / 1e9, 2) + ' Mrd';
  if (a >= 1e6) return s + fmt(a / 1e6, a >= 1e7 ? 1 : 2) + ' Mio';
  if (a >= 1e4) return s + fmt(a / 1e3, 1) + ' Tsd';
  return s + fmt(a, 2);
}
const geld = n => kurz(n) + ' $';
/* Zeiten aus der Datenbank sind Serverzeit (Europe/Berlin, "2026-11-15 13:40:00") */
function datum(s) {
  if (!s) return null;
  if (typeof s === 'number') return new Date(s > 1e12 ? s : s * 1000);
  const t = String(s).replace(' ', 'T');
  const d = new Date(t);   // ohne Zeitzone = Ortszeit des Browsers (Server läuft auf Europe/Berlin)
  return isNaN(d) ? null : d;
}
function zeit(s) {
  const d = datum(s);
  if (!d) return '–';
  const heute = new Date(), gestern = new Date(Date.now() - 864e5);
  const hm = d.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
  if (d.toDateString() === heute.toDateString()) return 'heute ' + hm;
  if (d.toDateString() === gestern.toDateString()) return 'gestern ' + hm;
  return d.toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit' }) + ' ' + hm;
}
const dauer = sec => { sec = Number(sec || 0); const h = Math.floor(sec / 3600); return h >= 24 ? Math.floor(h / 24) + ' T ' + (h % 24) + ' h' : h + ' h ' + Math.floor(sec % 3600 / 60) + ' min'; };
const jetzt = () => new Date().toLocaleTimeString('de-DE');
function toast(text, bad) { const d = document.createElement('div'); if (bad) d.className = 'bad'; d.textContent = text; $('#toast').appendChild(d); setTimeout(() => d.remove(), 5000); }
function clearTimers() { timers.forEach(clearInterval); timers = []; }
function token() { try { return sessionStorage.getItem('rp_token') || localStorage.getItem('rp_token'); } catch { return null; } }
function setToken(t, merken) {
  try { sessionStorage.removeItem('rp_token'); localStorage.removeItem('rp_token'); if (t) (merken ? localStorage : sessionStorage).setItem('rp_token', t); } catch { }
}
function status(text) { const s = $('#sbtext'); if (s) s.textContent = text; }
/* ANSI-Farbcodes aus der Server-Konsole entfernen */
const ohneAnsi = s => String(s).replace(/\u001b\[[0-9;]*[A-Za-z]/g, '').replace(/§[0-9a-fk-or]/gi, '');

async function api(method, path, body, raw) {
  const h = {};
  if (token()) h.Authorization = 'Bearer ' + token();
  let b;
  if (body !== undefined) { if (raw) { b = body; h['Content-Type'] = 'text/plain'; } else { b = JSON.stringify(body); h['Content-Type'] = 'application/json'; } }
  let r;
  try { r = await fetch(path, { method, headers: h, body: b, cache: 'no-store' }); }
  catch { throw new Error('Keine Verbindung zum Server.'); }
  let j = null;
  try { j = await r.json(); } catch { }
  if (r.status === 401) { setToken(null); me = null; loginView('Sitzung abgelaufen – bitte neu anmelden.'); throw new Error('abgemeldet'); }
  if (!r.ok || (j && j.status && j.status !== 'ok')) throw new Error((j && (j.error_data || j.error)) || ('Fehler ' + r.status));
  return j;
}
const mod = (cmd, args = {}) => api('POST', '/panel/mod', { cmd, args }).then(j => j.data ?? j);
const eco = (cmd, args = {}) => api('POST', '/panel/eco', { cmd, args }).then(j => j.data ?? j);
async function safe(fn) { try { return await fn(); } catch (e) { if (e.message !== 'abgemeldet') toast(e.message, true); } }

// ───────────────────────────── Login ─────────────────────────────
function loginView(msg) {
  clearTimers();
  app.innerHTML = `<div class="center"><form class="card login" id="lf" autocomplete="on">
    <div class="logo">RyvenMC</div><div class="sub">Server-Panel</div>
    <div class="tabline">Team-Login</div>
    <label class="lab">Benutzername</label><input class="field" id="u" autocomplete="username" required autofocus>
    <label class="lab">Passwort</label><input class="field" id="p" type="password" autocomplete="current-password" required>
    <label class="check" style="margin-top:12px"><input type="checkbox" id="m"> Angemeldet bleiben</label>
    <div class="err" id="e">${esc(msg || '')}</div>
    <button class="btn primary">Anmelden</button>
    <p class="muted small" style="margin-top:22px">secure.ryvenmc.de · neue Geräte/IPs gibt der Owner frei.</p></form></div>`;
  $('#lf').onsubmit = async ev => {
    ev.preventDefault();
    $('#e').textContent = '';
    const btn = $('#lf .btn');
    btn.disabled = true;
    try {
      const j = await api('POST', '/panel/login', { username: $('#u').value.trim(), password: $('#p').value });
      if (j.data.need_2fa) return tfaLoginView(j.data.ticket, $('#m').checked);
      setToken(j.data.token, $('#m').checked);
      await laden();
    } catch (e) { $('#e').textContent = e.message === 'abgemeldet' ? '' : e.message; btn.disabled = false; }
  };
}

function tfaLoginView(ticket, remember) {
  app.innerHTML = `<div class="center"><form class="card login" id="tf">
    <div class="logo">RyvenMC</div><div class="sub">Zwei-Faktor-Code</div>
    <label class="lab">Code aus der Authenticator-App (oder Wiederherstellungscode)</label>
    <input class="field" id="c" autocomplete="one-time-code" inputmode="numeric" required autofocus>
    <div class="err" id="e"></div><button class="btn primary">Bestätigen</button>
    <button type="button" class="btn" id="zu" style="width:100%;margin-top:8px">Abbrechen</button></form></div>`;
  $('#zu').onclick = () => loginView();
  $('#tf').onsubmit = async ev => {
    ev.preventDefault();
    $('#e').textContent = '';
    try {
      const j = await api('POST', '/panel/login/2fa', { ticket, code: $('#c').value.trim() });
      setToken(j.data.token, remember);
      await laden();
    } catch (e) { $('#e').textContent = e.message === 'abgemeldet' ? '' : e.message; }
  };
}

async function tfaView(erzwungen) {
  clearTimers();
  let st;
  try { st = (await api('POST', '/panel/2fa', { cmd: 'status' })).data; } catch (e) { toast(e.message); return; }
  const rahmen = inner => { app.innerHTML = `<div class="center"><div class="card login" id="tv">
    <div class="logo">RyvenMC</div><div class="sub">Zwei-Faktor-Authentifizierung</div>${inner}
    ${erzwungen && !st.enabled ? '' : '<button type="button" class="btn" id="zu" style="width:100%;margin-top:8px">Zurück</button>'}</div></div>`;
    const z = $('#zu'); if (z) z.onclick = () => laden(); };
  if (st.enabled) {
    rahmen(`<p class="muted small">2FA ist aktiv. Übrige Wiederherstellungscodes: ${st.recovery_left}.</p>
      ${st.required ? '<p class="muted small">Für deinen Rang ist 2FA Pflicht – nur der Owner kann sie zurücksetzen.</p>' : `
      <label class="lab">Passwort</label><input class="field" id="pw" type="password" autocomplete="current-password">
      <label class="lab">Aktueller Code</label><input class="field" id="c" inputmode="numeric" autocomplete="one-time-code">
      <div class="err" id="e"></div><button class="btn" id="off">2FA deaktivieren</button>`}`);
    const off = $('#off');
    if (off) off.onclick = async () => {
      try { await api('POST', '/panel/2fa', { cmd: 'disable', password: $('#pw').value, code: $('#c').value.trim() }); toast('2FA deaktiviert.'); laden(); }
      catch (e) { $('#e').textContent = e.message; }
    };
    return;
  }
  rahmen(`<p class="muted small">${erzwungen ? 'Für deinen Rang ist 2FA Pflicht. ' : ''}Trage das Geheimnis in einer Authenticator-App ein (Typ: zeitbasiert, 6 Stellen, 30 s).</p>
    <button class="btn primary" id="st">Einrichtung starten</button><div class="err" id="e"></div>`);
  $('#st').onclick = async () => {
    try {
      const s = (await api('POST', '/panel/2fa', { cmd: 'setup' })).data;
      let qr = '';
      try {
        const q = qrcode(0, 'M'); q.addData(String(s.uri)); q.make();
        const cs = Math.max(3, Math.floor(200 / (q.getModuleCount() + 8)));
        qr = `<div style="background:#fff;padding:8px;border-radius:8px;display:block;width:fit-content;line-height:0;margin:10px auto 14px" aria-label="QR-Code für Authenticator-App">${q.createSvgTag(cs, cs * 4)}</div>
        <p class="muted small">Mit der Authenticator-App scannen – oder das Geheimnis unten eintragen.</p>`;
      } catch (e) { qr = ''; }
      rahmen(`${qr}<label class="lab">Geheimnis</label><input class="field" readonly value="${esc(s.secret)}" onclick="this.select()">
        <p class="small"><a href="${esc(s.uri)}">In Authenticator-App öffnen (otpauth)</a></p>
        <label class="lab">Code zur Bestätigung</label><input class="field" id="c" inputmode="numeric" autocomplete="one-time-code" autofocus>
        <div class="err" id="e"></div><button class="btn primary" id="ok">Aktivieren</button>`);
      $('#ok').onclick = async () => {
        try {
          const r = (await api('POST', '/panel/2fa', { cmd: 'enable', code: $('#c').value.trim() })).data;
          rahmen(`<p class="small">2FA ist aktiv. Speichere diese Wiederherstellungscodes jetzt – sie werden nur einmal angezeigt und gelten je einmal:</p>
            <pre class="field" style="white-space:pre-wrap;user-select:all">${esc(r.recovery_codes.join('\n'))}</pre>
            <button class="btn primary" id="fin">Fertig</button>`);
          $('#fin').onclick = () => laden();
        } catch (e) { $('#e').textContent = e.message; }
      };
    } catch (e) { $('#e').textContent = e.message; }
  };
}

function passwortView(erzwungen) {
  clearTimers();
  app.innerHTML = `<div class="center"><form class="card login" id="pf">
    <div class="logo">RyvenMC</div><div class="sub">${erzwungen ? 'Bitte lege zuerst ein eigenes Passwort fest.' : 'Passwort ändern'}</div>
    <label class="lab">Aktuelles Passwort</label><input class="field" id="o" type="password" autocomplete="current-password" required>
    <label class="lab">Neues Passwort</label><input class="field" id="n" type="password" autocomplete="new-password" required minlength="8">
    <label class="lab">Wiederholen</label><input class="field" id="n2" type="password" autocomplete="new-password" required>
    <div class="err" id="e"></div><button class="btn primary">Speichern</button>
    ${erzwungen ? '' : '<button type="button" class="btn" id="zu" style="width:100%;margin-top:8px">Abbrechen</button>'}</form></div>`;
  const zu = $('#zu'); if (zu) zu.onclick = () => render();
  $('#pf').onsubmit = async ev => {
    ev.preventDefault();
    if ($('#n').value !== $('#n2').value) { $('#e').textContent = 'Die Passwörter stimmen nicht überein.'; return; }
    try { await api('POST', '/panel/password', { old: $('#o').value, new: $('#n').value }); toast('Passwort geändert.'); await laden(); }
    catch (e) { $('#e').textContent = e.message; }
  };
}

async function laden() {
  if (!token()) return loginView();
  try { me = (await api('GET', '/panel/me')).data; } catch (e) { if (e.message !== 'abgemeldet') loginView(e.message); return; }
  if (me.must_change) return passwortView(true);
  if (me.tfa_setup_required) return tfaView(true);
  render();
}

// ───────────────────────────── Rahmen ─────────────────────────────
const SEITEN = [
  { sec: 'Minecraft', id: 'server', name: 'Server-Steuerung', icon: 'server', perm: 'servers.view', view: serverView },
  { sec: 'Minecraft', id: 'moderation', name: 'Moderation', icon: 'shield', perm: 'moderation.view', view: modView },
  { sec: 'Minecraft', id: 'wirtschaft', name: 'Wirtschaft', icon: 'chart', perm: 'economy.view', view: ecoView },
  { sec: 'Minecraft', id: 'nutzer', name: 'Nutzer & Ränge', icon: 'users', perm: 'users.view', view: nutzerView },
  { sec: 'Minecraft', id: 'ki', name: 'KI-Support', icon: 'bot', perm: 'ki.view', view: kiView },
  { sec: 'Verwaltung', id: 'netzwerk', name: 'Netzwerk-Monitor', icon: 'pulse', perm: 'netzwerk.view', view: netzView },
  { sec: 'Datenschutz', id: 'datenschutz', name: 'Datenschutz & Compliance', icon: 'radar', perm: 'datenschutz.view', view: dsView },
  { sec: 'Verwaltung', id: 'team', name: 'Team & Zugänge', icon: 'key', perm: 'team.manage', view: teamView },
];

function render() {
  if (!me) return loginView();
  clearTimers();
  const sichtbar = SEITEN.filter(s => can(s.perm));
  const teile = (location.hash || '').slice(1).split('/').map(decodeURIComponent);
  let s = sichtbar.find(x => x.id === teile[0]);
  if (!s) { s = sichtbar[0]; teile.length = 0; }
  let nav = '', sec = '';
  for (const x of sichtbar) {
    if (x.sec !== sec) { sec = x.sec; nav += `<div class="sec">${esc(sec)}</div>`; }
    nav += `<a class="nav ${x === s ? 'on' : ''}" href="#${x.id}">${ic(x.icon)}${esc(x.name)}</a>`;
  }
  app.innerHTML = `<div class="layout"><nav class="side" id="side"><div class="logo">RyvenMC</div><div class="ver">Control Panel · ${VERSION}</div>
      ${nav}
      <div class="who"><div class="avatar">${esc((me.username || '?')[0].toUpperCase())}</div>
        <div class="name">${esc(me.username)}<small>${esc(me.rank || '')} · <a href="#" id="pw">Passwort</a> · <a href="#" id="tf2">2FA</a></small></div>
        <button class="btn" id="lo">Abmelden</button></div></nav>
    <main class="main" id="view"></main>
    <div class="statusbar"><span><span id="sbclock">${jetzt()}</span>&nbsp;&nbsp;<span id="sbtext">Bereit</span></span><span>RyvenMC.de</span></div></div>`;
  $('#lo').onclick = async () => { await api('POST', '/panel/logout').catch(() => { }); setToken(null); me = null; loginView('Abgemeldet.'); };
  $('#pw').onclick = e => { e.preventDefault(); passwortView(false); };
  $('#tf2').onclick = e => { e.preventDefault(); tfaView(false); };
  timers.push(setInterval(() => { const c = $('#sbclock'); if (c) c.textContent = jetzt(); }, 1000));
  // Rechte können sich ändern (Rang in LuckPerms) – jede Minute prüfen
  timers.push(setInterval(async () => {
    try { const n = (await api('GET', '/panel/me')).data; if (JSON.stringify(n.perms) !== JSON.stringify(me.perms)) { me = n; render(); } } catch { }
  }, 60000));
  const v = $('#view');
  if (!s) { v.innerHTML = '<div class="head"><h1>Keine Rechte</h1></div><p class="muted">Dein Rang hat im Panel noch keine Rechte – frag den Owner.</p>'; return; }
  s.view(v, teile.slice(1));
  dsBadge();
  timers.push(setInterval(dsBadge, 120000));
}
window.addEventListener('hashchange', () => { if (me && !me.must_change) render(); });
document.addEventListener('click', e => { const side = $('#side'); if (side && side.classList.contains('open') && !side.contains(e.target) && !e.target.closest('.menubtn')) side.classList.remove('open'); });

function kopf(titel, sub, actions = '') {
  return `<div class="head"><button class="btn icon menubtn" onclick="document.getElementById('side').classList.toggle('open')">${ic('menu')}</button>
    <div><h1>${esc(titel)}</h1><div class="subline" id="subline">${sub}</div></div><div class="actions">${actions}</div></div>`;
}
const tabs = (basis, liste, akt) => `<div class="tabs">${liste.map(([k, n]) => `<a href="#${basis}/${k}" class="${k === akt ? 'on' : ''}">${n}</a>`).join('')}</div>`;
const kpi = (icon, farbe, lbl, val, note = '', badge = '', valCls = '') =>
  `<div class="card kpi glow"><div class="top"><div class="ico c-${farbe}">${ic(icon)}</div><div class="lbl">${esc(lbl)}</div>${badge}</div>
   <div class="val ${valCls}">${val}</div><div class="note">${note}</div></div>`;
function seg(el, werte, akt, onchange) {
  el.innerHTML = werte.map(([k, n]) => `<button data-k="${k}" class="${k === akt ? 'on' : ''}">${n}</button>`).join('');
  $$('button', el).forEach(b => b.onclick = () => { $$('button', el).forEach(x => x.classList.toggle('on', x === b)); onchange(b.dataset.k); });
}

// ───────────────────────────── Server-Steuerung ─────────────────────────────
async function serverView(el, args) {
  if (args[0]) return konsoleView(el, args[0]);
  el.innerHTML = kopf('Server-Steuerung', 'Alle Server des Netzwerks · Stand <span id="stand">…</span>', `<button class="btn icon" id="rf" title="Aktualisieren">${ic('refresh')}</button>`) +
    '<div class="cards" id="kz" style="margin-top:16px"></div><div class="cards" id="sv"><div class="spinner"></div></div>';
  const list = (await safe(() => api('GET', '/api/v2/servers')))?.data || [];
  const box = $('#sv', el);
  async function aktualisieren() {
    status('Lade Server-Status …');
    const stats = await Promise.all(list.map(s => api('GET', `/api/v2/servers/${s.server_id}/stats`).then(j => j.data).catch(() => null)));
    const an = st => st && (st.running === true || String(st.running) === 'true');
    const laufen = stats.filter(an).length, spieler = stats.reduce((a, st) => a + (an(st) ? Number(st.online || 0) : 0), 0);
    $('#kz', el).innerHTML = kpi('server', 'cyan', 'Server online', `${laufen} / ${list.length}`, 'laufende Server') +
      kpi('users', 'pink', 'Spieler online', fmt(spieler), 'auf allen Servern');
    box.innerHTML = list.map((s, i) => {
      const st = stats[i] || {}, on = an(st), crash = String(st.crashed) === 'true';
      return `<div class="card srv glow"><div class="name"><span class="sdot ${on ? 'green' : crash ? 'red' : ''}"></span>${esc(s.server_name)}</div>
        <div class="meta">${on ? `${esc(st.online)}/${esc(st.max)} Spieler · CPU ${fmt(st.cpu, 1)} % · RAM ${fmt(st.mem_percent, 1)} % · ${esc(st.version || '')}` : (crash ? '<span class="c-red-t">abgestürzt</span>' : 'gestoppt')}</div>
        <div class="btnrow">
          ${can('console.view') ? `<a class="btn" href="#server/${encodeURIComponent(s.server_id)}">Konsole</a>` : ''}
          ${can('server.control') ? (on ? `<button class="btn red" data-a="stop_server" data-s="${s.server_id}">Stopp</button><button class="btn" data-a="restart_server" data-s="${s.server_id}">Neustart</button>`
                                         : `<button class="btn green" data-a="start_server" data-s="${s.server_id}">Start</button>`) : ''}
          ${can('server.backup') ? `<button class="btn" data-a="backup_server" data-s="${s.server_id}">Backup</button>` : ''}
        </div></div>`;
    }).join('') || '<div class="empty">Keine Server freigegeben.</div>';
    $$('button[data-a]', box).forEach(b => b.onclick = async () => {
      const name = list.find(x => x.server_id === b.dataset.s)?.server_name;
      if (!confirm(`${b.textContent}: ${name}?`)) return;
      b.disabled = true;
      await safe(async () => { await api('POST', `/api/v2/servers/${b.dataset.s}/action/${b.dataset.a}`); toast(`${b.textContent}: ${name} – ausgeführt.`); });
      setTimeout(aktualisieren, 3000);
    });
    const sd = $('#stand', el); if (sd) sd.textContent = jetzt();
    status('Bereit');
  }
  $('#rf', el).onclick = aktualisieren;
  await aktualisieren();
  timers.push(setInterval(aktualisieren, 15000));
}

async function konsoleView(el, id) {
  el.innerHTML = kopf('Konsole', '<a href="#server">← Server-Steuerung</a>', `<label class="check"><input type="checkbox" id="auto" checked> mitscrollen</label>`) +
    `<div class="mono console" id="c" style="margin-top:14px"></div>
    ${can('console.command') ? `<form class="tools" id="f" style="margin-top:10px"><input class="field" style="max-width:none;flex:1" id="cmd" placeholder="Befehl ohne / …" autocomplete="off"><button class="btn primary">Senden</button></form>` : ''}`;
  safe(async () => { const i = await api('GET', `/api/v2/servers/${id}`); if (i?.data?.server_name) $('h1', el).textContent = 'Konsole · ' + i.data.server_name; });
  const c = $('#c', el);
  let letzte = '';
  async function holen() {
    const j = await api('GET', `/api/v2/servers/${id}/logs?raw=true`).catch(() => null);
    if (!j) return;
    const text = (j.data || []).map(ohneAnsi).join('\n');
    if (text === letzte) return;
    c.textContent = letzte = text;
    if ($('#auto', el)?.checked) c.scrollTop = c.scrollHeight;
  }
  await holen();
  c.scrollTop = c.scrollHeight;
  timers.push(setInterval(holen, 2500));
  const f = $('#f', el);
  if (f) f.onsubmit = async ev => {
    ev.preventDefault();
    const cmd = $('#cmd', el).value.trim().replace(/^\//, '');
    if (!cmd) return;
    await safe(async () => { await api('POST', `/api/v2/servers/${id}/stdin`, cmd, true); $('#cmd', el).value = ''; setTimeout(holen, 700); });
  };
}

// ───────────────────────────── KI-Support ─────────────────────────────
async function kiView(el, args) {
  const tab = args[0] || 'uebersicht';
  el.innerHTML = kopf('KI-Support', 'Discord-Bot-Assistent · Fragen, Eskalationen und Wissen', `<button class="btn icon" id="rf" title="Aktualisieren">${ic('refresh')}</button>`) +
    tabs('ki', [['uebersicht', 'Übersicht'], ['verlauf', 'Verlauf'], ['wissen', 'Wissen'], ['einstellungen', 'Einstellungen']], tab) + '<div id="kv"></div>';
  $('#rf', el).onclick = () => render();
  const v = $('#kv', el);
  if (tab === 'uebersicht') return kiUebersicht(v);
  if (tab === 'verlauf') return kiVerlauf(v, args[1]);
  if (tab === 'wissen') return kiWissen(v);
  if (tab === 'einstellungen') return kiEinstellungen(v);
}

async function kiUebersicht(v) {
  v.innerHTML = '<div class="spinner"></div>';
  const o = await safe(() => mod('ki_overview'));
  if (!o) { v.innerHTML = ''; return; }
  v.innerHTML = `<div class="cards">` +
    kpi('pulse', o.aktiv ? 'green' : 'red', 'Status', o.aktiv ? 'aktiv' : 'aus', o.laeuft ? (o.api_key ? 'Bot läuft, API-Key gesetzt' : 'Bot läuft, aber kein API-Key') : 'Bot-Container läuft nicht') +
    kpi('chart', 'cyan', 'Fragen', fmt(o.heute), `heute · ${fmt(o.tage7)} in 7 Tagen · ${fmt(o.gesamt)} gesamt`) +
    kpi('flag', 'yellow', 'Eskalationsrate', fmt(o.eskalationsrate, 1) + ' %', `${fmt(o.eskaliert7)} von ${fmt(o.tage7)} in 7 Tagen`) +
    kpi('alert', 'red', 'Offene Eskalationen', `<span class="c-red-t">${fmt(o.offen)}</span>`, `${fmt(o.fehler7)} Fehler in 7 Tagen · ${fmt(o.wartend)} wartend`) +
    `</div><div class="card" style="margin-top:16px"><div class="meta">Letzte 7 Tage: ${fmt(o.discord7)} über Discord · ${fmt(o.ingame7)} ingame.
      ${o.offen ? `<br><a class="btn primary" href="#ki/verlauf/offen">${fmt(o.offen)} offene Eskalationen ansehen</a>` : ''}</div></div>`;
}

async function kiVerlauf(v, vorFilter) {
  let filter = vorFilter || 'alle', source = '', offset = 0;
  v.innerHTML = `<div class="tools"><div class="seg" id="fl"></div><div class="seg" id="sr"></div><input class="field" id="q" placeholder="Spieler, Frage oder Antwort …"></div>
    <div class="split"><div class="tcard"><table class="t" id="t"></table><div class="btnrow" id="pg" style="padding:8px"></div></div><div class="card detail" id="d"><div class="empty">Eintrag links auswählen.</div></div></div>`;
  let items = [];
  async function liste() {
    const r = await safe(() => mod('ki_verlauf', { filter, source, q: $('#q', v).value, offset, limit: 50 }));
    items = r?.items || [];
    $('#t', v).innerHTML = '<tr><th>#</th><th>Von</th><th>Frage</th><th>Zeit</th><th>Status</th></tr>' +
      (items.map(x => `<tr class="click" data-id="${x.id}"><td class="muted">#${x.id}</td><td><b>${esc(x.user)}</b><div class="muted small">${esc(x.source)}</div></td>
        <td class="wrap">${esc(String(x.question).slice(0, 110))}</td><td>${zeit(x.created)}</td>
        <td>${Number(x.escalated) === 1 ? '<span class="badge red">offen</span>' : Number(x.escalated) === 2 ? '<span class="badge">erledigt</span>' : esc(x.status)}</td></tr>`).join('')
        || '<tr><td colspan="5" class="empty">Keine Einträge.</td></tr>');
    $('#pg', v).innerHTML = `<button class="btn" id="pv" ${offset ? '' : 'disabled'}>‹ Neuer</button><button class="btn" id="nx" ${r?.mehr ? '' : 'disabled'}>Älter ›</button>`;
    $('#pv', v).onclick = () => { offset = Math.max(0, offset - 50); liste(); };
    $('#nx', v).onclick = () => { offset += 50; liste(); };
    $$('tr[data-id]', v).forEach(tr => tr.onclick = () => { $$('tr.sel', v).forEach(x => x.classList.remove('sel')); tr.classList.add('sel'); detail(items.find(i => String(i.id) === tr.dataset.id)); });
  }
  function detail(x) {
    const d = $('#d', v), esk = Number(x.escalated);
    d.innerHTML = `<h2>#${x.id} · ${esc(x.user)}</h2>
      <div class="meta">${esc(x.source)} · ${zeit(x.created)} · Status ${esc(x.status)}${esk === 1 ? ' · <b class="c-red-t">Eskalation offen</b>' : esk === 2 ? ' · Eskalation erledigt' : ''}</div>
      <div class="sechead">Frage</div><div class="mono chat">${esc(x.question)}</div>
      <div class="sechead">Antwort der KI</div><div class="mono chat">${x.answer ? esc(x.answer) : '<span class="muted">Keine Antwort.</span>'}</div>
      <div class="btnrow">${can('ki.edit') && esk === 1 ? '<button class="btn green" id="ok">Als erledigt markieren</button>' : ''}${can('ki.edit') && esk === 2 ? '<button class="btn" id="re">Wieder öffnen</button>' : ''}</div>`;
    const set = st => async () => { await safe(async () => { await mod('ki_set_status', { id: x.id, status: st }); toast(`#${x.id}: ${st === 'done' ? 'erledigt' : 'wieder offen'}`); d.innerHTML = '<div class="empty">Eintrag links auswählen.</div>'; liste(); }); };
    if ($('#ok', d)) $('#ok', d).onclick = set('done');
    if ($('#re', d)) $('#re', d).onclick = set('open');
  }
  seg($('#fl', v), [['alle', 'Alle'], ['eskaliert', 'Eskaliert'], ['offen', 'Offen'], ['erledigt', 'Erledigt']], filter, k => { filter = k; offset = 0; liste(); });
  seg($('#sr', v), [['', 'Alle Quellen'], ['discord', 'Discord'], ['ingame', 'Ingame']], source, k => { source = k; offset = 0; liste(); });
  let t; $('#q', v).oninput = () => { clearTimeout(t); t = setTimeout(() => { offset = 0; liste(); }, 350); };
  await liste();
}

async function kiWissen(v) {
  v.innerHTML = '<div class="spinner"></div>';
  const r = await safe(() => mod('ki_wissen'));
  const dateien = r?.dateien || [];
  v.innerHTML = `<div class="split"><div class="tcard"><table class="t" id="t"></table></div><div class="card detail" id="d"><div class="empty">Datei links auswählen.</div></div></div>`;
  $('#t', v).innerHTML = '<tr><th>Datei</th><th>Größe</th><th>Geändert</th></tr>' +
    (dateien.map(f => `<tr class="click" data-n="${esc(f.name)}"><td><b>${esc(f.name)}</b>${f.auto ? ' <span class="muted small">automatisch</span>' : ''}</td><td>${fmt(f.groesse / 1024, 1)} KB</td><td>${zeit(f.geaendert)}</td></tr>`).join('')
      || '<tr><td colspan="3" class="empty">Keine Dateien.</td></tr>');
  $$('tr[data-n]', v).forEach(tr => tr.onclick = async () => {
    $$('tr.sel', v).forEach(x => x.classList.remove('sel')); tr.classList.add('sel');
    const d = $('#d', v); d.innerHTML = '<div class="spinner"></div>';
    const f = await safe(() => mod('ki_wissen_lesen', { name: tr.dataset.n }));
    if (!f) { d.innerHTML = '<div class="empty">Konnte nicht gelesen werden.</div>'; return; }
    const edit = can('ki.edit') && !f.auto;
    d.innerHTML = `<h2>${esc(f.name)}</h2><div class="meta">${f.auto ? 'Wird täglich automatisch aus den Server-Configs erzeugt – nur Ansicht. ' : ''}Änderungen sind spätestens nach 60 Minuten aktiv (der Bot lädt das Wissen stündlich neu); vor jedem Speichern wird eine Sicherung angelegt.</div>
      <textarea class="field mono" id="tx" rows="22" style="width:100%" ${edit ? '' : 'readonly'}>${esc(f.text)}</textarea>
      <div class="btnrow">${edit ? '<button class="btn primary" id="sv">Speichern</button>' : ''}</div>`;
    if (edit) $('#sv', d).onclick = () => safe(async () => {
      const x = await mod('ki_wissen_speichern', { name: f.name, text: $('#tx', d).value });
      toast(x.hinweis || 'Gespeichert');
    });
  });
}

async function kiEinstellungen(v) {
  v.innerHTML = '<div class="spinner"></div>';
  const c = await safe(() => mod('ki_config'));
  if (!c) { v.innerHTML = ''; return; }
  const edit = can('ki.edit'), g = c.grenzen || {};
  v.innerHTML = `<form class="card" id="f" style="max-width:520px">
    <label class="lab">Wartezeit zwischen zwei Fragen (Sekunden, ${g.cooldown_seconds?.[0]}–${g.cooldown_seconds?.[1]})</label>
    <input class="field" name="cooldown_seconds" type="number" value="${esc(c.cooldown_seconds)}" ${edit ? '' : 'disabled'}>
    <label class="lab">Fragen pro Spieler und Tag (${g.daily_limit?.[0]}–${g.daily_limit?.[1]})</label>
    <input class="field" name="daily_limit" type="number" value="${esc(c.daily_limit)}" ${edit ? '' : 'disabled'}>
    <div class="meta" style="margin:10px 0">Der Bot liest diese Werte nur beim Start – nach dem Speichern gelten sie erst nach einem Neustart des Containers <b>ryvenlink-bot</b>.</div>
    ${edit ? '<button class="btn primary">Speichern</button>' : ''}</form>`;
  $('#f', v).onsubmit = e => { e.preventDefault(); const f = e.target; safe(async () => {
    const x = await mod('ki_config_set', { cooldown_seconds: Number(f.cooldown_seconds.value), daily_limit: Number(f.daily_limit.value) });
    toast(x.hinweis || 'Gespeichert');
  }); };
}

// ───────────────────────────── Moderation ─────────────────────────────
const PUN = { ban: 'Ban', mute: 'Mute', kick: 'Kick', warn: 'Verwarnung' };
const REP = { open: 'offen', claimed: 'in Bearbeitung', done: 'erledigt', rejected: 'abgelehnt' };
const REPDOT = { open: 'red', claimed: 'yellow', done: 'green', rejected: '' };
const darfStrafen = () => can('moderation.punish') || can('moderation.ban');

async function modView(el, args) {
  const tab = args[0] || 'reports';
  el.innerHTML = kopf('Moderation', 'Reports, Strafen und Spieler · Aktionen gelten netzwerkweit (RyvenBans) · Stand <span id="stand">' + jetzt() + '</span>',
      (darfStrafen() ? `<a class="btn primary" href="#moderation/bestrafen">+ Strafe vergeben</a>` : '') + `<button class="btn icon" id="rf" title="Aktualisieren">${ic('refresh')}</button>`) +
    tabs('moderation', [['reports', 'Reports'], ['strafen', 'Strafen'], ['verify', 'Verify'], ['spieler', 'Spieler'], ...(darfStrafen() ? [['bestrafen', 'Bestrafen']] : [])], tab) +
    '<div class="cards" id="ov"></div><div id="mv"></div>';
  $('#rf', el).onclick = () => render();
  safe(async () => {
    const o = await mod('overview');
    $('#ov', el).innerHTML = kpi('flag', 'red', 'Offene Reports', `<span class="c-red-t">${fmt(o.reports_open)}</span>`, `${fmt(o.reports_claimed)} in Bearbeitung`) +
      kpi('ban', 'pink', 'Aktive Banns', fmt(o.bans), 'netzwerkweit gesperrt') +
      kpi('lock', 'yellow', 'Aktive Mutes', fmt(o.mutes), `${fmt(o.today)} Strafen in 24 h`) +
      kpi('users', 'cyan', 'Discord verknüpft', fmt(o.links), `+${fmt(o.links_today)} in 24 h`);
  });
  const v = $('#mv', el);
  if (tab === 'reports') return reportsTab(v, args[1]);
  if (tab === 'strafen') return strafenTab(v);
  if (tab === 'spieler') return spielerTab(v, args[1]);
  if (tab === 'bestrafen') return bestrafenTab(v, args[1]);
  if (tab === 'verify') return verifyTab(v);
}

async function reportsTab(v, offenId) {
  v.innerHTML = `<div class="tools"><div class="seg" id="st"></div><input class="field" id="q" placeholder="Spieler oder Grund …"></div>
    <div class="split"><div class="tcard"><table class="t" id="t"></table></div><div class="card detail" id="d"><div class="empty">Report links auswählen.</div></div></div>`;
  let filter = 'active', gewaehlt = offenId ? Number(offenId) : null;
  async function liste() {
    const r = await safe(() => mod('reports', { status: filter, q: $('#q', v).value }));
    const rows = r?.reports || [];
    $('#t', v).innerHTML = '<tr><th>Gemeldet</th><th>#</th><th>Von</th><th>Grund</th><th>Zeit</th><th>Status</th></tr>' +
      (rows.map(x => `<tr class="click ${x.id === gewaehlt ? 'sel' : ''}" data-id="${x.id}"><td><span class="sdot ${REPDOT[x.status] || ''}"></span><b>${esc(x.target)}</b></td><td class="muted">#${x.id}</td>
        <td>${esc(x.reporter)}</td><td class="wrap">${esc(x.reason)}</td><td>${zeit(x.created)}</td><td class="st-${x.status}">${REP[x.status] || esc(x.status)}</td></tr>`).join('')
        || '<tr><td colspan="6" class="empty">Keine Reports.</td></tr>');
    $$('tr[data-id]', v).forEach(tr => tr.onclick = () => { gewaehlt = Number(tr.dataset.id); $$('tr.sel', v).forEach(x => x.classList.remove('sel')); tr.classList.add('sel'); detail(); });
    if (gewaehlt && rows.some(x => x.id === gewaehlt)) detail();
  }
  async function detail() {
    const d = $('#d', v);
    d.innerHTML = '<div class="spinner"></div>';
    const r = await safe(() => mod('report', { id: gewaehlt }));
    if (!r) { d.innerHTML = '<div class="empty">Report nicht gefunden.</div>'; return; }
    const kn = (st, txt, cls = '') => can('moderation.reports') && r.status !== st ? `<button class="btn ${cls}" data-st="${st}">${txt}</button>` : '';
    const letzte = (r.target_punishments || [])[0];
    d.innerHTML = `<h2>#${r.id} · ${esc(r.target)}</h2>
      <div class="meta">Gemeldet von <b>${esc(r.reporter)}</b> · ${zeit(r.created)} · ${esc(r.server)}<br>Grund: ${esc(r.reason)}<br>
        Status: <span class="st-${r.status}">${REP[r.status] || esc(r.status)}</span>${r.handler ? ' (' + esc(r.handler) + ')' : ''}<br>
        ${esc(r.target)}: ${fmt(r.target_reports)} Reports insgesamt · ${(r.target_punishments || []).length} Strafen${letzte ? ` (zuletzt ${PUN[letzte.type] || letzte.type}: ${esc(letzte.reason)})` : ''}</div>
      <div class="btnrow">${kn('claimed', 'Übernehmen')}${kn('done', 'Erledigt', 'green')}${kn('rejected', 'Ablehnen')}
        ${darfStrafen() ? `<a class="btn primary" href="#moderation/bestrafen/${encodeURIComponent(r.target)}">Spieler bestrafen …</a>` : ''}
        <a class="btn" href="#moderation/spieler/${encodeURIComponent(r.target)}">Akte</a></div>
      <div class="sechead">Chatverlauf (60 s vor der Meldung)</div>
      <div class="mono chat">${r.chatlog ? esc(r.chatlog) : '<span class="muted">Kein Chatverlauf gespeichert.</span>'}</div>`;
    $$('button[data-st]', d).forEach(b => b.onclick = async () => {
      await safe(async () => { await mod('report_set', { id: r.id, status: b.dataset.st }); toast(`Report #${r.id}: ${REP[b.dataset.st]}`); liste(); });
    });
  }
  seg($('#st', v), [['active', 'Offen'], ['done', 'Erledigt'], ['rejected', 'Abgelehnt'], ['all', 'Alle']], filter, k => { filter = k; liste(); });
  let t; $('#q', v).oninput = () => { clearTimeout(t); t = setTimeout(liste, 350); };
  await liste();
  timers.push(setInterval(liste, 30000));
}

function strafenTabelle(rows) {
  return `<div class="tcard"><table class="t"><tr><th>#</th><th>Art</th><th>Spieler</th><th>Grund</th><th>Von</th><th>Seit</th><th>Bis</th><th>Status</th></tr>` +
    (rows.map(x => `<tr><td class="muted">#${x.id}</td><td class="typ-${x.type}"><b>${PUN[x.type] || esc(x.type)}${x.ip_ban ? ' (IP)' : ''}</b></td>
      <td><a href="#moderation/spieler/${encodeURIComponent(x.name)}"><b>${esc(x.name)}</b></a></td><td class="wrap">${esc(x.reason)}</td><td>${esc(x.actor)}</td>
      <td>${zeit(x.created)}</td><td>${x.expires ? zeit(x.expires) : (x.type === 'ban' || x.type === 'mute' ? 'permanent' : '–')}</td>
      <td>${x.active ? '<span class="badge red">aktiv</span>' : (x.removed_by ? `<span class="muted small">aufgehoben · ${esc(x.removed_by)}</span>` : '<span class="muted small">vorbei</span>')}</td></tr>`).join('')
      || '<tr><td colspan="8" class="empty">Keine Einträge.</td></tr>') + '</table></div>';
}

async function strafenTab(v) {
  v.innerHTML = `<div class="tools"><div class="seg" id="ty"></div><label class="check"><input type="checkbox" id="ak"> nur aktive</label>
    <input class="field" id="q" placeholder="Spieler, Grund oder Teammitglied …"></div><div id="t"><div class="spinner"></div></div>`;
  let typ = 'all';
  async function liste() {
    const r = await safe(() => mod('punishments', { type: typ, active: $('#ak', v).checked, q: $('#q', v).value }));
    $('#t', v).innerHTML = strafenTabelle(r?.punishments || []);
  }
  seg($('#ty', v), [['all', 'Alle'], ['ban', 'Banns'], ['mute', 'Mutes'], ['kick', 'Kicks'], ['warn', 'Verwarnungen']], typ, k => { typ = k; liste(); });
  $('#ak', v).onchange = liste;
  let t; $('#q', v).oninput = () => { clearTimeout(t); t = setTimeout(liste, 350); };
  await liste();
}

async function spielerTab(v, name) {
  v.innerHTML = `<form class="tools" id="f"><input class="field" id="n" placeholder="Spielername …" value="${esc(name || '')}"><button class="btn primary">Akte öffnen</button></form><div id="a"></div>`;
  $('#f', v).onsubmit = e => { e.preventDefault(); const n = $('#n', v).value.trim(); if (n) location.hash = 'moderation/spieler/' + encodeURIComponent(n); };
  if (!name) { $('#a', v).innerHTML = '<div class="empty">Namen eingeben, um die Akte eines Spielers zu sehen.</div>'; return; }
  const p = await safe(() => mod('player', { name }));
  if (!p) return;
  $('#a', v).innerHTML = `<div class="cards">
      ${kpi('pulse', p.online ? 'green' : 'accent', 'Status', p.online ? '<span class="c-green-t">online</span>' : 'offline', p.online ? esc(p.server || '') : 'zuletzt ' + zeit(p.last_seen))}
      ${kpi('chart', 'cyan', 'Spielzeit', dauer(p.playtime), 'seit ' + zeit(p.first_join))}
      ${kpi('link', 'pink', 'Discord', p.link ? 'verknüpft' : '–', p.link ? esc(p.link.groups || '') : 'nicht verknüpft')}
      ${kpi('flag', 'red', 'Reports', `${(p.reports_against || []).length} / ${fmt(p.reports_by)}`, 'gegen ihn / von ihm')}</div>
    <div class="head" style="margin:6px 0 10px"><div><h1 style="font-size:18px">Strafen von ${esc(p.name)}</h1></div>
      <div class="actions">${darfStrafen() ? `<a class="btn primary" href="#moderation/bestrafen/${encodeURIComponent(p.name)}">Bestrafen …</a>` : ''}</div></div>
    ${strafenTabelle(p.punishments || [])}`;
}

async function bestrafenTab(v, name) {
  const aktionen = [['warn', 'Verwarnen', 'moderation.punish'], ['kick', 'Kicken', 'moderation.punish'], ['mute', 'Muten', 'moderation.punish'], ['unmute', 'Entmuten', 'moderation.punish'],
    ['ban', 'Bannen', 'moderation.ban'], ['ipban', 'IP-Bannen', 'moderation.ban'], ['unban', 'Entbannen', 'moderation.ban']].filter(a => can(a[2]));
  const vorl = (await safe(() => mod('templates')))?.templates || [];
  v.innerHTML = `<form class="card" id="f" style="max-width:640px">
    <label class="lab" style="margin-top:0">Spieler</label><input class="field" id="n" required value="${esc(name || '')}">
    <label class="lab">Aktion</label><select class="field" id="a">${aktionen.map(a => `<option value="${a[0]}">${a[1]}</option>`).join('')}</select>
    ${vorl.length ? `<label class="lab">Vorlage (Dauer steigt bei Wiederholung automatisch)</label><select class="field" id="vo"><option value="">– keine –</option>
      ${vorl.map(t => `<option value="#${esc(t.key)}">${esc(t.reason)} · ${esc(t.type)} ${esc((t.ladder || []).join(' → '))}</option>`).join('')}</select>` : ''}
    <label class="lab">Dauer <span class="muted">(30m, 6h, 7d, perm – leer bei Vorlage, Verwarnung, Kick)</span></label><input class="field" id="d" placeholder="7d">
    <label class="lab">Grund</label><input class="field" id="g" maxlength="200">
    <label class="check" style="margin-top:12px"><input type="checkbox" id="si"> still (keine Ansage im Chat)</label>
    <div class="err" id="e"></div><button class="btn primary">Ausführen</button></form>`;
  const vo = $('#vo', v);
  if (vo) vo.onchange = () => { if (vo.value) { $('#g', v).value = vo.value; $('#d', v).value = ''; } };
  $('#f', v).onsubmit = async e => {
    e.preventDefault();
    const a = { action: $('#a', v).value, player: $('#n', v).value.trim(), duration: $('#d', v).value.trim(), reason: $('#g', v).value.trim(), silent: $('#si', v).checked };
    if (!confirm(`${$('#a', v).selectedOptions[0].text}: ${a.player}?`)) return;
    $('#e', v).textContent = '';
    status('Strafe wird ausgeführt …');
    try {
      const r = await mod('act', a);
      toast(r.done ? (r.message || `${a.player}: ${PUN[r.punishment?.type] || 'erledigt'} #${r.punishment?.id ?? ''}`) : r.message, !r.done);
      if (r.done) location.hash = 'moderation/spieler/' + encodeURIComponent(a.player);
    } catch (err) { $('#e', v).textContent = err.message; }
    status('Bereit');
  };
}

// ───────────────────────────── Wirtschaft ─────────────────────────────
function linie(werte, zeiten) {
  const w = werte.filter(x => x != null);
  if (w.length < 2) return '<div class="muted small">Noch zu wenig Verlauf.</div>';
  const min = Math.min(...w), max = Math.max(...w), sp = max - min || 1, W = 600, H = 150;
  const pts = werte.map((y, i) => [i / (werte.length - 1) * W, H - 12 - ((y ?? min) - min) / sp * (H - 30)]);
  const d = pts.map((p, i) => (i ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' ');
  const last = pts[pts.length - 1];
  const fm = t => t ? new Date(t).toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit' }) + 'h' : '';
  return `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none"><defs>
      <linearGradient id="lg" x1="0" x2="1"><stop offset="0" stop-color="#55e0ff"/><stop offset="1" stop-color="#d966ff"/></linearGradient>
      <linearGradient id="fg" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#55e0ff" stop-opacity=".22"/><stop offset="1" stop-color="#55e0ff" stop-opacity="0"/></linearGradient></defs>
    <path d="${d} L ${W} ${H} L 0 ${H} Z" fill="url(#fg)"/><path d="${d}" fill="none" stroke="url(#lg)" stroke-width="3" vector-effect="non-scaling-stroke"/>
    <circle cx="${last[0]}" cy="${last[1]}" r="5" fill="#fff" stroke="#d966ff" stroke-width="3"/></svg><div class="chartlbl"><span>${fm(zeiten?.[0])}</span><span>${fm(zeiten?.[zeiten.length - 1])}</span></div>`;
}

async function ecoView(el, args) {
  const tab = args[0] || 'uebersicht';
  el.innerHTML = kopf('Wirtschaft', '<span id="ws">…</span>', `<div class="seg" id="tg"></div><select class="field" id="sv"><option value="">Alle Server</option></select>
      <button class="btn icon" id="rf" title="Aktualisieren">${ic('refresh')}</button>`) +
    tabs('wirtschaft', [['uebersicht', 'Übersicht'], ['spieler', 'Spieler'], ['shop', 'Shop'], ['crates', 'Crates'], ['preise', 'Preise']], tab) + '<div id="ev"><div class="spinner"></div></div>';
  const v = $('#ev', el);
  if (tab === 'preise') { $('#tg', el).style.display = 'none'; $('#sv', el).style.display = 'none'; $('#rf', el).onclick = () => render(); return preiseTab(v, el); }
  let tage = 7;
  seg($('#tg', el), [['1', '24 h'], ['3', '3 T'], ['7', '7 T'], ['14', '14 T'], ['30', '30 T']], '7', k => { tage = Number(k); laden(); });
  $('#sv', el).onchange = () => laden();
  $('#rf', el).onclick = () => laden();
  async function laden() {
    v.innerHTML = '<div class="spinner"></div>';
    status('Lade Wirtschaftsdaten …');
    const d = await safe(() => eco('summary', { days: tage, server: $('#sv', el).value || undefined }));
    status('Bereit');
    if (!d) { v.innerHTML = '<div class="empty">Keine Daten.</div>'; return; }
    const sel = $('#sv', el);
    if (sel.options.length === 1) (d.all_servers || []).forEach(s => sel.add(new Option(s, s)));
    $('#ws', el).textContent = `${(d.servers || []).join(', ')} · ${d.days === 1 ? '24 h' : d.days + ' Tage'} · ${fmt(d.accounts)} Konten · Stand ${jetzt()}`;
    if (tab === 'spieler') return spielerEco(v, d);
    if (tab === 'shop') return shopEco(v, d);
    if (tab === 'crates') return cratesEco(v, d);
    const diff = (d.supply_now || 0) - (d.supply_start || 0), proz = d.supply_start ? diff / d.supply_start * 100 : 0;
    const summe = (d.total_in || 0) + (d.total_out || 0) || 1;
    const shop = d.shop || {}, tax = d.tax || {}, crates = d.crates || {};
    const crateGeld = (crates.list || []).reduce((a, c) => a + (c.money || 0), 0), crateOpen = (crates.list || []).reduce((a, c) => a + (c.opens || 0), 0);
    const maxP = Math.max(1, ...(d.players || []).slice(0, 8).map(p => (p.in || 0) + (p.out || 0)));
    v.innerHTML = `${(d.warnings || []).map(w => `<div class="note-warn">⚠ ${esc(w)}</div>`).join('')}
      <div class="card glow hero"><div><div class="dotlbl">Geldmenge im Umlauf</div><div class="big">${fmt(d.supply_now)} $</div>
        <span class="badge ${diff >= 0 ? 'yellow' : 'green'}">${diff >= 0 ? '▲' : '▼'} ${diff >= 0 ? '+' : ''}${fmt(proz, 1)} % in ${d.days === 1 ? '24 h' : d.days + ' Tagen'}</span>
        <p class="muted small" style="margin:10px 0 0">Summe aller Kontostände · zu Beginn ${geld(d.supply_start)}. Wächst die Geldmenge schneller als die Spielerzahl, verliert Geld an Wert.</p></div>
        <div>${linie(d.supply || [], d.buckets || [])}</div></div>
      <div class="cards">
        ${kpi('plus', 'green', 'Geld erzeugt', geld(d.total_in), 'Shop, Crates, Pay, Jobs …', `<span class="badge green">${fmt(d.total_in / summe * 100)} %</span>`, 'c-green-t')}
        ${kpi('minus', 'red', 'Geld vernichtet', geld(d.total_out), 'Shop-Käufe, Steuer u. a.', `<span class="badge red">${fmt(d.total_out / summe * 100)} %</span>`, 'c-red-t')}
        ${kpi('pulse', 'yellow', 'Netto (Inflation)', (diff >= 0 ? '+' : '') + geld(d.total_in - d.total_out), 'mehr Geld im Umlauf', `<span class="badge yellow">Inflation</span>`, 'c-yellow-t')}
        ${kpi('box', 'pink', 'Shop: Verkauf / Kauf', `${kurz(shop.sell)} / ${kurz(shop.buy)}`, `${fmt(shop.sell_n)} Verkäufe · ${fmt(shop.buy_n)} Käufe`)}
        ${kpi('key', 'accent', 'Geld aus Crates', geld(crateGeld), `${fmt(crateOpen)} Öffnungen`)}
      </div>
      <div class="sechead">Verkaufssteuer je Server</div><div class="grid-tax">${steuerKarten(d.taxes || [])}</div>
      <div class="two"><div class="card"><div class="head" style="margin-bottom:8px"><div><b>Top-Verdiener</b> <span class="muted small">Einnahmen im Zeitraum</span></div></div><div class="blist">
          ${(d.players || []).slice(0, 8).map(p => `<div class="row"><div class="l"><span><span class="sdot cyan"></span>${esc(p.name)}</span><span>${geld(p.in)}</span></div>
            <div class="bar"><i style="width:${(p.in || 0) / maxP * 100}%;background:var(--green)"></i><i style="width:${(p.out || 0) / maxP * 100}%;background:var(--red)"></i></div></div>`).join('') || '<div class="empty">Keine Daten.</div>'}</div></div>
        <div class="card"><div class="head" style="margin-bottom:8px"><div><b>Woher kommt das Geld?</b> <span class="muted small">nach Quelle</span>${d.ausgeblendet?.anzahl ? `<div class="muted small">${fmt(d.ausgeblendet.anzahl)} Team-Konten ausgeblendet (${esc((d.ausgeblendet.namen || []).join(", "))})</div>` : ""}${d.admin_ausgeblendet?.count ? `<div class="muted small">Admin-Buchungen (/eco) ausgeblendet: ${fmt(d.admin_ausgeblendet.count)} Stück, ${geld(d.admin_ausgeblendet.in)} rein / ${geld(d.admin_ausgeblendet.out)} raus</div>` : ""}</div></div>
          <table class="t"><tr><th>Quelle</th><th>rein</th><th>raus</th><th>Spieler</th></tr>
          ${(d.sources || []).slice(0, 12).map(s => `<tr><td>${esc(s.source)}</td><td class="c-green-t">${geld(s.in)}</td><td class="c-red-t">${geld(s.out)}</td><td>${fmt(s.players)}</td></tr>`).join('')}</table></div></div>`;
  }
  await laden();
}

function steuerKarten(l) {
  return l.map(t => `<div class="card tax"><div class="rk-top"><b style="font-size:15px">${esc(t.server)}</b>${t.enabled ? '<span class="badge green">aktiv</span>' : '<span class="badge muted">aus</span>'}</div>
    <div class="mv ${t.enabled ? 'c-cyan-t' : 'muted'}">${t.enabled ? fmt(t.percent, 1) + ' %' : 'keine Steuer'}</div>
    <div class="muted small">auf ${esc((t.sources || []).join(', ') || '–')}</div>
    <div class="acc-meta"><div><span class="muted small">Eingenommen</span><br><b class="c-red-t">${geld(t.eingenommen)}</b></div><div><span class="muted small">Buchungen</span><br>${fmt(t.buchungen)}</div></div>
    ${can('economy.edit') ? `<div class="btnrow tight"><button class="btn" data-steuer="${esc(t.server)}" data-p="${t.percent}" data-on="${t.enabled ? 1 : 0}">Ändern</button></div>` : ''}</div>`).join('')
    || '<div class="empty">RyvenEco ist auf keinem Server installiert.</div>';
}
document.addEventListener('click', async e => {
  const b = e.target.closest('[data-steuer]');
  if (!b) return;
  const srv = b.dataset.steuer;
  const r = await dialog('Verkaufssteuer – ' + srv, `<div class="muted small">Gilt nur für „${esc(srv)}“. RyvenEco wird dort sofort neu geladen.</div>
    <label class="lab">Steuersatz in %</label><input class="field" name="percent" type="number" min="0" max="90" step="0.5" value="${esc(b.dataset.p)}" required>
    <label class="check"><input type="checkbox" name="enabled" ${b.dataset.on === '1' ? 'checked' : ''}> Steuer aktiv</label>`, { ok: 'Speichern' });
  if (!r) return;
  const ok = await safe(async () => { await eco('settax', { server: srv, percent: Number(r.percent), enabled: r.enabled }); return true; });
  if (ok) { toast(`Steuer auf ${srv}: ${r.enabled && Number(r.percent) > 0 ? fmt(Number(r.percent), 1) + ' %' : 'aus'}.`); render(); }
});

function spielerEco(v, d) {
  v.innerHTML = `<div class="two"><div><div class="sechead">Top-Verdiener im Zeitraum</div><div class="tcard"><table class="t"><tr><th>Spieler</th><th>rein</th><th>raus</th><th>Kontostand</th></tr>
      ${(d.players || []).slice(0, 60).map(p => `<tr><td><b>${esc(p.name)}</b></td><td class="c-green-t">${geld(p.in)}</td><td class="c-red-t">${geld(p.out)}</td><td>${geld(p.balance)}</td></tr>`).join('') || '<tr><td colspan="4" class="empty">Keine Daten.</td></tr>'}</table></div></div>
    <div><div class="sechead">Reichste Spieler</div><div class="tcard"><table class="t"><tr><th>#</th><th>Spieler</th><th>Kontostand</th></tr>
      ${(d.richest || []).slice(0, 30).map((p, i) => `<tr><td class="muted">${i + 1}</td><td><b>${esc(p.name)}</b></td><td>${fmt(p.balance)} $</td></tr>`).join('')}</table></div></div></div>`;
}

async function preiseTab(v, el) {
  const d = await safe(() => eco('items'));
  if (!d) { v.innerHTML = '<div class="empty">Keine Shop-Daten.</div>'; return; }
  $('#ws', el).textContent = `Shop von ${d.server} · ${fmt(d.items.length)} Items`;
  const edit = can('economy.edit');
  v.innerHTML = `${edit ? '<p class="muted small" style="margin-top:0">Preis ändern und <b>Enter</b> drücken. Leer = nicht kaufbar/verkaufbar. Warnung, wenn Verkauf über Kauf liegt.</p>' : ''}
    <div class="tools"><select class="field" id="se"><option value="">Alle Bereiche</option>${(d.sections || []).map(s => `<option>${esc(s)}</option>`).join('')}</select>
    <input class="field" id="q" placeholder="Item suchen …"></div><div class="tcard"><table class="t" id="t"></table></div>`;
  const preis = x => x >= 0 ? x : '';
  function zeigen() {
    const se = $('#se', v).value, q = $('#q', v).value.toLowerCase();
    const rows = d.items.filter(i => (!se || i.section === se) && (!q || (i.name + ' ' + i.material).toLowerCase().includes(q))).slice(0, 500);
    $('#t', v).innerHTML = '<tr><th>Item</th><th>Bereich</th><th>Kaufen</th><th>Verkaufen</th><th></th></tr>' + rows.map(i => {
      const warn = i.buy >= 0 && i.sell >= 0 && i.sell > i.buy;
      return `<tr data-p="${esc(i.path)}"><td><b>${esc(i.name || i.material)}</b>${i.hidden ? ' <span class="muted small">(versteckt)</span>' : ''}</td><td class="muted">${esc(i.section)}</td>
        <td>${edit ? `<input class="field" data-k="buy" value="${preis(i.buy)}" style="width:120px">` : (i.buy >= 0 ? geld(i.buy) : '–')}</td>
        <td>${edit ? `<input class="field" data-k="sell" value="${preis(i.sell)}" style="width:120px">` : (i.sell >= 0 ? geld(i.sell) : '–')}</td>
        <td>${warn ? '<span class="badge red">Verkauf > Kauf</span>' : ''}</td></tr>`;
    }).join('');
    if (edit) $$('input[data-k]', v).forEach(inp => inp.onkeydown = async e => {
      if (e.key !== 'Enter') return;
      const tr = inp.closest('tr'), args = { path: tr.dataset.p };
      args[inp.dataset.k] = inp.value.trim() === '' ? -1 : inp.value.trim();
      await safe(async () => {
        await eco('setprice', args);
        const it = d.items.find(x => x.path === tr.dataset.p); if (it) it[inp.dataset.k] = Number(args[inp.dataset.k]);
        toast('Gespeichert – aktiv nach dem nächsten Shop-Reload.'); inp.style.borderColor = 'var(--green)';
      });
    });
  }
  $('#se', v).onchange = zeigen;
  $('#q', v).oninput = zeigen;
  zeigen();
}

// ───────────────────────────── Netzwerk-Monitor ─────────────────────────────
const netz = (cmd, args = {}) => api('POST', '/panel/netz', { cmd, args }).then(j => j.data ?? j);
const NETZ_FARBE = { gateway: '#ffc857', cloudflare: '#55e0ff', google: '#3ddc84', quad9: '#d966ff' };
const NETZ_NAME = { cloudflare: 'Cloudflare', google: 'Google', quad9: 'Quad9', gateway: 'Gateway' };
const netzMbit = b => b == null ? '–' : (b / 1e6 >= 10 ? fmt(b / 1e6, 1) : fmt(b / 1e6, 2)) + ' Mbit/s';
const netzDauer = s => s < 60 ? s + ' s' : s < 3600 ? Math.floor(s / 60) + ' min ' + (s % 60) + ' s' : Math.floor(s / 3600) + ' h ' + Math.floor(s % 3600 / 60) + ' min';
const netzZeit = (t, sek) => new Date(t * 1000).toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', ...(sek ? { second: '2-digit' } : {}) });

/** Diagramm mit Achsen, Zahlen und beweglichem Cursor (Maus/Touch): zeigt Zeit und alle Werte an der Stelle.
 *  reihen: [{name, farbe, werte[], strich?}], leer[i] = erfasster Messausfall (rot). Einzelne fehlende Punkte werden überbrückt. */
function netzChart(box, reihen, zeiten, einheit, leer, { nachkomma = 1, festMax } = {}) {
  const n = zeiten.length, alle = reihen.flatMap(r => r.werte).filter(x => x != null);
  if (alle.length < 2) { box.innerHTML = '<div class="muted small">Noch zu wenig Messungen.</div>'; return; }
  const roh = Math.max(...alle, festMax ?? 0);          // festMax = Mindesthöhe der Achse
  const schritt = (() => { const z = roh / 4 || 1, p = 10 ** Math.floor(Math.log10(z)); return [1, 2, 2.5, 5, 10].map(f => f * p).find(f => f >= z); })();
  const max = Math.max(schritt * 4, festMax ?? 0), W = 1000, H = 200;
  const x = i => i / Math.max(1, n - 1) * W, y = v => H - v / max * H;
  const pfade = reihen.map(r => {
    let d = '', an = false;
    r.werte.forEach((v, i) => {
      if (v == null) { if (leer?.[i]) an = false; return; }
      d += (an ? 'L' : 'M') + x(i).toFixed(1) + ' ' + y(v).toFixed(1); an = true;
    });
    return `<path d="${d}" fill="none" stroke="${r.farbe}" stroke-width="${r.strich ? 1.5 : 2}" ${r.strich ? 'stroke-dasharray="6 4"' : ''} vector-effect="non-scaling-stroke"/>`;
  }).join('');
  const raster = [1, 2, 3].map(k => `<line x1="0" x2="${W}" y1="${y(schritt * k)}" y2="${y(schritt * k)}" stroke="#ffffff12" vector-effect="non-scaling-stroke"/>`).join('');
  const rot = (leer || []).map((l, i) => l ? `<rect x="${x(i) - W / n / 2}" y="0" width="${W / Math.max(1, n - 1) + 1}" height="${H}" fill="#ff5c6c" opacity=".3"/>` : '').join('');
  const xs = [0, .25, .5, .75, 1].map(f => Math.round(f * (n - 1)));
  box.innerHTML = `<div style="display:flex;gap:14px;flex-wrap:wrap;margin-bottom:6px" class="muted small">${reihen.map(r => `<span><b style="color:${r.farbe}">${r.strich ? '┅' : '━'}</b> ${esc(r.name)}</span>`).join('')}</div>
    <div style="display:flex;gap:8px"><div style="position:relative;width:46px;flex:none;height:${H}px" class="muted small">
        ${[0, 1, 2, 3, 4].map(k => `<div style="position:absolute;right:0;top:${(1 - k / 4) * 100}%;transform:translateY(-50%)">${fmt(schritt * k, schritt * k < 10 && schritt % 1 ? 1 : 0)} ${k === 4 ? esc(einheit) : ''}</div>`).join('')}</div>
      <div class="nc" style="position:relative;flex:1;height:${H}px;cursor:crosshair;touch-action:pan-y">
        <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" style="width:100%;height:100%;display:block;background:#ffffff05;border-radius:6px;overflow:hidden">${raster}${rot}${pfade}</svg>
        <div class="nlinie" style="position:absolute;top:0;bottom:0;width:1px;background:#fff9;display:none;pointer-events:none"></div>
        ${reihen.map((r, k) => `<div class="npkt" data-k="${k}" style="position:absolute;width:9px;height:9px;margin:-4.5px 0 0 -4.5px;border-radius:50%;background:${r.farbe};border:2px solid #0d0f14;display:none;pointer-events:none"></div>`).join('')}
        <div class="ntip" style="position:absolute;top:6px;display:none;pointer-events:none;background:#12151cf2;border:1px solid #ffffff22;border-radius:8px;padding:8px 10px;font-size:12.5px;white-space:nowrap;z-index:5;box-shadow:0 6px 20px #0008"></div>
      </div></div>
    <div style="display:flex;justify-content:space-between;margin:4px 0 0 54px" class="muted small">${xs.map(i => `<span>${netzZeit(zeiten[i])}</span>`).join('')}</div>`;
  const nc = $('.nc', box), linie = $('.nlinie', nc), tip = $('.ntip', nc), pkte = [...nc.querySelectorAll('.npkt')];
  const zeige = cx => {
    const r = nc.getBoundingClientRect(), f = Math.min(1, Math.max(0, (cx - r.left) / r.width)), i = Math.round(f * (n - 1)), px = i / Math.max(1, n - 1) * r.width;
    linie.style.display = 'block'; linie.style.left = px + 'px';
    pkte.forEach((p, k) => { const v = reihen[k].werte[i]; p.style.display = v == null ? 'none' : 'block'; if (v != null) { p.style.left = px + 'px'; p.style.top = (1 - v / max) * r.height + 'px'; } });
    tip.innerHTML = `<div style="font-weight:700;margin-bottom:4px">${netzZeit(zeiten[i], true)}</div>` + (leer?.[i] ? '<div class="c-red-t">Keine Messung (Ausfall)</div>' :
      reihen.map(rr => `<div style="display:flex;gap:10px;justify-content:space-between"><span><b style="color:${rr.farbe}">●</b> ${esc(rr.name)}</span><b>${rr.werte[i] == null ? '–' : fmt(rr.werte[i], nachkomma) + ' ' + esc(einheit)}</b></div>`).join(''));
    tip.style.display = 'block';
    const tw = tip.offsetWidth; tip.style.left = (px + 14 + tw > r.width ? px - tw - 14 : px + 14) + 'px';
  };
  const weg = () => { linie.style.display = tip.style.display = 'none'; pkte.forEach(p => p.style.display = 'none'); };
  nc.onmousemove = e => zeige(e.clientX); nc.onmouseleave = weg;
  nc.ontouchstart = nc.ontouchmove = e => zeige(e.touches[0].clientX); nc.ontouchend = () => setTimeout(weg, 1500);
}

async function netzView(el) {
  el.innerHTML = kopf('Netzwerk-Monitor', 'Unabhängige Messung alle 10 s – eigener Dienst außerhalb von Docker, Crafty und Minecraft · Stand <span id="stand">…</span>',
      `<div class="seg" id="zr"></div><button class="btn" id="pr">${ic('check')} Integrität prüfen</button><button class="btn primary" id="ex">${ic('box')} CSV-Export</button>`) +
    `<div class="cards" id="nk" style="margin-top:16px"><div class="spinner"></div></div>
     <div class="tcard" id="nz" style="margin-bottom:16px"></div>
     <div class="card" style="margin-bottom:14px"><div class="sec-row"><div class="sechead">Paketverlust (%)</div><span class="muted small">TCP = echter Verlust (Verbindungsaufbau musste wiederholt werden) · Ping = ICMP</span></div><div id="c2"></div></div>
     <div class="two" style="margin-bottom:14px"><div class="card"><div class="sechead">Ping (ms)</div><div id="c1"></div></div>
       <div class="card"><div class="sechead">TCP-Verbindungsaufbau (ms)</div><div id="c4"></div></div></div>
     <div class="card" style="margin-bottom:14px"><div class="sechead">Datenverkehr RX / TX (Mbit/s)</div><div id="c3"></div></div>
     <div class="card"><div class="sec-row"><div class="sechead">Ereignisse (7 Tage)</div><span class="muted small">Rot im Diagramm = keine Messung möglich (VM aus/eingefroren)</span></div><div id="ev"><div class="spinner"></div></div></div>
     <p class="muted small" style="margin-top:12px">Gemessen wird direkt auf dem Host (systemd-Dienst <code>ryvenpanel-netmon</code>). <b>TCP:</b> je Ziel 5 Verbindungsaufbauten zu Port 443 – dauert einer länger als 900 ms,
       musste das System ein verlorenes Paket neu senden (echter Verlust; Router filtern TCP nicht). <b>Ping:</b> je Ziel 5 ICMP-Pakete – Router beantworten Pings an sich selbst nur nachrangig,
       daher ist der Gateway-Wert nur bedingt aussagekräftig. Dazu Zähler der Netzwerkkarte, Gegenprobe des Minecraft-Ports und NTP-Status. Jede Messung ist per SHA-256 mit der vorherigen verkettet.</p>`;
  let stunden = 0.25;
  const live = () => stunden <= 0.25;
  seg($('#zr', el), [['0.25', '● Live 15 min'], ['1', '1 h'], ['6', '6 h'], ['24', '24 h'], ['168', '7 T'], ['720', '30 T']], '0.25', k => { stunden = Number(k); verlauf(); });

  async function zustand() {
    const d = await safe(() => netz('status'));
    if (!d) return;
    if (!d.letzte) { $('#nk', el).innerHTML = '<div class="empty">Noch keine Messungen.</div>'; return; }
    $('#stand', el).textContent = d.zeit.berlin + ' (' + d.zeit.utc + ')';
    const m = d.letzte, tcpV = d.tcp_extern_1min ?? 0, pingV = d.verlust_extern_1min ?? 0;
    const farbe = v => v >= 5 ? 'red' : v > 0 ? 'yellow' : 'green';
    const quote = d.erwartet_24h ? Math.min(100, d.messungen_24h / d.erwartet_24h * 100) : 100;
    $('#nk', el).innerHTML =
      kpi('pulse', d.laeuft ? 'green' : 'red', 'Monitor', d.laeuft ? '<span class="c-green-t">● live</span>' : '<span class="c-red-t">steht</span>', 'letzte Messung vor ' + d.alter + ' s') +
      kpi('alert', farbe(tcpV), 'Verlust TCP (1 min)', `<span class="c-${farbe(tcpV)}-t">${fmt(tcpV, 1)} %</span>`, 'echter Paketverlust, Ø der Ziele') +
      kpi('radar', farbe(pingV), 'Verlust Ping (1 min)', `<span class="c-${farbe(pingV)}-t">${fmt(pingV, 1)} %</span>`, 'ICMP, ohne Gateway') +
      kpi('chart', 'pink', 'RX / TX', netzMbit(m.rx_bps), 'TX ' + netzMbit(m.tx_bps) + ' · ' + esc(m.iface || '')) +
      kpi('flag', d.luecken_24h ? 'red' : 'green', 'Messlücken 24 h', `<span class="${d.luecken_24h ? 'c-red-t' : 'c-green-t'}">${d.luecken_24h}</span>`, d.luecken_24h ? 'zusammen ' + netzDauer(d.ausfall_24h_s) : 'Abdeckung ' + fmt(quote, 1) + ' %') +
      kpi('server', m.mc_ms != null ? 'green' : 'red', 'Minecraft-Port', m.mc_ms != null ? fmt(m.mc_ms, 1) + ' ms' : '<span class="c-red-t">zu</span>', 'TCP 25565 (Gegenprobe)') +
      kpi('lock', m.ntp === 1 ? 'green' : 'yellow', 'Uhrzeit', m.ntp === 1 ? '<span class="c-green-t">NTP-synchron</span>' : '<span class="c-yellow-t">nicht bestätigt</span>', 'Messung seit ' + (d.seit?.berlin || '–'));
    const b = v => v == null ? '<span class="muted">–</span>' : `<span class="badge ${farbe(v)}">${fmt(v, 1)} %</span>`;
    const ms = v => v == null ? '<span class="muted">–</span>' : fmt(v, 1) + ' ms';
    $('#nz', el).innerHTML = `<table class="t"><tr><th>Ziel</th><th>Adresse</th><th>Ping jetzt</th><th>Ping-Verlust 1 min</th><th>TCP jetzt</th><th>TCP-Verlust 1 min</th><th>TCP-Verlust 24 h</th><th>Ping Ø / max 24 h</th></tr>` +
      Object.entries(d.ziele).map(([k, [ip, name]]) => {
        const j = m.ping[k] || {}, t = m.tcp?.[k], h = d.h24[k] || {}, gw = k === 'gateway';
        return `<tr><td><b style="color:${NETZ_FARBE[k] || '#fff'}">●</b> ${esc(name)}${gw ? '<div class="muted small">nur Ping – Router drosseln Pings, Verlust nicht aussagekräftig</div>' : ''}</td><td class="muted">${esc(ip)}</td>
          <td>${j.rtt != null ? ms(j.rtt) : '<span class="c-red-t">keine Antwort</span>'}</td><td>${gw ? `<span class="muted">${fmt(d.min1.ping[k]?.verlust ?? 0, 1)} %</span>` : b(d.min1.ping[k]?.verlust)}</td>
          <td>${gw ? '<span class="muted">–</span>' : t ? ms(t.rtt) + (t.neu ? ` <span class="c-yellow-t small">${t.neu}× neu gesendet</span>` : '') + (t.fehl ? ` <span class="c-red-t small">${t.fehl}× gescheitert</span>` : '') : '–'}</td>
          <td>${gw ? '<span class="muted">–</span>' : b(d.min1.tcp[k]?.verlust)}</td><td>${gw ? '<span class="muted">–</span>' : b(d.h24_tcp[k]?.verlust)}</td>
          <td>${ms(h.rtt)} / ${ms(h.max)}</td></tr>`;
      }).join('') + '</table>';
  }

  async function verlauf() {
    const d = await safe(() => netz('verlauf', { stunden }));
    if (!d) return;
    const t = d.punkte.map(p => p.t), leer = d.punkte.map(p => !!p.leer), ks = Object.keys(d.ziele), tk = Object.keys(d.tcp_ziele || {});
    const opt = { nachkomma: d.schritt > 60 ? 2 : 1 };
    netzChart($('#c2', el), [
      ...tk.map(k => ({ name: 'TCP ' + NETZ_NAME[k], farbe: NETZ_FARBE[k], werte: d.punkte.map(p => p.q?.[k]?.verlust ?? null) })),
      { name: 'Ping extern (höchster)', farbe: '#ff5c6c', strich: true, werte: d.punkte.map(p => p.p ? Math.max(0, ...ks.filter(k => k !== 'gateway').map(k => p.p[k]?.verlust ?? 0)) : null) },
    ], t, '%', leer, { ...opt, festMax: 20 });
    netzChart($('#c1', el), ks.map(k => ({ name: d.ziele[k][1], farbe: NETZ_FARBE[k] || '#fff', strich: k === 'gateway', werte: d.punkte.map(p => p.p?.[k]?.rtt ?? null) })), t, 'ms', leer, opt);
    netzChart($('#c4', el), tk.map(k => ({ name: NETZ_NAME[k], farbe: NETZ_FARBE[k], werte: d.punkte.map(p => p.q?.[k]?.rtt ?? null) })), t, 'ms', leer, opt);
    netzChart($('#c3', el), [{ name: 'RX (empfangen)', farbe: '#55e0ff', werte: d.punkte.map(p => p.rx == null ? null : p.rx / 1e6) },
      { name: 'TX (gesendet)', farbe: '#d966ff', werte: d.punkte.map(p => p.tx == null ? null : p.tx / 1e6) }], t, 'Mbit/s', leer, { nachkomma: 2 });
  }

  async function ereignisse() {
    const d = await safe(() => netz('ereignisse', { tage: 7 }));
    if (!d) return;
    const B = { luecke: ['red', 'Messausfall'], neustart: ['yellow', 'Neustart'], verlust: ['red', 'Paketverlust'] };
    $('#ev', el).innerHTML = d.ereignisse.length ? `<div class="tcard"><table class="t"><tr><th>Art</th><th>Von (Berlin)</th><th>Bis (Berlin)</th><th>Dauer</th><th>Beschreibung</th><th>UTC</th></tr>` +
      d.ereignisse.map(e => `<tr><td><span class="badge ${B[e.art][0]}">${B[e.art][1]}</span></td><td>${esc(e.von.berlin)}</td><td>${esc(e.bis.berlin)}</td><td>${netzDauer(e.sekunden)}</td>
        <td class="wrap">${esc(e.text)}</td><td class="muted small">${esc(e.von.utc)} – ${esc(e.bis.utc)}</td></tr>`).join('') + '</table></div>'
      : '<div class="empty">Keine Ausfälle oder Paketverluste in den letzten 7 Tagen.</div>';
  }

  $('#pr', el).onclick = async () => {
    const r = await safe(() => netz('pruefen'));
    if (r) toast(r.fehler ? `Achtung: ${r.fehler} von ${fmt(r.geprueft)} Messungen passen nicht zur Hash-Kette (ab ${r.erster_fehler?.berlin}).` : `Integrität ok: ${fmt(r.geprueft)} Messungen lückenlos verkettet.`, !!r.fehler);
  };
  $('#ex', el).onclick = async () => {
    const lokal = d => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    const jetzt = new Date();
    const r = await dialog('Messungen als CSV exportieren', `<div class="muted small" style="margin-bottom:10px">Für den Störungsbericht an den Hoster: alle Rohmessungen (Ping und TCP) mit UTC- und deutscher Zeit, Ausfall-Liste und Integritätsprüfung (max. 31 Tage).</div>
      <label class="small">Von<input class="field" type="datetime-local" name="von" value="${lokal(new Date(jetzt - 86400000))}"></label>
      <label class="small" style="margin-top:8px;display:block">Bis<input class="field" type="datetime-local" name="bis" value="${lokal(jetzt)}"></label>`, { ok: 'Exportieren' });
    if (!r) return;
    const von = Math.floor(new Date(r.von) / 1000), bis = Math.floor(new Date(r.bis) / 1000);
    status('Exportiere …');
    const x = await safe(() => netz('export', { von, bis }));
    status('Bereit');
    if (!x) return;
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([x.csv], { type: 'text/csv;charset=utf-8' }));
    a.download = x.dateiname; a.click();
    toast(fmt(x.zeilen) + ' Messungen exportiert.');
  };

  await zustand(); verlauf(); ereignisse();
  // Live: Werte und Diagramme alle 10 s (= Messintervall); längere Zeiträume einmal pro Minute
  let takt = 0;
  timers.push(setInterval(() => {
    if (document.hidden || document.querySelector('.mbg')) return;
    takt++; zustand();
    if (live() || takt % 6 === 0) verlauf();
    if (takt % 6 === 0) ereignisse();
  }, 10000));
}

// ───────────────────────────── Datenschutz-Wächter ─────────────────────────────
// ───────────────────────────── Datenschutz: Löschungen ─────────────────────────────
/** Löschung eines Spielers mit einem Klick (+ Sicherheitsabfrage). Gleicher Ablauf wie /datenschutz freigeben im Spiel. */
async function dsLoeschen(spieler, antragId) {
  let v = null;
  if (!antragId) { v = await safe(() => mod('ds_vorschau', { spieler })); if (!v) return false; }
  const name = v ? v.spieler.name : spieler;
  const laeuft = v?.antrag?.status === 'laeuft';
  if (laeuft) { toast(`Für ${name} läuft bereits eine Löschung (#${v.antrag.id}).`, true); return false; }
  const r = await dialog(`Daten von ${name} löschen`, `
    <div class="c-red-t" style="font-weight:700;margin-bottom:8px">Endgültig – das lässt sich nicht rückgängig machen.</div>
    <div class="small" style="line-height:1.6">Gelöscht werden auf allen Servern: Datenbank-Einträge (Geld, Coins, Ränge, Plots, Homes, Statistiken …), Spielerdateien,
      Discord-Verknüpfung; Logs werden geschwärzt. Der Spieler wird getrennt und bis zum Abschluss gesperrt. Aktive Strafen bleiben bis zum Ablauf.</div>
    ${v ? `<div class="small" style="margin-top:10px">${v.discord ? '<span class="badge green">Discord verknüpft</span> Der Bot schickt die Datenauskunft vorher per DM.'
      : '<span class="badge yellow">Kein Discord</span> Es gibt keine Datenauskunft per DM – die Daten werden direkt gelöscht.'}</div>` : ''}
    ${v?.antrag ? `<div class="small muted" style="margin-top:6px">Offener Antrag #${v.antrag.id} wird damit freigegeben.</div>` : ''}
    <label class="check" style="margin-top:12px"><input type="checkbox" name="ok" required> Ich habe geprüft, dass die Löschung gewünscht bzw. rechtlich nötig ist.</label>`,
    { ok: 'Jetzt löschen', gefahr: true });
  if (!r || !r.ok) return false;
  status('Löschung wird gestartet …');
  const x = await safe(() => mod('ds_loeschen', antragId ? { id: antragId } : { spieler }));
  status('Bereit');
  if (x) toast(`Löschung #${x.id} für ${x.name} gestartet – die Server arbeiten sie jetzt ab.`);
  return !!x;
}

/** „Daten einsehen“ (Art. 15): Auskunft als Link per Discord-DM, per E-Mail oder zum Herunterladen. */
async function dsAuskunft(spieler) {
  const [v, st] = await Promise.all([safe(() => mod('ds_vorschau', { spieler })), safe(() => mod('ds_auskunft_status'))]);
  if (!v || !st) return false;
  const name = v.spieler.name;
  const dlg = dialog(`Daten einsehen – ${name}`, `
    <div class="small muted" style="margin-bottom:10px">Erstellt eine ZIP mit allen gespeicherten Daten (Datenbanken, Spielstände, Plugin-Daten, Logs, CoreProtect) – es wird nichts gelöscht. Auf Anfrage nur einzelne Kategorien.</div>
    <div style="margin-bottom:12px"><button type="button" class="btn" id="katl">${ic('eye')} Kategorien laden</button> <span class="muted small" id="katm">Ohne Auswahl werden alle Daten gesendet.</span>
      <div id="katbox" style="margin-top:8px"></div></div>
    <label class="check" style="margin-bottom:8px;${v.discord ? '' : 'opacity:.5'}"><input type="radio" name="weg" value="discord" ${v.discord ? 'checked' : 'disabled'}>
      <span><b>Link per Discord-DM</b> <span class="badge green">empfohlen</span><br><span class="muted small">${v.discord
        ? 'Der Bot schickt einen persönlichen Download-Link (7 Tage gültig). Identität ist über die /link-Verknüpfung belegt.'
        : 'Nicht möglich – kein Discord verknüpft.'}</span></span></label>
    <label class="check" style="margin-bottom:8px;${st.mail ? '' : 'opacity:.5'}"><input type="radio" name="weg" value="senden" ${st.mail ? (v.discord ? '' : 'checked') : 'disabled'}>
      <span><b>Per E-Mail</b><br><span class="muted small">${st.mail ? 'Absender ' + esc(st.absender) + ' · <b>vorher prüfen, dass die Adresse wirklich zum Spieler gehört!</b>' : 'Noch nicht eingerichtet (kein Postfach).'}</span></span></label>
    ${st.mail ? '<input class="field" type="email" name="email" placeholder="E-Mail-Adresse des Spielers" style="margin:0 0 10px 26px;width:calc(100% - 26px)">' : ''}
    <label class="check"><input type="radio" name="weg" value="download" ${!v.discord && !st.mail ? 'checked' : ''}>
      <span><b>Herunterladen</b><br><span class="muted small">ZIP hier im Browser speichern (z. B. um sie selbst zu übermitteln).</span></span></label>`, { ok: 'Auskunft erstellen', breit: true });
  const kb = [...document.querySelectorAll('.mbg')].pop();
  let kats = null;
  $('#katl', kb).onclick = async e => {
    const btn = e.currentTarget; btn.disabled = true; $('#katm', kb).textContent = 'Wird ermittelt – kann bis zu einer Minute dauern …';
    const k = await safe(() => mod('ds_auskunft_kategorien', { spieler: v.spieler.uuid }));
    btn.disabled = false;
    if (!k) { $('#katm', kb).textContent = 'Kategorien konnten nicht geladen werden.'; return; }
    kats = k.kategorien;
    $('#katm', kb).textContent = kats.length ? 'Nur die markierten Kategorien werden gesendet.' : 'Keine Daten gespeichert.';
    $('#katbox', kb).innerHTML = kats.length ? `<label class="check" style="margin-bottom:6px"><input type="checkbox" id="kata" checked><span><b>alle auswählen</b></span></label>` +
      kats.map(c => `<label class="check" style="margin-bottom:4px"><input type="checkbox" class="katc" name="kat_${esc(c.id)}" checked>
        <span><b>${esc(c.titel)}</b> <span class="badge muted">${fmt(c.anzahl)} Einträge · ${c.groesse >= 1048576 ? fmt(c.groesse / 1048576, 1) + ' MB' : fmt(c.groesse / 1024, 1) + ' KB'}</span><br><span class="muted small">${esc(c.beschreibung)}</span></span></label>`).join('') : '';
    const alle = $('#kata', kb), cs = () => [...kb.querySelectorAll('.katc')];
    if (alle) {
      alle.onchange = () => cs().forEach(c => c.checked = alle.checked);
      cs().forEach(c => c.onchange = () => { alle.checked = cs().every(x => x.checked); });
    }
  };
  const r = await dlg;
  if (!r) return false;
  const auswahl = kats && kats.length ? kats.filter(c => r['kat_' + c.id]).map(c => c.id) : null;
  if (auswahl && !auswahl.length) { toast('Bitte mindestens eine Kategorie auswählen.', true); return false; }
  if (r.weg === 'senden' && !(r.email || '').includes('@')) { toast('Bitte eine E-Mail-Adresse angeben.', true); return false; }
  status('Auskunft wird erstellt – das kann bis zu einer Minute dauern …');
  const x = await safe(() => mod('ds_auskunft', { spieler: v.spieler.uuid, modus: r.weg, email: r.email, ...(auswahl && auswahl.length < kats.length ? { kategorien: auswahl } : {}) }));
  status('Bereit');
  if (!x) return false;
  if (r.weg === 'download') {
    const bin = Uint8Array.from(atob(x.zip), c => c.charCodeAt(0));
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([bin], { type: 'application/zip' })); a.download = x.dateiname; a.click();
    toast(`Auskunft für ${name}: ${fmt(x.dateien)} Einträge, ${fmt(x.groesse / 1048576, 1)} MB heruntergeladen.`);
  } else toast(r.weg === 'discord' ? `Download-Link an ${name} per Discord geschickt (gültig bis ${new Date(x.ablauf * 1000).toLocaleString('de-DE')}).` : `Auskunft per Mail an ${x.email} geschickt.`);
  return true;
}

async function dsAuskunftTab(dv) {
  const darf = can('datenschutz.auskunft');
  dv.innerHTML = `${darf ? `<form class="tools" id="neu"><input class="field" name="spieler" placeholder="Spielername oder UUID" required style="max-width:260px">
      <button class="btn primary">${ic('eye')} Daten einsehen …</button><span class="muted small" id="mst"></span></form>` : ''}
    <div class="tcard" id="at"><div class="spinner"></div></div>
    <p class="muted small" style="margin-top:10px">Auskunft nach Art. 15 DSGVO. Bevorzugt per Discord-DM: Die Verknüpfung entsteht ingame über /link und belegt so, dass der Discord-Account zum Minecraft-Account gehört.
      Links sind 7 Tage gültig und werden danach automatisch gelöscht. Per E-Mail nur senden, wenn ihr geprüft habt, dass die Adresse dem Spieler gehört.</p>`;
  const KAT = { konto: 'Konto', wirtschaft: 'Wirtschaft', moderation: 'Moderation', support: 'KI-Support', chat: 'Chat & Befehle', verbindungen: 'Verbindungen', spielstand: 'Spielstand', bauprotokoll: 'Bauprotokoll', plots: 'Plots', sonstiges: 'Sonstiges' };
  const W = { discord: ['green', 'Discord-Link'], senden: ['cyan', 'E-Mail'], download: ['muted', 'Heruntergeladen'] };
  async function laden() {
    const d = await safe(() => mod('ds_auskunft_status'));
    if (!d) return;
    const m = $('#mst', dv);
    if (m) m.innerHTML = d.mail ? 'Mail: ' + esc(d.absender) : 'E-Mail-Versand noch nicht eingerichtet';
    $('#at', dv).innerHTML = d.verlauf.length ? `<table class="t"><tr><th>Zeit</th><th>Spieler</th><th>Weg</th><th>An</th><th>Kategorien</th><th>Umfang</th><th>Von</th><th>Gültig bis</th></tr>` +
      d.verlauf.map(e => `<tr><td class="small">${zeit(e.zeit)}</td><td><b>${esc(e.spieler)}</b></td>
        <td><span class="badge ${e.fehler ? 'red' : W[e.modus]?.[0] || 'muted'}">${e.fehler ? 'Fehler' : W[e.modus]?.[1] || esc(e.modus)}</span>${e.fehler ? `<div class="muted small">${esc(e.fehler)}</div>` : ''}</td>
        <td class="small">${esc(e.email || (e.modus === 'discord' ? 'Discord-DM' : '–'))}</td><td class="small">${Array.isArray(e.kategorien) ? e.kategorien.map(k => esc(KAT[k] || k)).join(', ') : 'alle'}</td><td class="small">${fmt(e.dateien)} Einträge · ${fmt(e.groesse / 1048576, 1)} MB</td>
        <td class="small">${esc(e.von)}</td><td class="small">${e.ablauf ? zeit(e.ablauf) + (e.ablauf * 1000 < Date.now() ? ' <span class="muted">(gelöscht)</span>' : '') : '–'}</td></tr>`).join('') + '</table>'
      : '<div class="empty">Noch keine Auskünfte erteilt.</div>';
  }
  const f = $('#neu', dv);
  if (f) f.onsubmit = async e => { e.preventDefault(); if (await dsAuskunft(new FormData(f).get('spieler').trim())) { f.reset(); laden(); } };
  await laden();
}

async function dsLoeschungen(dv) {
  const darf = can('datenschutz.loeschen');
  dv.innerHTML = `${darf ? `<form class="tools" id="neu"><input class="field" name="spieler" placeholder="Spielername oder UUID" required style="max-width:260px">
      <button class="btn red">${ic('alert')} Daten löschen …</button><span class="muted small">Spieler muss nicht online sein – kein Antrag nötig.</span></form>` : ''}
    <div class="tcard" id="lt"><div class="spinner"></div></div>
    <p class="muted small" style="margin-top:10px">Ablauf: Antrag → Freigabe → der Proxy exportiert die Daten (Discord-DM an den Spieler, falls verknüpft), löscht Datenbanken und schwärzt Logs,
      dann erledigt jeder Server seinen Teil. „Erledigt“ = alle Server fertig. Danach bleibt nur UUID + Zeitpunkte als Nachweis.</p>`;
  const S = { offen: ['yellow', 'Antrag offen'], laeuft: ['cyan', 'Läuft'], erledigt: ['green', 'Erledigt'], abgelehnt: ['muted', 'Abgelehnt'] };
  const E = { gesendet: ['green', 'per DM gesendet'], bereit: ['cyan', 'wird gesendet'], fehlgeschlagen: ['red', 'DM fehlgeschlagen'], keins: ['muted', 'kein Discord'] };
  async function laden() {
    const d = await safe(() => mod('ds_antraege'));
    if (!d) return;
    $('#lt', dv).innerHTML = d.antraege.length ? `<table class="t"><tr><th>#</th><th>Spieler</th><th>Status</th><th>Gestellt</th><th>Bearbeitet</th><th>Fortschritt</th><th>Auskunft</th><th></th></tr>` +
      d.antraege.map(a => `<tr><td class="muted">#${a.id}</td><td>${a.status === 'erledigt' ? '<span class="muted">gelöscht</span>' : `<b>${esc(a.name)}</b><div class="muted small brk">${esc(a.uuid)}</div>`}</td>
        <td><span class="badge ${S[a.status]?.[0] || 'muted'}">${S[a.status]?.[1] || esc(a.status)}</span>${a.grund ? `<div class="muted small">${esc(a.grund)}</div>` : ''}</td>
        <td class="small">${zeit(a.erstellt)}</td><td class="small">${a.handler ? esc(a.handler) + '<div class="muted">' + zeit(a.bearbeitet) + '</div>' : '<span class="muted">–</span>'}</td>
        <td class="small">${a.jobs ? `${a.jobs_fertig}/${a.jobs} Server${a.jobs_offen ? `<div class="muted">wartet: ${esc(a.jobs_offen)}</div>` : ''}` : a.status === 'laeuft' ? 'Proxy arbeitet …' : '<span class="muted">–</span>'}${a.fertig ? `<div class="muted">fertig ${zeit(a.fertig)}</div>` : ''}</td>
        <td>${a.export ? `<span class="badge ${E[a.export]?.[0] || 'muted'}">${E[a.export]?.[1] || esc(a.export)}</span>` : a.discord ? '<span class="muted small">Discord verknüpft</span>' : '<span class="muted small">kein Discord</span>'}</td>
        <td>${a.status === 'offen' && darf ? `<div class="btnrow tight"><button class="btn red" data-frei="${a.id}" data-name="${esc(a.name)}">Löschen</button><button class="btn" data-ab="${a.id}">Ablehnen</button></div>` : ''}</td></tr>`).join('') + '</table>'
      : '<div class="empty">Noch keine Löschanträge.</div>';
    $$('[data-frei]', dv).forEach(b => b.onclick = async () => { if (await dsLoeschen(b.dataset.name, Number(b.dataset.frei))) laden(); });
    $$('[data-ab]', dv).forEach(b => b.onclick = async () => {
      const r = await dialog(`Antrag #${b.dataset.ab} ablehnen`, '<label class="lab">Grund (sieht der Spieler)</label><input class="field" name="grund" required maxlength="200">', { ok: 'Ablehnen' });
      if (!r) return;
      if (await safe(() => mod('ds_ablehnen', { id: Number(b.dataset.ab), grund: r.grund }))) { toast('Antrag abgelehnt.'); laden(); }
    });
  }
  const f = $('#neu', dv);
  if (f) f.onsubmit = async e => { e.preventDefault(); if (await dsLoeschen(new FormData(f).get('spieler').trim())) { f.reset(); laden(); } };
  await laden();
  timers.push(setInterval(() => { if (!document.hidden && !document.querySelector('.mbg')) laden(); }, 10000));
}

async function dsView(el, args) {
  const reiter = ['waechter', 'loeschungen', 'auskunft', 'bericht', 'plugins', 'datenbanken', 'verzeichnis'].includes(args?.[0]) ? args[0] : 'waechter';
  el.innerHTML = kopf('Datenschutz & Compliance', 'Ausgehende Verbindungen, Plugins, Datenbanken und Verarbeitungsverzeichnis · Stand <span id="stand">…</span>',
      `<a class="btn" href="https://ryvenmc.de/datenschutz" target="_blank" rel="noopener">Datenschutzerklärung</a>`) +
    `<div style="margin-top:16px">${tabs('datenschutz', [['waechter', 'Verbindungs-Wächter'], ['loeschungen', 'Löschungen (DSGVO)'], ['auskunft', 'Auskunft (Daten einsehen)'], ['bericht', 'Bericht'], ['plugins', 'Plugins'], ['datenbanken', 'Datenbanken'], ['verzeichnis', 'Verarbeitungsverzeichnis']], reiter)}</div>
    <div id="dv"><div class="spinner"></div></div>`;
  const dv = $('#dv', el);
  if (reiter === 'waechter') return waechter(el, dv);
  if (reiter === 'loeschungen') { $('#stand', el).textContent = jetzt(); return dsLoeschungen(dv); }
  if (reiter === 'auskunft') { $('#stand', el).textContent = jetzt(); return dsAuskunftTab(dv); }
  if (reiter === 'bericht') { $('#stand', el).textContent = jetzt(); return dsBericht(dv); }
  const d = await safe(() => mod('plugins'));
  if (!d) { dv.innerHTML = '<div class="empty">Konnte nicht geladen werden.</div>'; return; }
  $('#stand', el).textContent = jetzt();
  if (reiter === 'plugins') return dsPlugins(dv, d);
  if (reiter === 'datenbanken') return dsDatenbanken(dv, d);
  return dsVerzeichnis(dv, d);
}

async function waechter(el, dv) {
  dv.innerHTML = `<div class="tools"><label class="check" style="margin:0"><input type="checkbox" id="auto" checked> Live (alle 5 s)</label><div class="seg" id="cf"></div></div>
    <div class="cards" id="zahlen"></div>
    <div class="sechead">Live – jetzt gerade offen (alle Container)</div><div class="tcard" style="margin-bottom:18px"><table class="t" id="live"></table></div>
    <div class="sechead">Verlauf – alles, was der Wächter je gesehen hat (wird jede Minute aufgezeichnet)</div><div class="tcard"><table class="t" id="hist"></table></div>
    <p class="muted small">Grün = bekannt und in der Datenschutzerklärung. Rot = unbekannt: der Discord-Bot meldet neue Ziele automatisch im Team-Kanal.
      Prüfen, welches Plugin das ist und ob Spielerdaten übertragen werden – dann Datenschutzerklärung ergänzen und als geprüft markieren, oder das Plugin abschalten.</p>`;
  const edit = can('datenschutz.edit');
  let filter = 'alle', d = null;
  const st = (b, neu) => b ? '<span class="badge green">bekannt</span>' : `<span class="badge red">${neu ? 'NEU – ungeprüft' : 'unbekannt'}</span>`;
  const fg = c => c.freigabe || c.grund ? `<span class="small"><b>${esc(c.freigabe || '–')}</b></span><br><span class="muted small">${esc(c.grund || '–')}</span>` : (c.bekannt ? '<span class="badge red">fehlt</span>' : '<span class="muted">–</span>');
  const knopf = (b, ip) => edit && !b ? `<button class="btn" data-ip="${esc(ip)}">als geprüft markieren</button>` : '';
  function zeigen() {
    if (!d) return;
    const liveF = d.live.filter(c => filter === 'alle' || (filter === 'mc' ? c.container === 'crafty_container' : c.container !== 'crafty_container'));
    const liveUnb = d.live.filter(c => !c.bekannt).length;
    const container = new Set(d.live.map(c => c.container || 'crafty_container'));
    $('#zahlen', dv).innerHTML = kpi('radar', 'cyan', 'Jetzt offen', fmt(d.live.length), `aus ${fmt(container.size)} Containern`) +
      kpi('alert', liveUnb ? 'red' : 'green', 'Davon unbekannt', `<span class="${liveUnb ? 'c-red-t' : 'c-green-t'}">${fmt(liveUnb)}</span>`, liveUnb ? 'bitte prüfen!' : 'alles bekannt') +
      kpi('check', 'green', 'Bekannte Ziele', fmt(d.bekannt), 'in der Datenschutzerklärung') +
      kpi('eye', d.unbekannt ? 'red' : 'accent', 'Unbekannte Ziele', `<span class="${d.unbekannt ? 'c-red-t' : ''}">${fmt(d.unbekannt)}</span>`, 'im Verlauf');
    $('#live', dv).innerHTML = '<tr><th>Server / Container</th><th>Ziel</th><th>Dienst</th><th>Verb.</th><th>Status</th><th>Datenschutz-Freigabe</th>' + (edit ? '<th></th>' : '') + '</tr>' +
      (liveF.map(c => `<tr><td><span class="sdot ${c.bekannt ? 'green' : 'red'}"></span><b>${esc(c.server_name)}</b>${c.container && c.container !== 'crafty_container' ? ' <span class="badge muted">Container</span>' : ''}</td>
        <td class="muted">${esc(c.ip)}:${c.port}</td><td class="wrap">${esc(c.dienst || '–')}</td><td>${c.verbindungen}</td><td>${st(c.bekannt, c.neu)}</td><td class="wrap">${fg(c)}</td>${edit ? `<td>${knopf(c.bekannt, c.ip)}</td>` : ''}</tr>`).join('')
        || `<tr><td colspan="7" class="empty">Gerade keine ausgehenden Verbindungen.</td></tr>`);
    $('#hist', dv).innerHTML = '<tr><th>Server / Container</th><th>Ziel</th><th>Dienst</th><th>Zertifikat / rDNS</th><th>zuerst</th><th>zuletzt</th><th>gesehen</th><th>Status</th><th>Datenschutz-Freigabe</th>' + (edit ? '<th></th>' : '') + '</tr>' +
      (d.verlauf.map(h => `<tr><td><b>${esc(h.server_name)}</b></td><td class="muted">${esc(h.ip)}:${h.port}</td><td class="wrap">${esc(h.dienst || '–')}</td>
        <td class="wrap muted small">${esc(h.zertifikat || h.rdns || '–')}</td><td>${zeit(h.zuerst)}</td><td>${zeit(h.zuletzt)}</td><td>${fmt(h.anzahl)}×</td>
        <td>${st(h.bekannt)}</td><td class="wrap">${fg(h)}</td>${edit ? `<td>${knopf(h.bekannt, h.ip)}</td>` : ''}</tr>`).join('') || `<tr><td colspan="10" class="empty">Noch nichts gesehen.</td></tr>`);
    $$('button[data-ip]', dv).forEach(b => b.onclick = async () => {
      const r = await dialog('Ziel als geprüft markieren', `<div class="muted small">Nur markieren, wenn der Dienst in der Datenschutzerklärung steht!</div>
        <label class="lab">Welcher Dienst ist ${esc(b.dataset.ip)}?</label><input class="field" name="dienst" required maxlength="120" placeholder="z. B. bStats – Plugin-Statistik">
        <label class="lab">Grund / Rechtsgrundlage (optional)</label><input class="field" name="grund" maxlength="200" placeholder="z. B. Art. 6 Abs. 1 lit. f – anonyme Statistik">`, { ok: 'Markieren' });
      if (!r) return;
      await safe(async () => { await mod('verbindung_ok', { ip: b.dataset.ip, dienst: r.dienst, grund: (r.grund || '').trim() || undefined }); toast(b.dataset.ip + ' als geprüft markiert.'); holen(); });
    });
  }
  async function holen() {
    try { d = await mod('verbindungen'); } catch (e) { if (e.message !== 'abgemeldet') $('#stand', el).textContent = e.message; return; }
    $('#stand', el).textContent = new Date(d.zeit * 1000).toLocaleTimeString('de-DE');
    zeigen();
  }
  seg($('#cf', dv), [['alle', 'Alle'], ['mc', 'Minecraft-Server'], ['andere', 'Andere Container']], filter, k => { filter = k; zeigen(); });
  await holen();
  timers.push(setInterval(() => { if ($('#auto', dv)?.checked && !document.hidden && !document.querySelector('.mbg')) holen(); }, 5000));
}

async function dsBericht(dv) {
  const b = await safe(() => mod('waechter_bericht'));
  if (!b) { dv.innerHTML = '<div class="empty">Bericht konnte nicht erstellt werden.</div>'; return; }
  const katFarbe = { Statistik: 'pink', Update: 'cyan', 'Lizenz/Update': 'cyan', Bedrock: 'green', Shop: 'yellow', Mojang: 'green', Discord: 'accent', 'Manuell geprüft': 'muted' };
  const proz = b.ips ? Math.round(b.cloudflare_ips / b.ips * 100) : 0;
  dv.innerHTML = `<div class="cards">
      ${kpi('radar', 'cyan', 'Gesehene Ziel-IPs', fmt(b.ips), `${fmt(b.eintraege)} Einträge im Verlauf`)}
      ${kpi('link', 'accent', 'Dahinter: Dienste', fmt(b.dienste.length), `${fmt(b.hosts)} Hostnamen erkannt`)}
      ${kpi('pulse', 'yellow', 'Über Cloudflare', proz + ' %', `${fmt(b.cloudflare_ips)} der IPs wechseln ständig`)}
      ${kpi('alert', b.unklar.length ? 'red' : 'green', 'Ungeklärt', `<span class="${b.unklar.length ? 'c-red-t' : 'c-green-t'}">${fmt(b.unklar.length)}</span>`, b.unklar.length ? 'Hostname noch nicht gesehen' : 'alles zugeordnet')}</div>
    <div class="card bericht">
      <h2>Warum tauchen ständig „neue“ IPs auf?</h2>
      <p><b>Kurz: Es sind fast immer dieselben Dienste – nur mit wechselnden Adressen.</b> ${proz} % der gesehenen Ziel-IPs gehören zu <b>Cloudflare</b>.
        Dienste wie bStats, CraftingStore, GeyserMC, Discord oder die Update-Server der Plugins liegen hinter Cloudflare; dort bekommt ein Server bei jeder
        DNS-Abfrage eine andere IP aus einem riesigen Adresspool. Der Wächter hat bisher nach <i>IP-Adresse</i> bewertet – deshalb war jede neue Cloudflare-IP für ihn ein „neues Ziel“.</p>
      <p><b>Was jetzt anders ist:</b> Der Dienst <code>ryvenpanel-sni</code> liest beim Verbindungsaufbau nur den <i>Hostnamen</i> mit (TLS-SNI bzw. HTTP-Host – keine Inhalte, keine Spielerdaten).
        Der Wächter bewertet Ziele jetzt nach Hostname: Steht der Dienst in der Datenschutzerklärung, wird eine neue IP automatisch als bekannt eingeordnet – egal, welche Cloudflare-Adresse gerade dran ist.
        Gemeldet werden nur noch wirklich neue Hostnamen. ${b.sni_aktiv ? '<span class="badge green">Mitschnitt aktiv</span>' : '<span class="badge yellow">Mitschnitt noch ohne aktuelle Daten</span>'}</p>
      <p><b>Votifier / Vote-Seiten:</b> Das ist <i>eingehender</i> Verkehr – die Vote-Seiten verbinden sich zu uns (Port 8195) und melden Votes${b.votes_heute ? ` (heute ca. ${fmt(b.votes_heute)} Log-Zeilen zu Votes)` : ''}.
        Der Wächter zählt nur Verbindungen, die <i>unsere</i> Server nach außen aufbauen – Votes erzeugen dort keine Einträge und sind völlig normal.</p>
      <p><b>Woher die ausgehenden Verbindungen stammen:</b> ${Object.entries(b.kategorien).sort((x, y) => y[1] - x[1]).map(([k, n]) => `<span class="badge ${katFarbe[k] || 'muted'}">${esc(k)}: ${n} IPs</span>`).join(' ')}
        – überwiegend anonyme Plugin-Statistiken und Update-Prüfungen, dazu Webshop, Bedrock-Unterstützung, Mojang-Profile und unser Discord-Bot.</p>
    </div>
    <div class="sechead">Dienste hinter den IPs</div>
    <div class="tcard" style="margin-bottom:18px"><table class="t"><tr><th>Dienst</th><th>Art</th><th>Hostnamen</th><th>IPs</th><th>davon Cloudflare</th><th>Server</th></tr>
      ${b.dienste.map(d => `<tr><td><b>${esc(d.dienst)}</b></td><td><span class="badge ${katFarbe[d.kategorie] || 'muted'}">${esc(d.kategorie)}</span></td>
        <td class="wrap small">${esc(d.hosts.join(', ') || '–')}</td><td>${fmt(d.ips)}</td><td>${fmt(d.cloudflare)}</td><td class="wrap small">${esc(d.server.join(', '))}</td></tr>`).join('')}</table></div>
    <div class="sechead">Noch ungeklärt</div>
    <div class="tcard"><table class="t"><tr><th>Ziel</th><th>Server</th><th>Hostname</th><th>Netz</th><th>zuerst</th></tr>
      ${b.unklar.map(u => `<tr><td class="muted">${esc(u.ip)}:${u.port}</td><td><b>${esc(u.server)}</b></td><td>${esc(u.host || u.rdns || '– (wird beim nächsten Aufruf erkannt)')}</td>
        <td>${u.cloudflare ? '<span class="badge yellow">Cloudflare</span>' : '<span class="badge muted">anderes</span>'}</td><td>${zeit(u.zuerst)}</td></tr>`).join('') || '<tr><td colspan="5" class="empty">Alles zugeordnet.</td></tr>'}</table></div>`;
}

const statusBadge = s => ({ offen: '<span class="badge red">nicht dokumentiert</span>', 'geändert': '<span class="badge yellow">Version geändert</span>', dokumentiert: '<span class="badge green">dokumentiert</span>' })[s];
const groesse = b => b >= 1048576 ? fmt(b / 1048576, 1) + ' MB' : fmt(b / 1024) + ' KB';

function dsPlugins(dv, d) {
  const P = d.plugins;
  const z = s => P.filter(p => p.status === s).length;
  dv.innerHTML = `<div class="cards">
      ${kpi('box', 'cyan', 'Plugins', fmt(P.length), `auf ${fmt(new Set(P.flatMap(p => p.server.map(s => s.server))).size)} Servern`)}
      ${kpi('alert', z('offen') ? 'red' : 'green', 'Nicht dokumentiert', `<span class="${z('offen') ? 'c-red-t' : 'c-green-t'}">${fmt(z('offen'))}</span>`, z('geändert') ? fmt(z('geändert')) + ' mit neuer Version' : 'Datenkategorien fehlen')}
      ${kpi('radar', 'pink', 'Mit Telemetrie', fmt(P.filter(p => p.telemetrie.length).length), 'bStats, Update-Prüfung u. a.')}
      ${kpi('link', 'accent', 'Mit externen Adressen', fmt(P.filter(p => p.hosts.length).length), 'laut Config')}
      ${kpi('server', 'green', 'Mit MariaDB', fmt(P.filter(p => p.datenbanken.length).length), 'eigene Datenbank')}</div>
    <div class="tools"><input class="field" id="q" placeholder="Plugin, Entwickler, Datenbank …"><div class="seg" id="pf"></div></div>
    <div class="split wide-r"><div class="plist big" id="pl"></div><div class="card pane" id="pd"><div class="empty">← Plugin auswählen<br><span class="muted small">Dann siehst du Version, Entwickler, Datenbanken, externe Adressen und dokumentierst die verarbeiteten Daten.</span></div></div></div>`;
  let filter = 'alle', wahl = null;
  seg($('#pf', dv), [['alle', 'Alle'], ['offen', 'Offen'], ['extern', 'Extern'], ['db', 'Datenbank'], ['ok', 'Dokumentiert']], filter, k => { filter = k; liste(); });
  function liste() {
    const q = $('#q', dv).value.toLowerCase();
    const l = P.filter(p => (!q || [p.name, p.autoren.join(' '), p.datenbanken.join(' '), p.hosts.join(' ')].join(' ').toLowerCase().includes(q)) &&
      (filter === 'alle' || (filter === 'offen' && p.status !== 'dokumentiert') || (filter === 'extern' && p.extern) || (filter === 'db' && p.datenbanken.length) || (filter === 'ok' && p.status === 'dokumentiert')));
    $('#pl', dv).innerHTML = l.map(p => `<button class="prow ${p.id === wahl ? 'on' : ''}" data-p="${esc(p.id)}">
        <span class="sdot ${p.status === 'dokumentiert' ? 'green' : p.status === 'geändert' ? 'yellow' : 'red'}"></span>
        <div class="pn"><b>${esc(p.name)}</b><span class="muted small">${esc(p.versionen.join(', '))} · ${esc(p.server.map(s => s.server).join(', '))}</span></div>
        <div class="pv">${p.telemetrie.length ? '<span class="badge pink">Telemetrie</span>' : ''}${p.datenbanken.length ? '<span class="badge cyan">DB</span>' : ''}</div></button>`).join('')
      || '<div class="empty">Keine Plugins.</div>';
    $$('[data-p]', dv).forEach(b => b.onclick = () => { wahl = b.dataset.p; liste(); detail(); });
  }
  function detail() {
    const p = P.find(x => x.id === wahl), pd = $('#pd', dv);
    if (!p) return;
    const doc = p.doku || {}, vor = p.vorschlag || {};
    const kat = doc.kategorien || vor.kategorien || [];
    const edit = can('datenschutz.edit');
    pd.innerHTML = `<div class="rp-head"><div><div class="muted small">${esc(p.art)} · ${esc(p.autoren.join(', ') || 'Entwickler unbekannt')}</div><h2>${esc(p.name)}</h2>
        ${p.website ? `<a class="small" href="${esc(/^https?:\/\//.test(p.website) ? p.website : 'https://' + p.website)}" target="_blank" rel="noopener noreferrer">${esc(p.website)}</a>` : ''}</div>${statusBadge(p.status)}</div>
      ${p.beschreibung ? `<p class="muted small" style="margin:6px 0 0">${esc(p.beschreibung)}</p>` : ''}
      <div class="sechead" style="margin-top:16px">Installiert auf</div>
      <div class="chips">${p.server.map(s => `<span class="chip big"><b>${esc(s.server)}</b><span class="muted">v${esc(s.version)} · ${groesse(s.groesse)} · ${zeit(s.geaendert)}</span></span>`).join('')}</div>
      <div class="facts">
        <div><div class="sechead">Speicherung</div>${p.speicher.length ? p.speicher.map(x => `<span class="badge muted">${esc(x)}</span>`).join(' ') : '<span class="muted small">nichts erkannt</span>'}
          ${p.datenbanken.length ? `<div class="small" style="margin-top:6px">MariaDB: ${p.datenbanken.map(x => `<a href="#datenschutz/datenbanken">${esc(x)}</a>`).join(', ')}</div>` : ''}
          ${p.lokal.length ? `<div class="muted small" style="margin-top:6px">Lokale Dateien: ${p.lokal.map(x => esc(x.datei) + ' (' + groesse(x.groesse) + ', ' + esc(x.server) + ')').join(', ')}</div>` : ''}</div>
        <div><div class="sechead">Externe Verbindungen</div>
          ${p.telemetrie.map(t => `<span class="badge pink">${esc(t)}</span>`).join(' ') || ''}
          ${p.hosts.length ? `<div class="small brk" style="margin-top:6px">laut Config: ${p.hosts.map(esc).join(', ')}</div>` : (!p.telemetrie.length ? '<span class="muted small">keine erkannt</span>' : '')}</div>
      </div>
      <div class="sec-row"><div class="sechead">Datenschutz-Dokumentation</div>${doc.geprueft_am ? `<span class="muted small">geprüft von ${esc(doc.geprueft_von)} · ${zeit(doc.geprueft_am)} · v${esc(doc.version || '?')}</span>` : (p.vorschlag ? '<span class="badge yellow">Vorschlag – bitte prüfen</span>' : '')}</div>
      <form id="doc">
        <div class="katgrid">${d.kategorien.map(k => `<label class="perm"><input type="checkbox" name="k" value="${esc(k)}" ${kat.includes(k) ? 'checked' : ''} ${edit ? '' : 'disabled'}><span><b>${esc(k)}</b></span></label>`).join('')}</div>
        <div class="frow"><div><label class="lab">Zweck</label><input class="field" name="zweck" maxlength="300" value="${esc(doc.zweck || vor.zweck || '')}" ${edit ? '' : 'disabled'}></div>
          <div><label class="lab">Rechtsgrundlage</label><select class="field" name="rechtsgrundlage" ${edit ? '' : 'disabled'}>${['', 'Art. 6 Abs. 1 lit. b DSGVO (Vertrag / Spielbetrieb)', 'Art. 6 Abs. 1 lit. f DSGVO (berechtigtes Interesse)', 'Art. 6 Abs. 1 lit. a DSGVO (Einwilligung)', 'Art. 6 Abs. 1 lit. c DSGVO (rechtliche Pflicht)', 'keine personenbezogenen Daten']
            .map(x => `<option ${x === (doc.rechtsgrundlage || '') ? 'selected' : ''}>${esc(x)}</option>`).join('')}</select></div></div>
        <div class="frow"><div><label class="lab">Speicherdauer</label><input class="field" name="speicherdauer" maxlength="120" value="${esc(doc.speicherdauer || '')}" placeholder="z. B. bis zur Löschanfrage, 30 Tage" ${edit ? '' : 'disabled'}></div>
          <div><label class="lab">Übermittlung an Dritte</label><input class="field" name="extern" maxlength="300" value="${esc(doc.extern || [...p.telemetrie, ...p.hosts].join(', '))}" ${edit ? '' : 'disabled'}></div></div>
        <label class="lab">Notiz</label><input class="field" name="notiz" maxlength="600" value="${esc(doc.notiz || '')}" ${edit ? '' : 'disabled'}>
        ${edit ? `<div class="btnrow"><button class="btn primary">${ic('check')} Geprüft & speichern</button>${p.doku ? '<button type="button" class="btn red" id="dl">Doku entfernen</button>' : ''}</div>` : ''}
      </form>`;
    if (!edit) return;
    $('#doc', pd).onsubmit = async e => {
      e.preventDefault();
      const f = new FormData(e.target);
      const args = { id: p.id, version: p.versionen.join(', '), kategorien: f.getAll('k'), zweck: f.get('zweck'), rechtsgrundlage: f.get('rechtsgrundlage'),
        speicherdauer: f.get('speicherdauer'), extern: f.get('extern'), notiz: f.get('notiz') };
      if (await safe(async () => { await mod('plugin_doku', args); return true; })) {
        p.doku = { ...args, geprueft_von: me.username, geprueft_am: Date.now() / 1000 }; p.status = 'dokumentiert'; p.vorschlag = null;
        toast(p.name + ' dokumentiert.'); liste(); detail(); dsBadge();
      }
    };
    if ($('#dl', pd)) $('#dl', pd).onclick = async () => {
      if (!await dialog('Doku entfernen', `Dokumentation von ${esc(p.name)} löschen?`, { ok: 'Entfernen', gefahr: true })) return;
      if (await safe(async () => { await mod('plugin_doku', { id: p.id, loeschen: true }); return true; })) { p.doku = null; p.status = 'offen'; liste(); detail(); dsBadge(); }
    };
  }
  $('#q', dv).oninput = liste;
  liste();
}

function dsDatenbanken(dv, d) {
  const D = d.datenbanken;
  const ohne = D.filter(x => !x.plugins.length);
  dv.innerHTML = `<div class="cards">
      ${kpi('server', 'cyan', 'Datenbanken', fmt(D.length), groesse(D.reduce((a, x) => a + x.groesse, 0)) + ' insgesamt')}
      ${kpi('box', 'green', 'Tabellen', fmt(D.reduce((a, x) => a + x.tabellen, 0)), '≈ ' + fmt(D.reduce((a, x) => a + x.zeilen, 0)) + ' Zeilen')}
      ${kpi('pulse', 'accent', 'Aktive Zugriffe', fmt(D.reduce((a, x) => a + x.aktiv.reduce((b, y) => b + y.verbindungen, 0), 0)), 'offene DB-Verbindungen')}
      ${kpi('alert', ohne.length ? 'yellow' : 'green', 'Ohne Plugin', fmt(ohne.length), ohne.length ? 'evtl. Altlasten – prüfen/löschen' : 'alles zugeordnet')}</div>
    <div class="grid-acc">${D.map(x => `<div class="card"><div class="acc-top"><div class="ico c-cyan">${ic('server')}</div><div class="acc-name"><b>${esc(x.name)}</b>
        <div class="muted small">${groesse(x.groesse)} · ${fmt(x.tabellen)} Tabellen · ≈ ${fmt(x.zeilen)} Zeilen</div></div>${x.aktiv.length ? '<span class="badge green">in Benutzung</span>' : '<span class="badge muted">keine Verbindung</span>'}</div>
      <div class="acc-meta"><div><span class="muted small">Plugins</span><br>${x.plugins.map(esc).join(', ') || '<span class="c-yellow-t">keins erkannt</span>'}</div>
        <div><span class="muted small">Zugriffsrechte</span><br><span class="small">${x.benutzer.map(esc).join(', ') || '–'}</span></div>
        <div><span class="muted small">Jetzt verbunden</span><br><span class="small">${x.aktiv.map(a => esc(a.benutzer) + ' (' + a.verbindungen + ')').join(', ') || '–'}</span></div></div></div>`).join('')}</div>`;
}

function dsVerzeichnis(dv, d) {
  const P = d.plugins.filter(p => p.doku || p.vorschlag);
  const zeile = p => { const x = p.doku || p.vorschlag || {}; return { name: p.name, version: p.versionen.join(', '), server: p.server.map(s => s.server).join(', '),
    kat: (x.kategorien || []).join(', '), zweck: x.zweck || '', recht: x.rechtsgrundlage || '', dauer: x.speicherdauer || '', speicher: [...p.speicher, ...p.datenbanken].join(', '),
    extern: x.extern || [...p.telemetrie, ...p.hosts].join(', '), stand: p.doku ? 'geprüft ' + zeit(p.doku.geprueft_am) + ' (' + (p.doku.geprueft_von || '') + ')' : 'Vorschlag, ungeprüft' }; };
  const rows = P.map(zeile);
  dv.innerHTML = `<div class="tools"><button class="btn primary" id="csv">${ic('box')} Als CSV herunterladen</button><button class="btn" id="druck">Drucken / PDF</button>
      <span class="muted small">${fmt(d.plugins.filter(p => p.doku).length)} geprüft · ${fmt(d.plugins.filter(p => !p.doku && p.vorschlag).length)} nur Vorschlag · ${fmt(d.plugins.filter(p => !p.doku && !p.vorschlag).length)} ohne Angaben (nicht enthalten)</span></div>
    <div class="note-warn" style="margin-bottom:12px">Das Verzeichnis ist eine Arbeitsgrundlage aus den technischen Daten und euren Angaben – es ersetzt keine rechtliche Prüfung.</div>
    <div class="tcard" id="vz"><table class="t"><tr><th>Plugin</th><th>Datenkategorien</th><th>Zweck</th><th>Rechtsgrundlage</th><th>Speicherung</th><th>Dauer</th><th>Dritte</th><th>Stand</th></tr>
      ${rows.map(r => `<tr><td><b>${esc(r.name)}</b><div class="muted small">${esc(r.version)} · ${esc(r.server)}</div></td><td class="wrap small">${esc(r.kat || '–')}</td><td class="wrap small">${esc(r.zweck || '–')}</td>
        <td class="wrap small">${esc(r.recht || '–')}</td><td class="wrap small">${esc(r.speicher || '–')}</td><td class="wrap small">${esc(r.dauer || '–')}</td><td class="wrap small">${esc(r.extern || '–')}</td>
        <td class="small ${r.stand.startsWith('Vorschlag') ? 'c-yellow-t' : 'c-green-t'}">${esc(r.stand)}</td></tr>`).join('') || '<tr><td colspan="8" class="empty">Noch nichts dokumentiert – im Reiter „Plugins“ beginnen.</td></tr>'}</table></div>`;
  $('#csv', dv).onclick = () => {
    const kopfz = ['Plugin', 'Version', 'Server', 'Datenkategorien', 'Zweck', 'Rechtsgrundlage', 'Speicherung', 'Speicherdauer', 'Übermittlung an Dritte', 'Stand'];
    const q = v => '"' + String(v ?? '').replace(/"/g, '""') + '"';
    const csv = '﻿' + [kopfz, ...rows.map(r => [r.name, r.version, r.server, r.kat, r.zweck, r.recht, r.speicher, r.dauer, r.extern, r.stand])].map(z => z.map(q).join(';')).join('\r\n');
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    a.download = 'RyvenMC-Verarbeitungsverzeichnis-' + new Date().toISOString().slice(0, 10) + '.csv';
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
  };
  $('#druck', dv).onclick = () => window.print();
}

// Warn-Zähler in der Seitenleiste (unbekannte Ziele + nicht dokumentierte Plugins)
async function dsBadge() {
  if (!can('datenschutz.view')) return;
  try {
    const s = await mod('ds_status');
    const a = document.querySelector('a.nav[href="#datenschutz"]');
    if (!a) return;
    a.querySelector('.navbadge')?.remove();
    const n = (s.unbekannt || 0) + (s.undokumentiert || 0);
    if (n) a.insertAdjacentHTML('beforeend', `<span class="navbadge ${s.unbekannt ? 'red' : 'yellow'}" title="${s.unbekannt} unbekannte Ziele · ${s.undokumentiert} Plugins ohne Doku">${n > 99 ? '99+' : n}</span>`);
  } catch { }
}

// ───────────────────────────── Moderation: Verify (Discord-Verknüpfungen) ─────────────────────────────
async function verifyTab(v) {
  v.innerHTML = `<div class="tools"><input class="field" id="q" placeholder="Spieler, Discord-ID oder UUID …"><span class="muted small" id="pc"></span></div><div class="tcard"><table class="t" id="t"></table></div>`;
  async function liste() {
    const r = await safe(() => mod('links', { q: $('#q', v).value }));
    const rows = r?.links || [];
    $('#pc', v).textContent = r ? `${fmt(r.pending_codes)} offene /link-Codes` : '';
    $('#t', v).innerHTML = '<tr><th>Spieler</th><th>Discord-ID</th><th>Ränge</th><th>Verknüpft</th><th>Zuletzt online</th><th>Spielzeit</th></tr>' +
      (rows.map(x => `<tr><td><span class="sdot ${x.online ? 'green' : ''}"></span><a href="#moderation/spieler/${encodeURIComponent(x.name || '')}"><b>${esc(x.name)}</b></a></td>
        <td class="muted">${esc(x.discord_id)}</td><td class="wrap">${esc(x.groups || '–')}${x.synced !== x.groups ? ' <span class="badge yellow">wird synchronisiert</span>' : ''}</td>
        <td>${zeit(x.linked)}</td><td>${x.online ? '<span class="c-green-t">online</span>' : zeit(x.last_seen)}</td><td>${dauer(x.playtime)}</td></tr>`).join('')
        || '<tr><td colspan="6" class="empty">Keine Verknüpfungen.</td></tr>');
  }
  let t; $('#q', v).oninput = () => { clearTimeout(t); t = setTimeout(liste, 350); };
  await liste();
}

// ───────────────────────────── Wirtschaft: Shop & Crates ─────────────────────────────
function shopEco(v, d) {
  const s = d.shop || {};
  const items = (s.items || []).slice();
  v.innerHTML = `<div class="cards">
      ${kpi('box', 'green', 'Verkauft (Geld rein)', geld(s.sell), `${fmt(s.sell_n)} Verkäufe`, '', 'c-green-t')}
      ${kpi('card', 'red', 'Gekauft (Geld raus)', geld(s.buy), `${fmt(s.buy_n)} Käufe`, '', 'c-red-t')}
      ${kpi('pulse', 'yellow', 'Shop-Bilanz', geld((s.sell || 0) - (s.buy || 0)), 'positiv = Shop erzeugt Geld')}
      ${kpi('server', 'cyan', 'Shop-Server', esc(s.server || '–'), 'EconomyShopGUI')}</div>
    <div class="tools"><div class="seg" id="so"></div><input class="field" id="q" placeholder="Item suchen …"></div>
    <div class="tcard"><table class="t" id="t"></table></div>`;
  let sort = 'umsatz';
  function zeigen() {
    const q = $('#q', v).value.toLowerCase();
    const key = { umsatz: i => (i.sold_money || 0) + (i.bought_money || 0), verkauf: i => i.sold_money || 0, kauf: i => i.bought_money || 0 }[sort];
    const rows = items.filter(i => !q || (i.name + ' ' + i.material).toLowerCase().includes(q)).sort((a, b) => key(b) - key(a)).slice(0, 300);
    $('#t', v).innerHTML = '<tr><th>Item</th><th>Bereich</th><th>verkauft</th><th>Geld rein</th><th>gekauft</th><th>Geld raus</th><th>Spieler</th><th>Preis K / V</th></tr>' +
      (rows.map(i => `<tr><td><b>${esc(i.name || i.path)}</b></td><td class="muted">${esc(i.section)}</td><td>${fmt(i.sold)}</td><td class="c-green-t">${geld(i.sold_money)}</td>
        <td>${fmt(i.bought)}</td><td class="c-red-t">${geld(i.bought_money)}</td><td>${fmt(i.players)}</td>
        <td class="muted">${i.buy_price >= 0 ? geld(i.buy_price) : '–'} / ${i.sell_price >= 0 ? geld(i.sell_price) : '–'}${i.sell_price > i.buy_price && i.buy_price >= 0 ? ' <span class="badge red">!</span>' : ''}</td></tr>`).join('')
        || '<tr><td colspan="8" class="empty">Keine Shop-Bewegungen im Zeitraum.</td></tr>');
  }
  seg($('#so', v), [['umsatz', 'Umsatz'], ['verkauf', 'Verkäufe'], ['kauf', 'Käufe']], sort, k => { sort = k; zeigen(); });
  $('#q', v).oninput = zeigen;
  zeigen();
}

function cratesEco(v, d) {
  const c = d.crates || {};
  const list = c.list || [];
  const geldSum = list.reduce((a, x) => a + (x.money || 0), 0), opens = list.reduce((a, x) => a + (x.opens || 0), 0);
  v.innerHTML = `<div class="cards">
      ${kpi('key', 'accent', 'Öffnungen', fmt(opens), 'im Zeitraum')}
      ${kpi('plus', 'green', 'Ausgeschüttetes Geld', geld(geldSum), 'aus Geld-Gewinnen', '', 'c-green-t')}
      ${kpi('box', 'pink', 'Kisten', fmt(list.length), 'mit Öffnungen')}</div>
    <div class="two"><div><div class="sechead">Kisten</div><div class="tcard"><table class="t"><tr><th>Kiste</th><th>Öffnungen</th><th>Geld</th><th>Spieler</th><th>Top-Öffner</th></tr>
      ${list.map(x => `<tr><td><b>${esc(x.name)}</b></td><td>${fmt(x.opens)}</td><td class="c-green-t">${geld(x.money)}</td><td>${fmt(x.players)}</td>
        <td class="wrap muted small">${(x.top || []).map(t => esc(t[0]) + ' (' + fmt(t[1]) + ')').join(', ')}</td></tr>`).join('') || '<tr><td colspan="5" class="empty">Keine Öffnungen.</td></tr>'}</table></div></div>
    <div><div class="sechead">Häufigste Gewinne</div><div class="tcard"><table class="t"><tr><th>Gewinn</th><th>Kiste</th><th>Anzahl</th><th>Geld</th></tr>
      ${(c.rewards || []).slice(0, 60).map(r => `<tr><td><b>${esc(r.name)}</b></td><td class="muted">${esc(r.crate)}</td><td>${fmt(r.count)}</td><td>${r.money ? geld(r.money) : '–'}</td></tr>`).join('') || '<tr><td colspan="4" class="empty">–</td></tr>'}</table></div></div></div>`;
}

// ───────────────────────────── Nutzer & Ränge (LuckPerms, Money, Coins) ─────────────────────────────
const ohneFarben = s => String(s || '').replace(/\\\./g, '.').replace(/&#[0-9a-fA-F]{6}|&[0-9a-fk-orA-FK-OR]|§./g, '');
const ablauf = e => e ? 'bis ' + zeit(e) : 'dauerhaft';
const kontext = x => [x.server && x.server !== 'global' ? 'Server ' + x.server : '', x.world && x.world !== 'global' ? 'Welt ' + x.world : ''].filter(Boolean).join(' · ') || 'überall';
let nutzerServer = null;

async function nutzerView(el, args) {
  const reiter = ['spieler', 'raenge', 'protokoll'].includes(args?.[0]) ? args[0] : 'spieler';
  el.innerHTML = kopf('Nutzer & Ränge', 'Ränge, Permissions, Money und Coins · <span id="nsub">lädt …</span>',
      `<div class="seg" id="srv"></div><button class="btn icon" id="rf" title="Neu laden">${ic('refresh')}</button>`) +
    `<div style="margin-top:16px">${tabs('nutzer', [['spieler', 'Spieler'], ['raenge', 'Ränge'], ['protokoll', 'Protokoll']], reiter)}</div><div id="nv"><div class="spinner"></div></div>
    <div class="konsole" id="kons" hidden><div class="sechead" style="margin:0 0 6px">Antwort der Konsole</div><pre id="kout"></pre></div>`;
  const nv = $('#nv', el);
  let d = null, wahl = args?.[1] || null, rangWahl = args?.[0] === 'raenge' ? args?.[1] : null;
  const darfRang = can('users.ranks'), darfGeld = can('users.economy');

  async function laden() {
    const neu = await safe(() => mod('nutzer', { server: nutzerServer }));
    if (!neu) { nv.innerHTML = '<div class="empty">Daten konnten nicht geladen werden.</div>'; return; }
    d = neu;
    nutzerServer = d.server.id;
    seg($('#srv', el), d.servers.map(s => [s.id, s.name]), d.server.id, k => { nutzerServer = k; laden(); });
    $('#nsub', el).innerHTML = `${fmt(d.players.length)} Spieler · ${fmt(d.groups.length)} Ränge · Money & Coins von „${esc(d.server.name)}“ · ` +
      (d.server.running ? '<span class="c-green-t">Server läuft – Änderungen per Konsole</span>' : '<span class="c-yellow-t">Server aus – nur ansehen</span>');
    ({ spieler, raenge, protokoll })[reiter]();
  }
  async function ausfuehren(args, meldung) {
    const r = await safe(() => mod('nutzer_act', { server: nutzerServer, ...args }));
    if (!r) return false;
    $('#kons', el).hidden = false;
    $('#kout', el).innerHTML = (r.befehle || []).map(b => '<span class="c-accent-t">&gt; ' + esc(b) + '</span>').join('\n') + '\n' + esc((r.ausgabe || []).join('\n') || '(keine Ausgabe)');
    $('#kout', el).className = r.warnung ? 'c-yellow-t' : '';
    toast(r.warnung ? 'Bitte Antwort der Konsole prüfen.' : (meldung || 'Ausgeführt auf ' + r.server + '.'), r.warnung);
    return true;
  }
  const rangOptionen = (akt = '') => d.groups.map(g => `<option ${g.name === akt ? 'selected' : ''} value="${esc(g.name)}">${esc(g.name)} (Gewicht ${g.weight})</option>`).join('');
  const ctxFelder = `<div class="frow"><div><label class="lab">Nur auf Server (leer = überall)</label><input class="field" name="server_ctx" pattern="[A-Za-z0-9_\\-]{0,40}"></div>
    <div><label class="lab">Nur in Welt (leer = überall)</label><input class="field" name="world_ctx" pattern="[A-Za-z0-9_\\-]{0,40}"></div></div>`;

  // ── Spieler: Liste links, Akte rechts (bricht auf schmalen Bildschirmen um)
  function spieler() {
    nv.innerHTML = `<div class="split"><div class="card pane"><div class="tools"><input class="field" id="q" placeholder="Name oder UUID …"><div class="seg" id="flt"></div></div>
        <div class="muted small" id="pc" style="margin-bottom:6px"></div><div class="plist" id="pl"></div></div>
      <div class="card pane" id="pd"><div class="empty">← Spieler auswählen<br><span class="muted small">Dann verwaltest du hier Rang, Permissions, Money und Coins.</span></div></div></div>`;
    let filter = 'alle';
    seg($('#flt', nv), [['alle', 'Alle'], ['team', 'Team'], ['reich', 'Reichste'], ['coins', 'Coins']], filter, k => { filter = k; liste(); });
    function liste() {
      const q = $('#q', nv).value.toLowerCase();
      let l = d.players.filter(p => !q || p.name.toLowerCase().includes(q) || p.uuid.includes(q));
      if (filter === 'team') l = l.filter(p => p.weight >= 90);
      if (filter === 'reich') l = l.slice().sort((a, b) => (b.money || 0) - (a.money || 0));
      if (filter === 'coins') l = l.slice().sort((a, b) => (b.coins || 0) - (a.coins || 0));
      $('#pc', nv).textContent = `${fmt(l.length)} Spieler`;
      $('#pl', nv).innerHTML = l.slice(0, 400).map(p => `<button class="prow ${p.uuid === wahl ? 'on' : ''}" data-u="${p.uuid}">
          <div class="avatar sm">${esc(p.name[0].toUpperCase())}</div><div class="pn"><b>${esc(p.name)}</b><span class="muted small">${esc(p.rank)}</span></div>
          <div class="pv"><span>${p.money != null ? geld(p.money) : '–'}</span><span class="muted small">${p.coins != null ? fmt(p.coins) + ' Coins' : ''}</span></div></button>`).join('')
        || '<div class="empty">Keine Spieler.</div>';
      $$('[data-u]', nv).forEach(b => b.onclick = () => { wahl = b.dataset.u; history.replaceState(null, '', '#nutzer/spieler/' + wahl); liste(); akte(); });
    }
    $('#q', nv).oninput = liste;
    liste();
    if (wahl) akte();
  }

  async function akte() {
    const p = d.players.find(x => x.uuid === wahl);
    const pd = $('#pd', nv);
    if (!p) { pd.innerHTML = '<div class="empty">Spieler nicht gefunden.</div>'; return; }
    pd.innerHTML = '<div class="spinner"></div>';
    const x = await safe(() => mod('nutzer_detail', { server: nutzerServer, uuid: p.uuid }));
    if (!x || wahl !== p.uuid) return;
    const gw = n => (d.groups.find(g => g.name === n) || {}).weight ?? 0;
    pd.innerHTML = `<div class="acc-top"><div class="avatar">${esc(p.name[0].toUpperCase())}</div>
        <div class="acc-name"><b style="font-size:19px">${esc(p.name)}</b><div class="muted small brk">${esc(p.uuid)}</div></div>
        <span class="badge cyan">${esc(p.rank)}</span></div>
      <div class="btnrow"><a class="btn" href="#moderation/spieler/${encodeURIComponent(p.name)}">${ic('shield')} Spielerakte & Strafen</a>${can('datenschutz.auskunft') ? `<button class="btn" id="dsein">${ic('eye')} Daten einsehen</button>` : ''}${can('datenschutz.loeschen') ? `<button class="btn red" id="dsdel">${ic('alert')} Daten löschen (DSGVO)</button>` : ''}</div>
      <div class="mini-cards">
        <div class="mini"><div class="muted small">Money · ${esc(d.server.name)}</div><div class="mv c-green-t">${p.money != null ? geld(p.money) : '–'}</div>
          ${darfGeld ? `<div class="btnrow tight"><button class="btn" data-eco="money" data-op="set">Setzen</button><button class="btn" data-eco="money" data-op="give">${ic('plus')}</button><button class="btn" data-eco="money" data-op="take">${ic('minus')}</button></div>` : ''}</div>
        <div class="mini"><div class="muted small">Coins · PlayerPoints</div><div class="mv c-yellow-t">${p.coins != null ? fmt(p.coins) : '–'}</div>
          ${darfGeld && d.coins ? `<div class="btnrow tight"><button class="btn" data-eco="coins" data-op="set">Setzen</button><button class="btn" data-eco="coins" data-op="give">${ic('plus')}</button><button class="btn" data-eco="coins" data-op="take">${ic('minus')}</button></div>` : ''}</div>
      </div>
      <div class="sec-row"><div class="sechead">Ränge</div>${darfRang ? `<div class="btnrow tight"><button class="btn" id="rset">Rang setzen</button><button class="btn" id="radd">${ic('plus')} Gruppe</button><button class="btn" id="rtmp">${ic('plus')} Temporär</button></div>` : ''}</div>
      <div class="chips">${x.groups.sort((a, b) => gw(b.group) - gw(a.group)).map((g, i) => `<span class="chip big"><b>${esc(g.group)}</b><span class="muted">${esc(kontext(g))} · ${esc(ablauf(g.expiry))}</span>${darfRang ? `<button data-rg="${i}" title="Entfernen">✕</button>` : ''}</span>`).join('') || '<span class="muted small">nur default</span>'}</div>
      <div class="sec-row"><div class="sechead">Eigene Permissions (${x.perms.length})</div>${darfRang ? `<div class="btnrow tight"><button class="btn" id="padd">${ic('plus')} Permission</button><button class="btn" id="ptmp">${ic('plus')} Temporär</button></div>` : ''}</div>
      ${x.perms.length ? `<div class="tcard"><table class="t"><tr><th>Permission</th><th>Wert</th><th>Gilt</th><th>Ablauf</th><th></th></tr>${x.perms.map((r, i) => `<tr><td class="brk"><code>${esc(r.perm)}</code></td>
        <td>${r.value ? '<span class="badge green">true</span>' : '<span class="badge red">false</span>'}</td><td class="muted small">${esc(kontext(r))}</td><td class="muted small">${esc(ablauf(r.expiry))}</td>
        <td>${darfRang ? `<button class="btn icon" data-pt="${i}" title="Wert umschalten">⇄</button> <button class="btn icon red" data-pu="${i}" title="Entfernen">✕</button>` : ''}</td></tr>`).join('')}</table></div>` : '<div class="muted small">Keine eigenen Permissions – alles kommt über die Ränge.</div>'}
      <div class="sechead" style="margin-top:18px">Verlauf</div>
      <div class="timeline">${x.log.slice(0, 40).map(l => `<div class="tl"><span class="badge ${l.art === 'Coins' ? 'yellow' : 'muted'}">${esc(l.art)}</span><div><div>${esc(l.text)}</div><div class="muted small">${zeit(l.time)} · ${esc(l.actor || '')}</div></div></div>`).join('') || '<div class="muted small">Keine Einträge.</div>'}</div>`;
    const nochmal = async ok => { if (ok) { await new Promise(r => setTimeout(r, 600)); await laden(); } };
    const dsb = $('#dsdel', pd); if (dsb) dsb.onclick = () => dsLoeschen(p.uuid);
    const dse = $('#dsein', pd); if (dse) dse.onclick = () => dsAuskunft(p.uuid);
    $$('[data-eco]', pd).forEach(b => b.onclick = async () => {
      const art = b.dataset.op, money = b.dataset.eco === 'money', akt = money ? p.money : p.coins;
      const r = await dialog(`${money ? 'Money' : 'Coins'} ${{ set: 'setzen', give: 'hinzufügen', take: 'abziehen' }[art]} – ${p.name}`,
        `<div class="muted small">Aktuell: ${money ? geld(akt) : fmt(akt)} · Kürzel möglich: 5k, 2.5m</div><label class="lab">Betrag</label><input class="field" name="b" required>`, { ok: 'Ausführen' });
      if (!r) return;
      const m = String(r.b).trim().toLowerCase().replace(',', '.').match(/^(\d+(?:\.\d+)?)([km]?)$/);
      if (!m) return toast('Ungültiger Betrag.', true);
      const betrag = Number(m[1]) * ({ k: 1e3, m: 1e6 }[m[2]] || 1);
      nochmal(await ausfuehren({ op: money ? 'user_money' : 'user_coins', uuid: p.uuid, name: p.name, art, amount: betrag }));
    });
    if (!darfRang) return;
    const rangDialog = async (op, titel, hinweis) => {
      const r = await dialog(titel + ' – ' + p.name, (hinweis ? `<div class="muted small">${hinweis}</div>` : '') +
        `<label class="lab">Rang</label><select class="field" name="group">${rangOptionen(p.rank)}</select>` +
        (op === 'user_addtemp' ? '<label class="lab">Dauer (z. B. 30d, 12h, 1mo, 1w2d)</label><input class="field" name="dur" value="30d" required pattern="(\\d+(mo|[smhdwy]))+">' : '') +
        (op !== 'user_set' ? ctxFelder : ''), { ok: 'Ausführen' });
      if (r) nochmal(await ausfuehren({ op, uuid: p.uuid, ...r }));
    };
    $('#rset', pd).onclick = () => rangDialog('user_set', 'Rang setzen', 'Ersetzt alle dauerhaften Ränge durch den gewählten (LuckPerms „parent set“).');
    $('#radd', pd).onclick = () => rangDialog('user_add', 'Gruppe hinzufügen');
    $('#rtmp', pd).onclick = () => rangDialog('user_addtemp', 'Temporärer Rang');
    $$('[data-rg]', pd).forEach(b => b.onclick = async () => {
      const g = x.groups[+b.dataset.rg];
      if (await dialog('Rang entfernen', `„${esc(g.group)}“ (${esc(kontext(g))}) von ${esc(p.name)} entfernen?`, { ok: 'Entfernen', gefahr: true }))
        nochmal(await ausfuehren({ op: 'user_remove', uuid: p.uuid, group: g.group, expiry: g.expiry, server_ctx: g.server, world_ctx: g.world }));
    });
    const permDialog = async temp => {
      const r = await dialog((temp ? 'Temporäre Permission – ' : 'Permission – ') + p.name,
        `<div class="muted small">Beispiele: essentials.fly · worldedit.* · false = ausdrücklich verbieten</div>
         <label class="lab">Permission</label><input class="field" name="perm" required pattern="-?[A-Za-z0-9_.\\-*:]{1,200}">
         <label class="lab">Wert</label><select class="field" name="value"><option>true</option><option>false</option></select>` +
        (temp ? '<label class="lab">Dauer (z. B. 1d, 12h, 30m)</label><input class="field" name="dur" value="1d" required pattern="(\\d+(mo|[smhdwy]))+">' : '') + ctxFelder, { ok: 'Setzen' });
      if (r) nochmal(await ausfuehren({ op: temp ? 'user_perm_settemp' : 'user_perm_set', uuid: p.uuid, ...r }));
    };
    $('#padd', pd).onclick = () => permDialog(false);
    $('#ptmp', pd).onclick = () => permDialog(true);
    $$('[data-pu]', pd).forEach(b => b.onclick = async () => {
      const r = x.perms[+b.dataset.pu];
      if (await dialog('Permission entfernen', `„${esc(r.perm)}“ von ${esc(p.name)} entfernen?`, { ok: 'Entfernen', gefahr: true }))
        nochmal(await ausfuehren({ op: 'user_perm_unset', uuid: p.uuid, perm: r.perm, expiry: r.expiry, server_ctx: r.server, world_ctx: r.world }));
    });
    $$('[data-pt]', pd).forEach(b => b.onclick = async () => {
      const r = x.perms[+b.dataset.pt];
      if (r.expiry) return toast('Temporäre Rechte bitte entfernen und neu setzen.', true);
      nochmal(await ausfuehren({ op: 'user_perm_set', uuid: p.uuid, perm: r.perm, value: !r.value, server_ctx: r.server, world_ctx: r.world }));
    });
  }

  // ── Ränge: Karten-Raster, Klick öffnet die Verwaltung des Rangs
  function raenge() {
    nv.innerHTML = `<div class="tools"><input class="field" id="gq" placeholder="Rang suchen …">${darfRang ? `<button class="btn primary" id="gneu">${ic('plus')} Rang anlegen</button>` : ''}</div>
      <div class="split wide-r"><div class="grid-rank" id="gl"></div><div class="card pane" id="gd"><div class="empty">← Rang auswählen</div></div></div>`;
    function liste() {
      const q = $('#gq', nv).value.toLowerCase();
      $('#gl', nv).innerHTML = d.groups.filter(g => !q || g.name.includes(q)).map(g => `<button class="rank-card ${g.name === rangWahl ? 'on' : ''}" data-g="${esc(g.name)}">
        <div class="rk-top"><b>${esc(g.name)}</b><span class="badge muted">${g.weight}</span></div>
        <div class="rk-prefix">${esc(ohneFarben(g.prefix)) || '<span class="muted">kein Prefix</span>'}</div>
        <div class="muted small">${fmt(g.perms)} Einträge${g.parents.length ? ' · erbt ' + esc(g.parents.join(', ')) : ''}</div></button>`).join('');
      $$('[data-g]', nv).forEach(b => b.onclick = () => { rangWahl = b.dataset.g; history.replaceState(null, '', '#nutzer/raenge/' + rangWahl); liste(); rang(); });
    }
    $('#gq', nv).oninput = liste;
    if ($('#gneu', nv)) $('#gneu', nv).onclick = async () => {
      const r = await dialog('Rang anlegen', '<label class="lab">Name (a–z, 0–9, _ -)</label><input class="field" name="group" required pattern="[a-z0-9_\\-]{1,36}">', { ok: 'Anlegen' });
      if (r && await ausfuehren({ op: 'grp_create', group: r.group })) { rangWahl = r.group; await new Promise(x => setTimeout(x, 600)); laden(); }
    };
    liste();
    if (rangWahl) rang();
  }

  async function rang() {
    const g = d.groups.find(x => x.name === rangWahl);
    const gd = $('#gd', nv);
    if (!g) { gd.innerHTML = '<div class="empty">Rang nicht gefunden.</div>'; return; }
    gd.innerHTML = '<div class="spinner"></div>';
    const x = await safe(() => mod('rang_detail', { server: nutzerServer, group: g.name }));
    if (!x || rangWahl !== g.name) return;
    const eigene = x.perms.filter(r => !/^(weight|prefix|suffix|displayname|group)\./.test(r.perm));
    gd.innerHTML = `<div class="rp-head"><div><div class="muted small">Rang · ${fmt(x.members)} Spieler direkt</div><h2>${esc(g.name)}</h2><div class="rk-prefix">${esc(ohneFarben(g.prefix))}</div></div>
        ${darfRang ? `<div class="btnrow tight"><button class="btn" id="gw">Gewicht ${g.weight}</button><button class="btn" id="gp">Prefix</button>${g.name !== 'default' ? `<button class="btn red" id="gdel">${ic('ban')} Löschen</button>` : ''}</div>` : ''}</div>
      <div class="sec-row"><div class="sechead">Erbt von</div>${darfRang ? `<button class="btn" id="gpa">${ic('plus')} Eltern-Rang</button>` : ''}</div>
      <div class="chips">${g.parents.map(pa => `<span class="chip big"><b>${esc(pa)}</b>${darfRang ? `<button data-pa="${esc(pa)}" title="Entfernen">✕</button>` : ''}</span>`).join('') || '<span class="muted small">erbt von keinem Rang</span>'}</div>
      <div class="sec-row"><div class="sechead">Permissions (${eigene.length})</div><div class="btnrow tight"><input class="field" id="pq" placeholder="filtern …" style="max-width:220px">${darfRang ? `<button class="btn" id="gpadd">${ic('plus')} Permission</button>` : ''}</div></div>
      <div class="tcard"><table class="t" id="gpt"></table></div>`;
    const zeigen = () => {
      const q = $('#pq', gd).value.toLowerCase();
      $('#gpt', gd).innerHTML = '<tr><th>Permission</th><th>Wert</th><th>Gilt</th><th></th></tr>' + eigene.map((r, i) => [r, i]).filter(([r]) => !q || r.perm.toLowerCase().includes(q)).slice(0, 400).map(([r, i]) =>
        `<tr><td class="brk"><code>${esc(r.perm)}</code></td><td>${r.value ? '<span class="badge green">true</span>' : '<span class="badge red">false</span>'}</td>
         <td class="muted small">${esc(kontext(r))}${r.expiry ? ' · ' + esc(ablauf(r.expiry)) : ''}</td><td>${darfRang ? `<button class="btn icon red" data-gu="${i}" title="Entfernen">✕</button>` : ''}</td></tr>`).join('');
      $$('[data-gu]', gd).forEach(b => b.onclick = async () => {
        const r = eigene[+b.dataset.gu];
        if (await dialog('Permission entfernen', `„${esc(r.perm)}“ aus Rang ${esc(g.name)} entfernen?`, { ok: 'Entfernen', gefahr: true }))
          nachher(await ausfuehren({ op: 'grp_perm_unset', group: g.name, perm: r.perm, expiry: r.expiry, server_ctx: r.server, world_ctx: r.world }));
      });
    };
    const nachher = async ok => { if (ok) { await new Promise(r => setTimeout(r, 600)); await laden(); } };
    $('#pq', gd).oninput = zeigen;
    zeigen();
    if (!darfRang) return;
    $('#gw', gd).onclick = async () => {
      const r = await dialog('Gewicht – ' + g.name, '<div class="muted small">Höheres Gewicht = höherer Rang (bestimmt u. a. den Panel-Rang).</div><label class="lab">Gewicht</label><input class="field" name="weight" type="number" min="0" max="100000" value="' + g.weight + '">', { ok: 'Setzen' });
      if (r) nachher(await ausfuehren({ op: 'grp_weight', group: g.name, weight: r.weight }));
    };
    $('#gp', gd).onclick = async () => {
      const r = await dialog('Prefix – ' + g.name, '<div class="muted small">Farbcodes wie &amp;c oder &amp;#ff0000 sind erlaubt. Leer = Prefix entfernen.</div><label class="lab">Prefix</label><input class="field" name="prefix" maxlength="64" value="' + esc(g.prefix.replace(/\\\./g, '.')) + '"><label class="lab">Priorität</label><input class="field" name="prio" type="number" value="' + (g.weight || 100) + '">', { ok: 'Speichern' });
      if (r) nachher(await ausfuehren({ op: 'grp_prefix', group: g.name, prefix: r.prefix, prio: r.prio }));
    };
    if ($('#gdel', gd)) $('#gdel', gd).onclick = async () => {
      if (await dialog('Rang löschen', `Rang „${esc(g.name)}“ mit allen Permissions löschen? ${fmt(x.members)} Spieler verlieren ihn. Das kann nicht rückgängig gemacht werden.`, { ok: 'Löschen', gefahr: true })) {
        if (await ausfuehren({ op: 'grp_delete', group: g.name })) { rangWahl = null; await new Promise(r => setTimeout(r, 600)); laden(); }
      }
    };
    $('#gpa', gd).onclick = async () => {
      const r = await dialog('Eltern-Rang – ' + g.name, `<div class="muted small">${esc(g.name)} erbt dann alle Permissions des gewählten Rangs.</div><label class="lab">Rang</label><select class="field" name="parent">${rangOptionen()}</select>`, { ok: 'Hinzufügen' });
      if (r) nachher(await ausfuehren({ op: 'grp_parent_add', group: g.name, parent: r.parent }));
    };
    $$('[data-pa]', gd).forEach(b => b.onclick = async () => {
      if (await dialog('Eltern-Rang entfernen', `${esc(g.name)} erbt nicht mehr von „${esc(b.dataset.pa)}“?`, { ok: 'Entfernen', gefahr: true }))
        nachher(await ausfuehren({ op: 'grp_parent_remove', group: g.name, parent: b.dataset.pa }));
    });
    $('#gpadd', gd).onclick = async () => {
      const r = await dialog('Permission – Rang ' + g.name, `<label class="lab">Permission</label><input class="field" name="perm" required pattern="-?[A-Za-z0-9_.\\-*:]{1,200}">
        <label class="lab">Wert</label><select class="field" name="value"><option>true</option><option>false</option></select>` + ctxFelder, { ok: 'Setzen' });
      if (r) nachher(await ausfuehren({ op: 'grp_perm_set', group: g.name, ...r }));
    };
  }

  async function protokoll() {
    nv.innerHTML = `<div class="tools"><input class="field" id="lq" placeholder="Filtern (Spieler, Rang, Befehl) …"><div class="seg" id="la"></div></div><div class="tcard"><table class="t" id="lt"><tr><td><div class="spinner"></div></td></tr></table></div>`;
    const r = await safe(() => mod('nutzer_log', { server: nutzerServer }));
    if (!r) return;
    let art = 'alle';
    const zeigen = () => {
      const q = $('#lq', nv).value.toLowerCase();
      const rows = r.log.filter(l => (art === 'alle' || l.art === art) && (!q || [l.actor, l.target, l.text].join(' ').toLowerCase().includes(q))).slice(0, 300);
      $('#lt', nv).innerHTML = '<tr><th>Zeit</th><th>Art</th><th>Von</th><th>An</th><th>Änderung</th></tr>' + (rows.map(l =>
        `<tr><td>${zeit(l.time)}</td><td><span class="badge ${l.art === 'Coins' ? 'yellow' : 'muted'}">${esc(l.art)}</span></td><td><b>${esc(l.actor)}</b></td><td>${esc(l.target)}</td><td class="wrap muted small">${esc(l.text)}</td></tr>`).join('')
        || '<tr><td colspan="5" class="empty">Keine Einträge.</td></tr>');
    };
    seg($('#la', nv), [['alle', 'Alle'], ['LuckPerms', 'Ränge'], ['Coins', 'Coins']], art, k => { art = k; zeigen(); });
    $('#lq', nv).oninput = zeigen;
    zeigen();
  }

  $('#rf', el).onclick = laden;
  await laden();
}

// ───────────────────────────── Team & Zugänge (nur team.manage) ─────────────────────────────
const team = (cmd, args = {}) => api('POST', '/panel/team', { cmd, args }).then(j => j.data ?? j);

// Dialog im App-Stil (kein Browser-confirm): resolve(true/Formulardaten) oder null bei Abbruch
function dialog(titel, html, { ok = 'Speichern', gefahr = false, breit = false } = {}) {
  return new Promise(res => {
    const w = document.createElement('div');
    w.className = 'mbg';
    w.innerHTML = `<form class="mdl${breit ? ' wide' : ''}"><div class="mhead"><b>${esc(titel)}</b><button type="button" class="btn icon" data-x>✕</button></div>
      <div class="mbody">${html}</div><div class="merr c-red-t small"></div>
      <div class="mfoot"><button type="button" class="btn" data-x>Abbrechen</button><button class="btn ${gefahr ? 'red' : 'primary'}">${esc(ok)}</button></div></form>`;
    document.body.appendChild(w);
    const zu = v => { w.remove(); document.removeEventListener('keydown', esc_); res(v); };
    const esc_ = e => { if (e.key === 'Escape') zu(null); };
    document.addEventListener('keydown', esc_);
    $$('[data-x]', w).forEach(b => b.onclick = () => zu(null));
    w.onclick = e => { if (e.target === w) zu(null); };
    const f = $('form', w);
    f.onsubmit = e => { e.preventDefault(); const d = {}; new FormData(f).forEach((v, k) => d[k] = v); $$('input[type=checkbox]', f).forEach(c => d[c.name] = c.checked); zu(d); };
    w.fehler = t => { $('.merr', w).textContent = t; };
    setTimeout(() => $('input:not([type=checkbox]),select', w)?.focus(), 30);
  });
}
const neuesPasswort = () => {
  const z = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789', a = new Uint32Array(16);
  crypto.getRandomValues(a);
  let p = [...a].map(n => z[n % z.length]).join('');
  return /\d/.test(p) && /[a-z]/.test(p) && /[A-Z]/.test(p) ? p : neuesPasswort();
};

async function teamView(el, args) {
  const reiter = ['accounts', 'raenge', 'api', 'sitzungen', 'protokoll'].includes(args?.[0]) ? args[0] : 'accounts';
  el.innerHTML = kopf('Team & Zugänge', 'Accounts, Rechte, IP-Freigaben und Protokoll · Stand <span id="stand">…</span>',
      `<button class="btn primary" id="neu">${ic('plus')} Account anlegen</button><button class="btn icon" id="rf" title="Aktualisieren">${ic('refresh')}</button>`) +
    `<div class="cards" id="kz" style="margin-top:16px"></div><div id="anf"></div>` +
    tabs('team', [['accounts', 'Accounts'], ['raenge', 'Ränge & Rechte'], ['api', 'API-Schlüssel'], ['sitzungen', 'Sitzungen'], ['protokoll', 'Protokoll']], reiter) +
    `<div id="tv"><div class="spinner"></div></div>`;
  const tv = $('#tv', el);
  let st = null, rechteDirty = false;

  async function laden() {
    const neu = await safe(() => team('state'));
    if (!neu) return;
    st = neu;
    $('#stand', el).textContent = jetzt();
    const req = st.requests || [], users = st.users || [], ses = st.sessions || [];
    $('#kz', el).innerHTML = kpi('users', 'cyan', 'Accounts', fmt(users.length), `${users.filter(u => u.frozen).length} gesperrt`) +
      kpi('pulse', 'green', 'Aktive Sitzungen', fmt(ses.length), 'App und Browser') +
      kpi('alert', req.length ? 'red' : 'accent', 'Offene IP-Anfragen', `<span class="${req.length ? 'c-red-t' : ''}">${fmt(req.length)}</span>`, req.length ? 'bitte prüfen' : 'nichts offen') +
      kpi('shield', 'pink', 'Gateway', 'v' + esc(st.version), 'Team-Dienst');
    $('#anf', el).innerHTML = req.length ? `<div class="sechead">Offene IP-Anfragen</div><div class="grid-req">${req.map(r => {
      const u = users.find(x => x.username === r.username) || {};
      return `<div class="card req"><div class="rq-top"><span class="ico c-red">${ic('alert')}</span><div><b>${esc(r.username)}</b> möchte sich von einer neuen IP anmelden<div class="muted small">${zeit(r.time)}</div></div></div>
        <div class="ipbig">${esc(r.ip)}</div><div class="muted small">bisher erlaubt: ${esc(JSON.parse(u.ips || '[]').join(', ') || '–')}</div>
        <div class="btnrow"><button class="btn green" data-ip="approve" data-u="${esc(r.username)}" data-v="${esc(r.ip)}">${ic('check')} Freigeben</button><button class="btn red" data-ip="deny" data-u="${esc(r.username)}" data-v="${esc(r.ip)}">Ablehnen</button></div></div>`;
    }).join('')}</div>` : '';
    $$('button[data-ip]', $('#anf', el)).forEach(b => b.onclick = () => aktion('ip', { username: b.dataset.u, ip: b.dataset.v, action: b.dataset.ip },
      `${b.dataset.ip === 'approve' ? 'Freigeben' : 'Ablehnen'}: ${b.dataset.v} für ${b.dataset.u}?`, b.dataset.ip === 'deny'));
    if (reiter === 'raenge' && rechteDirty) return;   // ungespeicherte Häkchen nicht überschreiben
    ({ accounts, raenge, api, sitzungen, protokoll })[reiter]();
  }

  // ── Accounts als Karten (wachsen mit der Breite, nichts wird gequetscht)
  function accounts() {
    const users = st.users || [];
    tv.innerHTML = `<div class="tools"><input class="field" id="uq" placeholder="Account, Spieler oder IP suchen …"></div><div class="grid-acc" id="ga"></div>`;
    const zeigen = () => {
      const q = $('#uq', tv).value.toLowerCase();
      $('#ga', tv).innerHTML = users.filter(u => !q || [u.username, u.mc_name, u.role, u.ips, u.last_ip, u.note].join(' ').toLowerCase().includes(q)).map(u => {
        const ips = JSON.parse(u.ips || '[]'), ich = u.username === st.me;
        const live = (st.sessions || []).filter(s => s.username === u.username).length;
        const status = u.frozen ? '<span class="badge red">gesperrt</span>' : u.must_change ? '<span class="badge yellow">muss Passwort ändern</span>' : '<span class="badge green">aktiv</span>';
        return `<div class="card acc${u.frozen ? ' frozen' : ''}">
          <div class="acc-top"><div class="avatar">${esc((u.username || '?')[0].toUpperCase())}</div>
            <div class="acc-name"><b>${esc(u.username)}</b>${ich ? ' <span class="badge cyan">du</span>' : ''}<div class="muted small">${u.mc_name ? 'Minecraft: ' + esc(u.mc_name) : 'fester Rang'} · Rang <b class="c-accent-t">${esc(u.rank || u.role || '–')}</b></div></div>
            ${status} ${u.totp_on ? '<span class="badge green">2FA</span>' : '<span class="badge">kein 2FA</span>'}</div>
          <div class="acc-meta"><div><span class="muted small">Letzter Login</span><br>${u.last_login ? zeit(u.last_login) : '–'}</div>
            <div><span class="muted small">Sitzungen</span><br>${live ? `<span class="c-green-t">${live} aktiv</span>` : 'keine'}</div>
            <div><span class="muted small">Angelegt</span><br>${u.created ? zeit(u.created) : '–'}</div></div>
          ${u.note ? `<div class="muted small acc-note">${esc(u.note)}</div>` : ''}
          <div class="muted small" style="margin-top:12px">Freigegebene IPs</div>
          <div class="chips">${ips.map(ip => `<span class="chip">${esc(ip)}<button title="IP entfernen" data-rm="${esc(ip)}" data-u="${esc(u.username)}">✕</button></span>`).join('') || '<span class="muted small">keine – erste Anmeldung legt sie fest</span>'}
            <button class="chip add" data-addip="${esc(u.username)}">${ic('plus')} IP</button></div>
          <div class="btnrow">
            ${ich ? '' : `<button class="btn" data-edit="${esc(u.username)}">Bearbeiten</button>`}
            <button class="btn" data-pw="${esc(u.username)}">${ic('key')} Passwort</button>
            ${u.totp_on ? `<button class="btn" data-tfar="${esc(u.username)}">2FA zurücksetzen</button>` : ''}
            ${ich ? '' : `<button class="btn" data-ua="kick" data-u="${esc(u.username)}">Abmelden</button>`}
            ${ich ? '' : (u.frozen ? `<button class="btn green" data-ua="unfreeze" data-u="${esc(u.username)}">Entsperren</button>` : `<button class="btn red" data-ua="freeze" data-u="${esc(u.username)}">${ic('lock')} Sperren</button>`)}
            ${ich ? '' : `<button class="btn icon red" title="Account löschen" data-del="${esc(u.username)}">${ic('ban')}</button>`}
          </div></div>`;
      }).join('') || '<div class="empty">Keine Accounts gefunden.</div>';
      $$('[data-rm]', tv).forEach(b => b.onclick = () => aktion('ip', { username: b.dataset.u, ip: b.dataset.rm, action: 'remove' }, `IP ${b.dataset.rm} bei ${b.dataset.u} entfernen? Sitzungen von dort werden beendet.`, true));
      $$('[data-addip]', tv).forEach(b => b.onclick = () => ipHinzu(b.dataset.addip));
      $$('[data-ua]', tv).forEach(b => b.onclick = () => aktion('user', { username: b.dataset.u, action: b.dataset.ua },
        { kick: `${b.dataset.u} überall abmelden?`, freeze: `${b.dataset.u} sperren? Alle Sitzungen enden sofort.`, unfreeze: `${b.dataset.u} entsperren?` }[b.dataset.ua], b.dataset.ua !== 'unfreeze'));
      $$('[data-tfar]', tv).forEach(b => b.onclick = () => aktion('tfa_reset', { username: b.dataset.tfar }, `2FA von ${b.dataset.tfar} zurücksetzen? Alle Sitzungen enden; der Account kann 2FA neu einrichten.`, true, 'Zurücksetzen'));
      $$('[data-pw]', tv).forEach(b => b.onclick = () => passwort(b.dataset.pw));
      $$('[data-edit]', tv).forEach(b => b.onclick = () => bearbeiten(users.find(u => u.username === b.dataset.edit)));
      $$('[data-del]', tv).forEach(b => b.onclick = () => aktion('user', { username: b.dataset.del, action: 'delete' },
        `Account „${b.dataset.del}“ endgültig löschen? Das kann nicht rückgängig gemacht werden.`, true, 'Löschen'));
    };
    $('#uq', tv).oninput = zeigen;
    zeigen();
  }

  const rangFelder = (u = {}) => {
    const raenge = [...new Set([...Object.keys(st.roles || {}), ...(st.groups || [])])].sort();
    return `<label class="lab">Verknüpfung</label><div class="seg" id="art"><button type="button" data-k="mc" class="${u.role && !u.mc_name ? '' : 'on'}">Minecraft-Spieler</button><button type="button" data-k="rang" class="${u.role && !u.mc_name ? 'on' : ''}">Fester Rang</button></div>
      <div id="f-mc"><label class="lab">Minecraft-Name</label><input class="field" name="mc_name" value="${esc(u.mc_name || '')}" placeholder="z. B. RyvenDE"><div class="muted small" style="margin-top:4px">Der Rang kommt dann automatisch aus LuckPerms.</div></div>
      <div id="f-rang" hidden><label class="lab">Rang</label><select class="field" name="role">${raenge.map(r => `<option ${r === u.role ? 'selected' : ''}>${esc(r)}</option>`).join('')}</select></div>
      <label class="lab">Notiz (optional)</label><input class="field" name="note" maxlength="200" value="${esc(u.note || '')}">`;
  };
  const rangUmschalter = () => {
    const w = $$('.mbg').pop();
    const set = k => { $('#f-mc', w).hidden = k !== 'mc'; $('#f-rang', w).hidden = k !== 'rang'; w.dataset.art = k; };
    seg($('#art', w), [['mc', 'Minecraft-Spieler'], ['rang', 'Fester Rang']], $('#art .on', w)?.dataset.k || 'mc', set);
    $$('#art button', w).forEach(b => b.type = 'button');
    set($('#art .on', w)?.dataset.k || 'mc');
  };
  const verkn = (d, w) => w.dataset.art === 'rang' ? { role: d.role, mc_name: '' } : { mc_name: (d.mc_name || '').trim(), role: '' };

  async function neuerAccount() {
    const pw = neuesPasswort();
    const p = dialog('Account anlegen', `<label class="lab">Benutzername</label><input class="field" name="username" required pattern="[a-zA-Z0-9_.\\-]{3,32}" placeholder="3–32 Zeichen, a–z 0–9 _ . -">` +
      rangFelder() + `<label class="lab">Start-Passwort</label><div class="pwrow"><input class="field" name="password" value="${pw}" required minlength="8"><button type="button" class="btn" id="cp">Kopieren</button></div>
      <label class="check"><input type="checkbox" name="must_change" checked> muss beim ersten Login geändert werden</label>
      <div class="muted small" style="margin-top:8px">Die erste Anmeldung legt die feste IP fest. Passwort sicher übermitteln (nicht in öffentliche Kanäle).</div>`, { ok: 'Anlegen' });
    rangUmschalter();
    const w = $$('.mbg').pop();
    $('#cp', w).onclick = () => navigator.clipboard?.writeText($('[name=password]', w).value).then(() => toast('Passwort kopiert.'));
    const d = await p;
    if (!d) return;
    await speichern('user', { username: d.username.toLowerCase(), action: 'create', password: d.password, must_change: d.must_change, note: d.note, ...verkn(d, w) }, `Account ${d.username} angelegt.`);
  }
  async function bearbeiten(u) {
    const p = dialog('Account bearbeiten: ' + u.username, rangFelder(u) + `<div class="muted small" style="margin-top:10px">Speichern meldet ${esc(u.username)} überall ab, damit der neue Rang sofort gilt.</div>`);
    rangUmschalter();
    const w = $$('.mbg').pop();
    const d = await p;
    if (!d) return;
    await speichern('user', { username: u.username, action: 'update', note: d.note, ...verkn(d, w) }, 'Gespeichert.');
  }
  async function passwort(name) {
    const ich = name === st.me;
    const p = dialog('Passwort zurücksetzen: ' + name, `<label class="lab">Neues Passwort</label><div class="pwrow"><input class="field" name="password" value="${neuesPasswort()}" required minlength="8"><button type="button" class="btn" id="cp">Kopieren</button></div>
      <div class="muted small" style="margin-top:8px">${ich ? 'Du wirst danach abgemeldet und musst das Passwort beim nächsten Login ändern.' : esc(name) + ' wird abgemeldet und muss es beim nächsten Login ändern.'} Mindestens 8 Zeichen mit Groß-/Kleinbuchstaben und Zahl (oder 14+ Zeichen).</div>`, { ok: 'Zurücksetzen' });
    const w = $$('.mbg').pop();
    $('#cp', w).onclick = () => navigator.clipboard?.writeText($('[name=password]', w).value).then(() => toast('Passwort kopiert.'));
    const d = await p;
    if (!d) return;
    await speichern('user', { username: name, action: 'password', password: d.password }, 'Passwort zurückgesetzt – jetzt sicher übermitteln.');
  }
  async function ipHinzu(name) {
    const d = await dialog('IP freigeben für ' + name, `<label class="lab">IP-Adresse (IPv4 oder IPv6)</label><input class="field" name="ip" required pattern="[0-9a-fA-F:.]{3,45}">
      <div class="muted small" style="margin-top:6px">Bei IPv6 gilt automatisch das ganze /64-Netz des Anschlusses.</div>`, { ok: 'Freigeben' });
    if (d) await speichern('ip', { username: name, ip: d.ip.trim(), action: 'add' }, 'IP freigegeben.');
  }

  // ── Ränge & Rechte: eine Karte pro Rang, Rechte nach Bereich gruppiert
  function raenge() {
    const perms = st.perms || [], roles = JSON.parse(JSON.stringify(st.roles || {}));
    const alle = [...new Set([...Object.keys(roles), ...(st.groups || [])])];
    const prio = r => ['owner', 'admin', 'developer', 'moderator', 'supporter'].indexOf(r);
    alle.sort((a, b) => ((prio(a) + 1 || 99) - (prio(b) + 1 || 99)) || a.localeCompare(b));
    const bereiche = {};
    perms.forEach(([k, t]) => (bereiche[{ servers: 'Server', console: 'Server', server: 'Server', files: 'Dateien', economy: 'Wirtschaft', moderation: 'Moderation', datenschutz: 'Datenschutz', netzwerk: 'Verwaltung', team: 'Verwaltung' }[k.split('.')[0]] || 'Sonstiges'] ||= []).push([k, t]));
    let akt = alle.find(r => (roles[r] || []).length) || alle[0];
    tv.innerHTML = `<div class="rights"><div class="card rlist" id="rl"></div><div class="card" id="rp"></div></div>`;
    const liste = () => {
      $('#rl', tv).innerHTML = `<div class="sechead" style="margin-top:0">Ränge</div>` + alle.map(r => `<button class="rrow ${r === akt ? 'on' : ''}" data-r="${esc(r)}"><b>${esc(r)}</b><span class="muted small">${(roles[r] || []).length} Rechte</span></button>`).join('') +
        `<div class="muted small" style="margin-top:10px">Ränge kommen aus LuckPerms. Ohne Rechte hat der Rang keinen Panel-Zugang.</div>`;
      $$('[data-r]', tv).forEach(b => b.onclick = () => { akt = b.dataset.r; liste(); panel(); });
    };
    const panel = () => {
      const hat = new Set(roles[akt] || []);
      $('#rp', tv).innerHTML = `<div class="rp-head"><div><div class="muted small">Rechte für Rang</div><h2>${esc(akt)}</h2></div>
          <div class="btnrow" style="margin:0"><button class="btn" id="alle">Alle</button><button class="btn" id="keine">Keine</button><button class="btn primary" id="sp" ${rechteDirty ? '' : 'disabled'}>Speichern</button></div></div>
        <div class="pgrid">${Object.entries(bereiche).map(([b, l]) => `<div class="pgroup"><div class="sechead">${esc(b)}</div>${l.map(([k, t]) =>
          `<label class="perm"><input type="checkbox" data-p="${k}" ${hat.has(k) ? 'checked' : ''} ${akt === 'owner' && k === 'team.manage' ? 'disabled' : ''}><span><b>${esc(t)}</b><code>${k}</code></span></label>`).join('')}</div>`).join('')}</div>`;
      const sync = () => { roles[akt] = $$('[data-p]', tv).filter(c => c.checked).map(c => c.dataset.p); rechteDirty = true; $('#sp', tv).disabled = false; liste(); };
      $$('[data-p]', tv).forEach(c => c.onchange = sync);
      $('#alle', tv).onclick = () => { $$('[data-p]', tv).forEach(c => c.checked = true); sync(); };
      $('#keine', tv).onclick = () => { $$('[data-p]', tv).forEach(c => c.checked = c.disabled); sync(); };
      $('#sp', tv).onclick = async () => {
        if (!await dialog('Rechte speichern', 'Die neuen Rechte gelten sofort für alle angemeldeten Team-Mitglieder.', { ok: 'Speichern' })) return;
        const ok = await safe(async () => { await team('roles', { roles }); return true; });
        if (ok) { rechteDirty = false; toast('Rechte gespeichert.'); laden(); }
      };
    };
    liste(); panel();
  }

  // ── API-Schlüssel für Folgeprojekte (Bots, Tools, Websites)
  function api() {
    const keys = st.api_keys || [], ap = st.api_perms || [], namen = Object.fromEntries((st.perms || []).map(p => [p[0], p[1]]));
    const status = k => k.revoked ? '<span class="badge red">gesperrt</span>' : (k.expires && k.expires * 1000 < Date.now()) ? '<span class="badge yellow">abgelaufen</span>' : '<span class="badge green">aktiv</span>';
    tv.innerHTML = `<div class="card bericht"><h2>API für Folgeprojekte</h2>
        <p>Mit einem API-Schlüssel können eigene Projekte (Discord-Bots, Tools, Websites) die Panel-API nutzen – ohne Team-Passwort.
        Jeder Schlüssel hat <b>eigene Rechte</b> und funktioniert <b>nur von den eingetragenen IPs</b>. Team-Verwaltung ist per Schlüssel nie möglich.
        Aufruf: <code>Authorization: Bearer rpk_…</code> an <code>https://secure.ryvenmc.de</code> – Doku: <code>API.md</code> im Repo <b>RyvenMC/RyvenPanel-Web</b>.</p>
        <div class="btnrow"><button class="btn primary" id="apineu">${ic('plus')} API-Schlüssel anlegen</button></div></div>
      <div class="grid-acc">${keys.map(k => `<div class="card acc${k.revoked ? ' frozen' : ''}">
        <div class="acc-top"><div class="ico c-accent">${ic('key')}</div><div class="acc-name"><b>${esc(k.name)}</b>
          <div class="muted small"><code>${esc(k.prefix)}…</code> · angelegt von ${esc(k.created_by)} · ${zeit(k.created)}</div></div>${status(k)}</div>
        ${k.note ? `<div class="muted small acc-note">${esc(k.note)}</div>` : ''}
        <div class="acc-meta"><div><span class="muted small">Zuletzt benutzt</span><br>${k.last_used ? zeit(k.last_used) : 'noch nie'}<br><span class="muted small">${esc(k.last_ip || '')}</span></div>
          <div><span class="muted small">Aufrufe</span><br>${fmt(k.uses)}</div><div><span class="muted small">Läuft ab</span><br>${k.expires ? zeit(k.expires) : 'nie'}</div></div>
        <div class="muted small" style="margin-top:12px">Rechte</div><div class="chips">${JSON.parse(k.perms || '[]').map(p => `<span class="chip" title="${esc(namen[p] || '')}">${esc(p)}</span>`).join('')}</div>
        <div class="muted small" style="margin-top:10px">Erlaubte IPs</div><div class="chips">${JSON.parse(k.ips || '[]').map(ip => `<span class="chip">${esc(ip)}</span>`).join('')}</div>
        ${k.revoked ? '' : `<div class="btnrow"><button class="btn red" data-revoke="${esc(k.name)}">${ic('lock')} Sperren</button></div>`}</div>`).join('')
        || '<div class="empty">Noch keine API-Schlüssel.</div>'}</div>`;
    $$('[data-revoke]', tv).forEach(b => b.onclick = () => aktion('api_revoke', { name: b.dataset.revoke },
      `API-Schlüssel „${b.dataset.revoke}“ sperren? Projekte, die ihn nutzen, verlieren sofort den Zugriff.`, true, 'Sperren'));
    $('#apineu', tv).onclick = async () => {
      const gruppen = {};
      ap.forEach(p => (gruppen[p.split('.')[0]] ||= []).push(p));
      const r = await dialog('API-Schlüssel anlegen', `<label class="lab">Name des Projekts</label><input class="field" name="name" required pattern="[a-z0-9_\\-]{3,32}" placeholder="z. B. discord-statsbot">
        <label class="lab">Notiz (optional)</label><input class="field" name="note" maxlength="200" placeholder="wofür, wer betreut es">
        <label class="lab">Erlaubte IPs (eine pro Zeile – Server, auf dem das Projekt läuft)</label><textarea class="field" name="ips" rows="3" required placeholder="203.0.113.10"></textarea>
        <label class="lab">Gültig für Tage (0 = unbegrenzt)</label><input class="field" name="tage" type="number" min="0" max="3650" value="365">
        <label class="lab">Rechte – nur so viele wie nötig</label>
        <div class="katgrid">${ap.map(p => `<label class="perm"><input type="checkbox" name="perm_${esc(p)}" ${p === 'servers.view' ? 'checked' : ''}><span><b>${esc(namen[p] || p)}</b><code>${esc(p)}</code></span></label>`).join('')}</div>`,
        { ok: 'Anlegen', breit: true });
      if (!r) return;
      const perms = ap.filter(p => r['perm_' + p]);
      const res = await safe(() => team('api_create', { name: r.name.toLowerCase(), note: r.note, ips: r.ips.split(/[\s,]+/).filter(Boolean), tage: Number(r.tage), perms }));
      if (!res || !res.token) return;
      const fertig = dialog('Schlüssel erstellt – jetzt kopieren!', `<div class="note-warn" style="margin:8px 0">Der Schlüssel wird <b>nur dieses eine Mal</b> angezeigt. Sicher ablegen (z. B. Umgebungsvariable des Projekts), nie in Git oder Discord posten.</div>
        <div class="pwrow"><input class="field" id="tok" readonly value="${esc(res.token)}"><button type="button" class="btn" id="tcp">Kopieren</button></div>`, { ok: 'Fertig' });
      const tcp = document.getElementById('tcp');
      if (tcp) tcp.onclick = () => navigator.clipboard?.writeText(document.getElementById('tok').value).then(() => toast('Schlüssel kopiert.'));
      await fertig;
      laden();
    };
  }

  function sitzungen() {
    const ses = st.sessions || [];
    tv.innerHTML = `<div class="grid-acc">${ses.map(x => `<div class="card"><div class="acc-top"><div class="avatar">${esc((x.username || '?')[0].toUpperCase())}</div>
        <div class="acc-name"><b>${esc(x.username)}</b><div class="muted small">Rang ${esc(x.rank)}</div></div><span class="badge green">aktiv</span></div>
        <div class="acc-meta"><div><span class="muted small">IP</span><br><span class="brk">${esc(x.ip)}</span></div><div><span class="muted small">Zuletzt aktiv</span><br>${zeit(x.last_seen)}</div><div><span class="muted small">Angemeldet</span><br>${zeit(x.created)}</div><div><span class="muted small">Läuft ab</span><br>${zeit(x.expires)}</div></div></div>`).join('')
      || '<div class="empty">Keine aktiven Sitzungen.</div>'}</div>`;
  }

  function protokoll() {
    tv.innerHTML = `<div class="tools"><input class="field" id="aq" placeholder="Filtern (Name, Aktion, IP) …"></div><div class="tcard"><table class="t" id="aud"></table></div>`;
    const zeigen = () => {
      const q = $('#aq', tv).value.toLowerCase();
      const rows = (st.audit || []).filter(a => !q || [a.name, a.action, a.detail, a.ip, a.result].join(' ').toLowerCase().includes(q)).slice(0, 200);
      $('#aud', tv).innerHTML = '<tr><th>Zeit</th><th>Account</th><th>IP</th><th>Aktion</th><th>Details</th><th>Ergebnis</th></tr>' +
        (rows.map(a => { const bad = /abgelehnt|blockiert|Fehler|gesperrt|Verweigert/i.test((a.result || '') + ' ' + (a.action || ''));
          return `<tr><td>${zeit(a.time)}</td><td><b>${esc(a.name)}</b></td><td class="muted">${esc(a.ip)}</td><td>${esc(a.action)}</td><td class="wrap muted small">${esc(a.detail)}</td>
            <td class="${bad ? 'c-red-t' : ''}">${esc(a.result)}</td></tr>`; }).join('') || '<tr><td colspan="6" class="empty">Keine Einträge.</td></tr>');
    };
    $('#aq', tv).oninput = zeigen;
    zeigen();
  }

  async function speichern(cmd, args, meldung) {
    const ok = await safe(async () => { await team(cmd, args); return true; });
    if (ok) { toast(meldung); await laden(); }
    return ok;
  }
  async function aktion(cmd, args, frage, gefahr = false, okText = 'Bestätigen') {
    if (!await dialog('Bitte bestätigen', esc(frage), { ok: okText, gefahr })) return;
    await speichern(cmd, args, 'Erledigt.');
  }
  $('#neu', el).onclick = neuerAccount;
  $('#rf', el).onclick = laden;
  await laden();
  timers.push(setInterval(() => { if (!document.hidden && !document.querySelector('.mbg')) laden(); }, 20000));
}

laden();
