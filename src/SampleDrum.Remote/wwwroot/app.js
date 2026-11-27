// SampleDrum · painel do celular (módulo ES, carregado com type="module"). JavaScript puro, sem framework nem CDN.
// Regras: a seleção mostrada só muda por um `state` do PC (nunca otimista); nada é reenviado sozinho depois de
// reconectar; o token fica só no cookie HttpOnly (aqui guardamos apenas o nome do aparelho).

const PIECE_LABELS = { kick: 'Bumbo', snare: 'Caixa', tom: 'Tom', floorTom: 'Surdo' };
const PIECE_ORDER = ['kick', 'snare', 'tom', 'floorTom'];
const NAME_KEY = 'sampledrum.deviceName';
const DEFAULT_NAME = 'Celular do som';
const IDLE_PROBE_MS = 25000;   // sem nenhum evento SSE por esse tempo: confere se o PC ainda responde
const OFFLINE_AFTER_MS = 6000; // "Reconectando" vira "Sem conexão com o PC" depois disso
const ERROR_TEXT = {
  filenotfound: 'arquivo não encontrado',
  unsupportedformat: 'formato de WAV não suportado',
  toolong: 'sample longo demais',
  outsidelibrary: 'fora da pasta de samples',
  ioerror: 'erro ao ler o arquivo',
  invalidargument: 'pedido inválido',
  internal: 'erro interno no PC',
  samplerate: 'taxa de amostragem diferente',
};

const $ = (id) => document.getElementById(id);
const ui = {};

const app = {
  view: 'loading',
  state: null,
  samples: [],
  samplesPromise: null,
  presets: [],
  presetTouched: false,
  conn: 'connecting',
  es: null,
  freshConnection: true,
  lastEventAt: 0,
  retryCount: 0,
  retryTimer: 0,
  offlineTimer: 0,
  startTimer: 0,
  probing: false,
  ops: new Map(),        // clientOpId -> registro da operação deste aparelho
  latest: {},            // alvo ('kit' ou peça) -> clientOpId do pedido mais recente
  status: {},            // alvo -> { text, kind, timer }
  cards: {},             // peça -> elementos do cartão
  shown: {},             // peça -> sample mostrado (segurado enquanto há pedido deste aparelho em andamento)
  shownKit: undefined,
  lastAuthCheck: 0,
  picker: null,          // { piece, baseVersion }
  confirmResolve: null,
  toastTimer: 0,
};

// utilidades

function uuid() {
  const c = window.crypto;
  // randomUUID só existe em contexto seguro (HTTPS/localhost); na LAN por HTTP usamos getRandomValues.
  if (c && typeof c.randomUUID === 'function') {
    try { return c.randomUUID(); } catch { /* segue para o plano B */ }
  }
  const b = new Uint8Array(16);
  if (c && typeof c.getRandomValues === 'function') c.getRandomValues(b);
  else for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

class NetError extends Error {}

async function api(method, path, body, timeoutMs = 8000) {
  const ctl = typeof AbortController === 'function' ? new AbortController() : null;
  const timer = ctl ? setTimeout(() => ctl.abort(), timeoutMs) : 0;
  const init = { method, credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' } };
  if (ctl) init.signal = ctl.signal;
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  try {
    const res = await fetch(path, init);
    let data = null;
    if (res.status !== 204) {
      const text = await res.text();
      if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    }
    return { status: res.status, ok: res.ok, data, retryAfter: res.headers.get('Retry-After') };
  } catch (e) {
    throw new NetError(e && e.name === 'AbortError' ? 'timeout' : 'network');
  } finally {
    clearTimeout(timer);
  }
}

function problemText(r, fallback) {
  const d = r && r.data;
  if (d && typeof d === 'object') return d.detail || d.title || fallback;
  return fallback;
}

function problemCode(r) {
  const d = r && r.data;
  return d && typeof d === 'object' && typeof d.code === 'string' ? d.code : '';
}

function retryAfter(r) {
  const v = Number((r.data && r.data.retryAfterSeconds) || r.retryAfter);
  return isFinite(v) && v > 0 ? Math.ceil(v) : 0;
}

function parse(text) {
  try { return JSON.parse(text); } catch { return null; }
}

function isState(s) {
  return !!s && typeof s === 'object' && typeof s.version === 'number' && Array.isArray(s.pieces);
}

function pieceLabel(id, p) {
  return (p && p.label) || PIECE_LABELS[id] || id;
}

function fmtDuration(ms) {
  if (typeof ms !== 'number' || !isFinite(ms) || ms <= 0) return '';
  return `${(ms / 1000).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} s`;
}

function fold(s) {
  return String(s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}

function loadName() {
  try { return localStorage.getItem(NAME_KEY) || DEFAULT_NAME; } catch { return DEFAULT_NAME; }
}

function saveName(name) {
  try { localStorage.setItem(NAME_KEY, name); } catch { /* modo privado: tudo bem */ }
}

function toast(text, ms = 4000) {
  ui.toast.textContent = text;
  ui.toast.hidden = false;
  clearTimeout(app.toastTimer);
  app.toastTimer = setTimeout(() => { ui.toast.hidden = true; }, ms);
}

function online() {
  return app.view === 'main' && app.conn === 'online' && !!app.state;
}

// telas

function setView(v) {
  app.view = v;
  ui.viewLoading.hidden = v !== 'loading';
  ui.viewPair.hidden = v !== 'pair';
  ui.viewMain.hidden = v !== 'main';
  ui.kitRow.hidden = v !== 'main';
  if (v !== 'main') {
    closePicker(true);
    ui.banner.hidden = true;
  }
}

async function start() {
  clearTimeout(app.startTimer);
  setView('loading');
  ui.loadingMsg.textContent = 'Conectando ao PC…';
  ui.loadingRetry.hidden = true;
  setConn('connecting');
  let r;
  try {
    r = await api('GET', '/api/state');
  } catch {
    loadingFailed('Sem conexão com o PC. Confira se o celular está no Wi-Fi da equipe e se o painel está ligado no PC.');
    return;
  }
  if (r.status === 401) { enterPairing(''); return; }
  if (!r.ok || !isState(r.data)) {
    loadingFailed(problemText(r, `O PC respondeu com erro (${r.status}).`));
    return;
  }
  enterMain(r.data);
}

function loadingFailed(text) {
  setConn('offline');
  ui.loadingMsg.textContent = `${text} Tentando de novo…`;
  ui.loadingRetry.hidden = false;
  clearTimeout(app.startTimer);
  app.startTimer = setTimeout(start, 4000);
}

// pareamento

function readPairFragment() {
  const hash = location.hash || '';
  if (!/pair=/.test(hash)) return '';
  const m = /[#&]pair=(\d{6})(?:&|$)/.exec(hash);
  // Tira o código da barra de endereço (e do histórico) assim que lido.
  history.replaceState(null, '', location.pathname + location.search);
  return m ? m[1] : '';
}

function enterPairing(message) {
  closeEvents();
  clearTimeout(app.startTimer);
  app.state = null;
  app.ops.clear();
  app.latest = {};
  setView('pair');
  setConn('connecting');
  ui.conn.textContent = 'Não pareado';
  setPairMsg(message, message ? 'error' : '');
  if (!ui.pairName.value.trim()) ui.pairName.value = loadName();
  if (!ui.pairCode.value) ui.pairCode.focus();
}

function setPairMsg(text, kind) {
  ui.pairMsg.textContent = text || '';
  ui.pairMsg.className = `msg ${kind || ''}`;
}

async function onPairSubmit(ev) {
  ev.preventDefault();
  const code = ui.pairCode.value.replace(/\D/g, '');
  const name = ui.pairName.value.trim().slice(0, 40) || DEFAULT_NAME;
  if (code.length !== 6) {
    setPairMsg('Digite os 6 números que aparecem na tela do PC.', 'error');
    ui.pairCode.focus();
    return;
  }
  ui.pairSubmit.disabled = true;
  ui.pairSubmit.textContent = 'Pareando…';
  setPairMsg('', '');
  try {
    const r = await api('POST', '/api/pair', { code, deviceName: name });
    if (r.ok) {
      saveName(name);
      ui.pairCode.value = '';
      start();
      return;
    }
    if (r.status === 401) {
      setPairMsg('Código errado ou vencido. Confira o código na tela do PC (vale 5 minutos).', 'error');
    } else if (r.status === 429) {
      const wait = retryAfter(r);
      setPairMsg(`Muitas tentativas erradas. Espere ${wait ? `${wait} s` : 'um pouco'} e confira o código na tela do PC.`, 'error');
    } else if (r.status === 409 && problemCode(r) === 'too_many_devices') {
      setPairMsg('O PC já tem o máximo de celulares pareados. Remova algum na lista do PC e tente de novo.', 'error');
    } else if (r.status === 403) {
      setPairMsg('O PC recusou este endereço. Abra o painel pelo endereço ou QR code mostrado no PC.', 'error');
    } else {
      setPairMsg(problemText(r, `Não deu para parear (erro ${r.status}).`), 'error');
    }
    ui.pairCode.select();
  } catch {
    setPairMsg('Sem conexão com o PC. Confira o Wi-Fi e tente de novo.', 'error');
  } finally {
    ui.pairSubmit.disabled = false;
    ui.pairSubmit.textContent = 'Parear';
  }
}

// tela principal

function enterMain(state) {
  setView('main');
  app.freshConnection = true;
  applyState(state, true);
  loadPresets();
  loadSamples();
  openEvents();
}

function applyState(s, authoritative) {
  if (!isState(s)) return;
  // Num mesmo fluxo a versão só sobe; um GET atrasado não pode voltar o estado para trás.
  if (!authoritative && app.state && s.version < app.state.version) return;
  app.state = s;
  render();
}

async function refreshState() {
  try {
    const r = await api('GET', '/api/state', undefined, 6000);
    if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return false; }
    if (r.ok && isState(r.data)) applyState(r.data, false);
    return r.ok;
  } catch {
    return false;
  }
}

function presetName(id) {
  const p = app.presets.find((x) => x.id === id);
  return p ? p.name : '';
}

function render() {
  const s = app.state;
  if (!s || app.view !== 'main') return;

  const kit = s.kit || {};
  const kitName = kit.presetId ? (kit.name || presetName(kit.presetId) || kit.presetId) : 'Personalizado';
  if (!holding('kit') || app.shownKit === undefined) app.shownKit = kitName;
  ui.kitName.textContent = app.shownKit;

  const sim = s.simulation === true || /simula/i.test(s.profile || '');
  ui.profile.textContent = s.profile || (sim ? 'Simulação' : 'Igreja');
  ui.profile.className = sim ? 'chip chip-sim' : 'chip';
  ui.audio.textContent = s.audioRunning ? 'Áudio ligado' : 'Áudio parado';
  ui.audio.className = s.audioRunning ? 'chip chip-ok' : 'chip chip-warn';
  ui.kitOff.hidden = s.kitEnabled !== false;
  ui.deviceName.textContent = (s.device && s.device.name) || loadName();

  renderPresets();

  const byId = {};
  for (const p of s.pieces) byId[p.piece] = p;
  const order = PIECE_ORDER.filter((id) => byId[id]).concat(s.pieces.map((p) => p.piece).filter((id) => !PIECE_ORDER.includes(id)));
  for (const id of order) renderCard(id, byId[id]);
  for (const id of Object.keys(app.cards)) {
    if (!byId[id]) app.cards[id].root.hidden = true;
  }
  if (app.picker) renderPickerList();
}

// Pedido deste aparelho ainda sem desfecho (inclusive aplicado e ainda não confirmado pelo áudio): a seleção mostrada
// fica como estava até o fim, porque o `state` pode trazer a troca antes de `engineConfirmed` (contrato, seção 6).
function holding(target) {
  for (const rec of app.ops.values()) {
    if (!rec.done && rec.kind !== 'test' && (target === 'kit' || rec.kind === 'preset' || rec.target === target)) return true;
  }
  return false;
}

function ensureCard(id) {
  let c = app.cards[id];
  if (c) return c;
  const root = ui.tplCard.content.firstElementChild.cloneNode(true);
  c = {
    root,
    label: root.querySelector('.piece-label'),
    route: root.querySelector('.tag-route'),
    off: root.querySelector('.tag-off'),
    meter: root.querySelector('.meter-fill'),
    sampleBtn: root.querySelector('.sample-btn'),
    sampleName: root.querySelector('.sample-name'),
    sampleMeta: root.querySelector('.sample-meta'),
    status: root.querySelector('.op-status'),
    testBtn: root.querySelector('.test-btn'),
  };
  root.dataset.piece = id;
  c.sampleBtn.addEventListener('click', () => openPicker(id));
  c.testBtn.addEventListener('click', () => onTest(id));
  root.addEventListener('animationend', () => root.classList.remove('hit'));
  app.cards[id] = c;
  ui.pieces.appendChild(root);
  return c;
}

function renderCard(id, p) {
  const c = ensureCard(id);
  const label = pieceLabel(id, p);
  const pending = p.route === 'pending';
  const selectable = p.selectable !== false;
  c.root.hidden = false;
  c.root.classList.toggle('pending', pending);
  c.label.textContent = label;
  c.route.hidden = !pending;
  c.off.hidden = p.enabled !== false;
  if (!holding(id) || !(id in app.shown)) app.shown[id] = p.sample;
  const sample = app.shown[id];
  c.sampleName.textContent = sample ? (sample.name || sample.id) : 'Sem sample';
  const dur = sample ? fmtDuration(sample.durationMs) : '';
  c.sampleMeta.textContent = selectable ? (dur ? `${dur} · toque para trocar` : 'Toque para escolher') : 'Troca indisponível agora';
  c.sampleBtn.disabled = !online() || !selectable;
  c.sampleBtn.setAttribute('aria-label', `${label}: ${sample ? sample.name || sample.id : 'sem sample'}. Trocar sample.`);
  c.testBtn.hidden = app.state.allowPhoneTest !== true;
  const audioOn = app.state.audioRunning !== false;
  c.testBtn.disabled = !online() || !audioOn || !p.sample;
  c.testBtn.textContent = !audioOn ? `Testar ${label} (áudio parado)` : !p.sample ? `Testar ${label} (sem sample)` : `Testar ${label} na mesa`;
  renderStatus(id);
}

function renderPresets() {
  const s = app.state;
  const sel = ui.presetSelect;
  const current = (s && s.kit && s.kit.presetId) || '';
  const want = app.presetTouched ? sel.value : current;
  const sig = app.presets.map((p) => `${p.id}\u0000${p.name}\u0000${p.available}`).join('\u0001');
  if (sel.dataset.sig !== sig) {
    sel.dataset.sig = sig;
    sel.textContent = '';
    const ph = document.createElement('option');
    ph.value = '';
    ph.textContent = app.presets.length ? 'Escolha um kit…' : 'Nenhum kit salvo no PC';
    sel.appendChild(ph);
    for (const p of app.presets) {
      const o = document.createElement('option');
      o.value = p.id;
      o.textContent = p.available === false ? `${p.name || p.id} (falta arquivo no PC)` : p.name || p.id;
      o.disabled = p.available === false;
      sel.appendChild(o);
    }
  }
  sel.value = app.presets.some((p) => p.id === want && p.available !== false) ? want : '';
  sel.disabled = !online() || app.presets.length === 0;
  ui.presetApply.disabled = !online() || !sel.value;
  renderStatus('kit');
}

async function loadPresets() {
  try {
    const r = await api('GET', '/api/presets');
    if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return; }
    if (r.ok && Array.isArray(r.data)) {
      app.presets = r.data
        .filter((p) => p && typeof p.id === 'string')
        .sort((a, b) => String(a.name || a.id).localeCompare(String(b.name || b.id), 'pt-BR', { sensitivity: 'base' }));
      render();
    }
  } catch { /* a lista volta na próxima reconexão */ }
}

function loadSamples() {
  if (app.samplesPromise) return app.samplesPromise;
  app.samplesPromise = (async () => {
    try {
      const r = await api('GET', '/api/samples', undefined, 10000);
      if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return false; }
      if (r.ok && Array.isArray(r.data)) {
        app.samples = r.data
          .filter((x) => x && typeof x.id === 'string')
          .sort((a, b) => String(a.name || a.id).localeCompare(String(b.name || b.id), 'pt-BR', { sensitivity: 'base', numeric: true }));
        app.samplesLoaded = true;
        if (app.picker) renderPickerList();
        return true;
      }
      return false;
    } catch {
      return false;
    } finally {
      app.samplesPromise = null;
    }
  })();
  return app.samplesPromise;
}

// status por alvo

function setStatus(target, text, kind, clearMs) {
  const prev = app.status[target];
  if (prev) clearTimeout(prev.timer);
  const st = { text, kind, timer: 0 };
  if (clearMs) {
    st.timer = setTimeout(() => {
      if (app.status[target] === st) { delete app.status[target]; renderStatus(target); }
    }, clearMs);
  }
  app.status[target] = st;
  renderStatus(target);
}

function renderStatus(target) {
  const el = target === 'kit' ? ui.kitStatus : app.cards[target] && app.cards[target].status;
  if (!el) return;
  let st = app.status[target];
  if (!st && target !== 'kit' && app.state) {
    // Troca em andamento pedida por outro aparelho ou pelo PC.
    const p = app.state.pieces.find((x) => x.piece === target);
    if (p && p.pendingOpId) st = { text: 'Trocando… (pedido de outro celular)', kind: 'busy' };
  }
  const text = st ? st.text : '';
  const cls = st ? `op-status st-${st.kind}` : 'op-status';
  if (el.textContent !== text) el.textContent = text;
  if (el.className !== cls) el.className = cls;
}

// operações

function newOp(target, kind, what) {
  const rec = { clientOpId: uuid(), opId: '', target, kind, what, status: 'sending', engineConfirmed: false, errors: [], done: false, polls: 0, pollTimer: 0 };
  app.ops.set(rec.clientOpId, rec);
  app.latest[target] = rec.clientOpId;
  // Guarda só as operações recentes.
  if (app.ops.size > 50) {
    for (const [k, v] of app.ops) { if (v.done) { app.ops.delete(k); if (app.ops.size <= 40) break; } }
  }
  return rec;
}

function findOp(o) {
  if (o.clientOpId && app.ops.has(o.clientOpId)) return app.ops.get(o.clientOpId);
  if (o.opId) for (const rec of app.ops.values()) if (rec.opId === o.opId) return rec;
  return null;
}

function busyText(rec) {
  return rec.kind === 'preset' ? `Aplicando o kit «${rec.what}»…` : 'Trocando…';
}

function errorsText(rec) {
  const errs = Array.isArray(rec.errors) ? rec.errors : [];
  const relevant = rec.kind === 'preset' ? errs : errs.filter((e) => !e.piece || e.piece === rec.target);
  const list = (relevant.length ? relevant : errs).map((e) => {
    const msg = e.message || ERROR_TEXT[fold(e.code).replace(/[^a-z]/g, '')] || e.code || 'erro';
    return rec.kind === 'preset' && e.piece ? `${pieceLabel(e.piece)}: ${msg}` : msg;
  });
  return list.length ? list.join('; ') : 'erro desconhecido';
}

function finish(rec) {
  rec.done = true;
  clearTimeout(rec.pollTimer);
  render(); // solta a seleção segurada por holding()
}

function showOp(rec) {
  const isLatest = app.latest[rec.target] === rec.clientOpId;
  const st = rec.status;
  if (st === 'sending' || st === 'queued' || st === 'preparing') {
    if (isLatest) setStatus(rec.target, busyText(rec), 'busy');
    return;
  }
  if (st === 'applied') {
    if (rec.engineConfirmed) {
      finish(rec);
      if (isLatest) setStatus(rec.target, rec.kind === 'preset' ? `Kit «${rec.what}» aplicado` : 'Aplicado', 'ok', 6000);
    } else if (rec.audioRunning === false || (rec.audioRunning === undefined && app.state && app.state.audioRunning === false)) {
      finish(rec);
      if (isLatest) setStatus(rec.target, rec.kind === 'preset' ? `Kit «${rec.what}» aplicado (áudio parado)` : 'Aplicado (áudio parado)', 'ok', 10000);
    } else if (isLatest) {
      // Entregue ao motor; falta a thread de áudio confirmar.
      setStatus(rec.target, busyText(rec), 'busy');
    }
    if (typeof rec.resultVersion === 'number' && app.state && rec.resultVersion > app.state.version) refreshState();
    return;
  }
  if (st === 'failed') {
    finish(rec);
    if (isLatest) setStatus(rec.target, `Falhou: ${errorsText(rec)}`, 'error');
    return;
  }
  if (st === 'superseded') {
    finish(rec);
    if (isLatest) setStatus(rec.target, 'Substituído por um pedido mais novo', 'warn', 10000);
    return;
  }
  if (st) {
    finish(rec);
    if (isLatest) setStatus(rec.target, `Estado desconhecido: ${st}`, 'warn', 10000);
  }
}

function onOpUpdate(o) {
  if (!o || typeof o !== 'object') return;
  const rec = findOp(o);
  if (!rec || rec.done) return; // de outro aparelho ou já resolvida
  if (o.opId) rec.opId = o.opId;
  rec.status = String(o.status || '').toLowerCase();
  rec.engineConfirmed = o.engineConfirmed === true;
  if (typeof o.audioRunning === 'boolean') rec.audioRunning = o.audioRunning;
  rec.errors = Array.isArray(o.errors) ? o.errors : [];
  if (typeof o.resultVersion === 'number') rec.resultVersion = o.resultVersion;
  showOp(rec);
  if (!rec.done) schedulePoll(rec, 1500);
}

// Consulta de reserva (por exemplo, se o SSE caiu). Nunca reenvia o comando.
function schedulePoll(rec, delay) {
  clearTimeout(rec.pollTimer);
  if (rec.done || !rec.opId) return;
  rec.pollTimer = setTimeout(() => pollOp(rec), delay);
}

async function pollOp(rec) {
  if (rec.done || !rec.opId || app.view !== 'main') return;
  rec.polls++;
  try {
    const r = await api('GET', `/api/operations/${encodeURIComponent(rec.opId)}`, undefined, 6000);
    if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return; }
    if (r.ok && r.data) { onOpUpdate(r.data); }
    else if (r.status === 404) {
      finish(rec);
      if (app.latest[rec.target] === rec.clientOpId) setStatus(rec.target, 'O PC não tem mais notícia deste pedido. Confira a peça.', 'warn', 10000);
      refreshState();
      return;
    }
  } catch { /* sem rede: tenta de novo abaixo */ }
  if (rec.done) return;
  if (rec.polls >= 20) {
    finish(rec);
    if (app.latest[rec.target] === rec.clientOpId) {
      if (rec.status === 'applied') setStatus(rec.target, 'Aplicado (sem confirmação do áudio)', 'warn', 10000);
      else setStatus(rec.target, 'Sem resposta do PC sobre este pedido. Confira a peça antes de tentar de novo.', 'warn');
    }
    return;
  }
  schedulePoll(rec, Math.min(1000 * rec.polls, 5000));
}

function failRec(rec, text, kind) {
  finish(rec);
  rec.status = 'failed';
  if (app.latest[rec.target] === rec.clientOpId) setStatus(rec.target, text, kind || 'error');
}

function handleCommandResponse(rec, r) {
  if (r.ok) {
    const opId = r.data && (r.data.opId || r.data.id);
    if (opId) rec.opId = String(opId);
    if (rec.status === 'sending') rec.status = 'queued';
    if (r.data && r.data.status) onOpUpdate(Object.assign({ clientOpId: rec.clientOpId }, r.data));
    else showOp(rec);
    if (!rec.done) schedulePoll(rec, 2500);
    return;
  }
  if (r.status === 409) {
    const st = r.data && (r.data.state || r.data.currentState);
    if (isState(st)) applyState(st, true); // o PC pode ter reiniciado (version menor)
    refreshState();
    failRec(rec, 'Alterado em outro dispositivo. Confira e tente de novo.', 'warn');
    return;
  }
  if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return; }
  if (r.status === 403) {
    failRec(rec, 'O PC recusou o pedido. Abra o painel pelo endereço mostrado no PC.');
    return;
  }
  if (r.status === 429) { failRec(rec, 'Muitos pedidos seguidos. Espere um instante e tente de novo.', 'warn'); return; }
  failRec(rec, `Falhou: ${problemText(r, `erro ${r.status}`)}`);
}

async function sendSample(piece, sample, baseVersion) {
  if (!online()) { toast('Sem conexão com o PC.'); return; }
  const rec = newOp(piece, 'sample', sample.name || sample.id);
  showOp(rec);
  let r;
  try {
    r = await api('POST', `/api/pieces/${encodeURIComponent(piece)}/sample`, { sampleId: sample.id, clientOpId: rec.clientOpId, baseVersion });
  } catch {
    failRec(rec, 'Sem resposta do PC. Confira a peça antes de tentar de novo.', 'warn');
    return;
  }
  handleCommandResponse(rec, r);
}

function presetSummary(preset) {
  const pieces = preset.pieces || {};
  return PIECE_ORDER.map((id) => {
    const x = pieces[id] || {};
    return `${PIECE_LABELS[id]}: ${x.sampleName || (x.sampleId ? x.sampleId.split('/').pop() : 'sem sample')}`;
  }).join('\n');
}

async function onApplyPreset() {
  if (!online()) return;
  const id = ui.presetSelect.value;
  const preset = app.presets.find((p) => p.id === id);
  if (!preset) return;
  const baseVersion = app.state.version;
  const name = preset.name || preset.id;
  const ok = await confirmDialog({
    title: 'Aplicar kit?',
    text: `Trocar as quatro peças pelo kit «${name}». O som novo vale a partir do próximo golpe.\n\n${presetSummary(preset)}`,
    ok: 'Aplicar kit',
  });
  if (!ok) return;
  if (!online()) { toast('Sem conexão com o PC. Nada foi enviado.'); return; }
  app.presetTouched = false;
  const rec = newOp('kit', 'preset', name);
  showOp(rec);
  let r;
  try {
    r = await api('POST', '/api/kit/preset', { presetId: id, clientOpId: rec.clientOpId, baseVersion });
  } catch {
    failRec(rec, 'Sem resposta do PC. Confira o kit antes de tentar de novo.', 'warn');
    return;
  }
  handleCommandResponse(rec, r);
}

async function onTest(piece) {
  if (!online() || !app.state.allowPhoneTest) return;
  const label = pieceLabel(piece, app.state.pieces.find((p) => p.piece === piece));
  const ok = await confirmDialog({
    title: `Testar ${label}?`,
    text: 'Vai tocar na mesa: o som pode sair no PA e na transmissão.',
    ok: 'Tocar agora',
    danger: true,
  });
  if (!ok) return;
  if (!online()) { toast('Sem conexão com o PC. Nada foi enviado.'); return; }
  try {
    const r = await api('POST', `/api/pieces/${encodeURIComponent(piece)}/test`, { clientOpId: uuid() });
    if (r.ok) toast(`Teste do ${label} enviado.`);
    else if (r.status === 401) enterPairing('Este celular não está mais autorizado. Pareie de novo.');
    else if (r.status === 403 && problemCode(r) === 'test_not_allowed') { toast('O teste pelo celular está desligado no PC.', 6000); refreshState(); }
    else if (r.status === 409 && problemCode(r) === 'audio_stopped') { toast('O áudio está parado no PC: o teste não toca.', 6000); refreshState(); }
    else if (r.status === 409 && problemCode(r) === 'no_sample') { toast(`O ${label} está sem sample.`, 6000); refreshState(); }
    else toast(`Teste não enviado: ${problemText(r, `erro ${r.status}`)}`, 6000);
  } catch {
    toast('Sem resposta do PC. O teste pode não ter tocado.', 6000);
  }
}

async function onLogout() {
  const ok = await confirmDialog({
    title: 'Desparear este celular?',
    text: 'Para controlar de novo vai ser preciso um código novo do PC.',
    ok: 'Desparear',
    danger: true,
  });
  if (!ok) return;
  try { await api('POST', '/api/logout', {}); } catch { /* sem rede: o PC pode remover pela lista */ }
  enterPairing('Celular despareado.');
  setPairMsg('Celular despareado.', 'ok');
}

// SSE e conexão

function setConn(c) {
  app.conn = c;
  const labels = { online: 'Conectado', connecting: 'Conectando', reconnecting: 'Reconectando', offline: 'Sem conexão com o PC' };
  ui.conn.textContent = labels[c] || c;
  ui.conn.className = `badge conn conn-${c}`;
  const showBanner = app.view === 'main' && (c === 'reconnecting' || c === 'offline');
  ui.banner.hidden = !showBanner;
  ui.bannerSub.textContent = c === 'offline' ? 'Tentando reconectar sozinho. Confira o Wi-Fi do celular.' : 'Reconectando…';
  if (c !== 'reconnecting') clearTimeout(app.offlineTimer);
  if (c !== 'online') onMeters({}); // medidor parado não pode parecer ao vivo
  if (app.view === 'main') render();
  if (app.picker) renderPickerList();
}

function touch() {
  app.lastEventAt = Date.now();
}

function openEvents() {
  closeEvents();
  if (typeof EventSource !== 'function') {
    loadingFailed('Este navegador não tem suporte ao painel (EventSource).');
    return;
  }
  app.freshConnection = true;
  touch();
  const es = new EventSource('/api/events');
  app.es = es;
  const on = (name, fn) => es.addEventListener(name, (e) => { if (app.es !== es) return; touch(); fn(e); });
  on('state', (e) => {
    const s = parse(e.data);
    if (!isState(s)) return;
    const first = app.freshConnection;
    app.freshConnection = false;
    applyState(s, first); // na (re)conexão o retrato completo manda
    if (first) onConnected();
  });
  on('op', (e) => onOpUpdate(parse(e.data)));
  on('meters', (e) => onMeters(parse(e.data)));
  on('hit', (e) => onHit(parse(e.data)));
  on('revoked', () => {
    closeEvents();
    enterPairing('Este celular foi removido no PC. Para usar de novo, pareie com um código novo.');
  });
  es.onmessage = () => { if (app.es === es) touch(); };
  es.onerror = () => { if (app.es === es) onEventsError(es); };
}

function closeEvents() {
  clearTimeout(app.retryTimer);
  if (app.es) {
    const es = app.es;
    app.es = null;
    try { es.close(); } catch { /* nada */ }
  }
}

function onConnected() {
  app.retryCount = 0;
  clearTimeout(app.offlineTimer);
  setConn('online');
  loadPresets();
  // Só consulta o que estava pendente; nunca reenvia.
  for (const rec of app.ops.values()) {
    if (!rec.done && rec.opId) { rec.polls = 0; schedulePoll(rec, 100); }
  }
}

function markDisconnected() {
  app.freshConnection = true;
  // Arma o prazo só na passagem para "Reconectando": cada nova tentativa falha não pode adiar o aviso.
  if (app.conn === 'online' || app.conn === 'connecting') {
    setConn('reconnecting');
    clearTimeout(app.offlineTimer);
    app.offlineTimer = setTimeout(() => { if (app.conn === 'reconnecting') setConn('offline'); }, OFFLINE_AFTER_MS);
  }
}

function onEventsError(es) {
  markDisconnected();
  if (es.readyState === 2 /* CLOSED */) {
    // O navegador desistiu (resposta HTTP de erro, por exemplo 401). Confere e reabre com espera crescente.
    closeEvents();
    scheduleReconnect();
  }
  // CONNECTING: o próprio EventSource tenta de novo; confere se o aparelho ainda está pareado (401 = parear de novo).
  else if (Date.now() - app.lastAuthCheck > 3000) {
    app.lastAuthCheck = Date.now();
    refreshState();
  }
}

function scheduleReconnect(delay) {
  clearTimeout(app.retryTimer);
  const wait = delay !== undefined ? delay : Math.min(1000 * 2 ** app.retryCount, 10000);
  app.retryCount = Math.min(app.retryCount + 1, 6);
  app.retryTimer = setTimeout(async () => {
    if (app.view !== 'main') return;
    try {
      const r = await api('GET', '/api/state', undefined, 5000);
      if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return; }
      if (r.ok && isState(r.data)) applyState(r.data, true);
    } catch { /* PC fora do ar: tenta abrir o SSE mesmo assim */ }
    if (app.view === 'main') openEvents();
  }, wait);
}

// Wi-Fi do celular pode cair sem o EventSource perceber; confere quando fica tempo demais sem eventos.
async function probe() {
  if (app.probing || app.view !== 'main') return;
  app.probing = true;
  try {
    const r = await api('GET', '/api/state', undefined, 5000);
    if (r.status === 401) { enterPairing('Este celular não está mais autorizado. Pareie de novo.'); return; }
    if (r.ok && isState(r.data)) {
      applyState(r.data, app.conn !== 'online');
      if (app.conn !== 'online' || Date.now() - app.lastEventAt > IDLE_PROBE_MS) {
        openEvents(); // reabre o fluxo; o primeiro `state` volta a valer como retrato completo
      }
      touch();
    }
  } catch {
    closeEvents();
    markDisconnected();
    scheduleReconnect();
  } finally {
    app.probing = false;
  }
}

function watchdog() {
  if (app.view !== 'main' || document.hidden) return;
  if (app.conn === 'online' && Date.now() - app.lastEventAt > IDLE_PROBE_MS) probe();
}

function onMeters(m) {
  if (!m || typeof m !== 'object') return;
  const values = m.pieces && typeof m.pieces === 'object' ? m.pieces : m;
  for (const id of Object.keys(app.cards)) {
    const db = Number(values[id]);
    const f = isFinite(db) ? Math.max(0, Math.min(1, (db + 60) / 60)) : 0;
    app.cards[id].meter.style.transform = `scaleX(${f.toFixed(3)})`;
  }
}

function onHit(h) {
  if (!h || !app.cards[h.piece]) return;
  const root = app.cards[h.piece].root;
  const v = Number(h.velocity);
  root.style.setProperty('--hit', String(isFinite(v) ? Math.max(0.3, Math.min(1, v)) : 0.8));
  root.classList.remove('hit');
  void root.offsetWidth; // reinicia a animação
  root.classList.add('hit');
}

// lista de samples

function openPicker(piece) {
  if (!online()) { toast('Sem conexão com o PC.'); return; }
  const p = app.state.pieces.find((x) => x.piece === piece);
  if (!p || p.selectable === false) return;
  app.picker = { piece, baseVersion: app.state.version, opener: document.activeElement };
  ui.pickerTitle.textContent = `${pieceLabel(piece, p)}: escolher sample`;
  ui.pickerSearch.value = '';
  ui.picker.hidden = false;
  document.body.classList.add('locked');
  history.pushState({ sdPicker: true }, '');
  renderPickerList();
  ui.pickerClose.focus();
  loadSamples();
  // Mostra o atual na tela.
  const cur = ui.pickerList.querySelector('.current');
  if (cur) cur.scrollIntoView({ block: 'center' });
}

function closePicker(fromHistory) {
  if (!app.picker) return;
  const opener = app.picker.opener;
  app.picker = null;
  ui.picker.hidden = true;
  document.body.classList.remove('locked');
  if (!fromHistory && history.state && history.state.sdPicker) history.back();
  if (opener && typeof opener.focus === 'function' && app.view === 'main') opener.focus();
}

function renderPickerList() {
  if (!app.picker || !app.state) return;
  const piece = app.picker.piece;
  const p = app.state.pieces.find((x) => x.piece === piece);
  const shown = piece in app.shown ? app.shown[piece] : p && p.sample;
  const currentId = shown ? shown.id : '';
  const q = fold(ui.pickerSearch.value.trim());
  const words = q ? q.split(/\s+/) : [];
  const list = app.samples.filter((s) => {
    if (!words.length) return true;
    const hay = fold(`${s.name || ''} ${s.id}`);
    return words.every((w) => hay.includes(w));
  });
  const nameCount = {};
  for (const s of app.samples) nameCount[s.name] = (nameCount[s.name] || 0) + 1;
  const can = online();

  const frag = document.createDocumentFragment();
  for (const s of list) {
    const li = document.createElement('li');
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'sample-item';
    b.disabled = !can;
    const isCur = s.id === currentId;
    if (isCur) { b.classList.add('current'); b.setAttribute('aria-current', 'true'); }
    const text = document.createElement('span');
    text.className = 'sample-text';
    const name = document.createElement('span');
    name.className = 'sample-name';
    name.textContent = s.name || s.id;
    const meta = document.createElement('span');
    meta.className = 'sample-meta';
    const bits = [fmtDuration(s.durationMs)];
    if (s.channels === 2) bits.push('estéreo');
    // Nome repetido em pastas diferentes: mostra a pasta (relativa à biblioteca) para diferenciar.
    if (nameCount[s.name] > 1 && s.id.includes('/')) bits.push(s.id.slice(0, s.id.lastIndexOf('/')));
    meta.textContent = bits.filter(Boolean).join(' · ');
    text.append(name, meta);
    b.appendChild(text);
    if (isCur) {
      const now = document.createElement('span');
      now.className = 'now';
      now.textContent = '✓ atual';
      b.appendChild(now);
    }
    b.addEventListener('click', () => onPick(s));
    li.appendChild(b);
    frag.appendChild(li);
  }
  const scroll = ui.pickerList.scrollTop;
  ui.pickerList.textContent = '';
  if (!list.length) {
    const li = document.createElement('li');
    li.className = 'empty';
    li.textContent = !app.samplesLoaded ? 'Carregando a lista do PC…'
      : app.samples.length ? 'Nenhum sample com esse nome.' : 'Nenhum WAV na pasta de samples do PC.';
    frag.appendChild(li);
  }
  ui.pickerList.appendChild(frag);
  ui.pickerList.scrollTop = scroll;
  ui.pickerInfo.textContent = !can ? 'Sem conexão com o PC: não dá para trocar agora.'
    : app.samplesLoaded ? `${list.length} de ${app.samples.length} samples` : '';
}

function onPick(sample) {
  if (!app.picker) return;
  const { piece, baseVersion } = app.picker;
  const p = app.state && app.state.pieces.find((x) => x.piece === piece);
  closePicker(false);
  if (p && p.sample && p.sample.id === sample.id) { toast('Esse já é o sample atual.'); return; }
  sendSample(piece, sample, baseVersion);
}

// confirmação

function confirmDialog({ title, text, ok, danger }) {
  if (app.confirmResolve) app.confirmResolve(false);
  ui.confirmTitle.textContent = title;
  ui.confirmText.textContent = text;
  ui.confirmOk.textContent = ok || 'OK';
  ui.confirmOk.className = `btn ${danger ? 'btn-danger' : 'btn-primary'}`;
  const opener = document.activeElement;
  ui.confirm.hidden = false;
  ui.confirmCancel.focus();
  return new Promise((resolve) => {
    app.confirmResolve = (v) => {
      app.confirmResolve = null;
      ui.confirm.hidden = true;
      if (opener && typeof opener.focus === 'function') opener.focus();
      resolve(v);
    };
  });
}

// início

function bind() {
  const ids = {
    viewLoading: 'view-loading', viewPair: 'view-pair', viewMain: 'view-main', loadingMsg: 'loading-msg', loadingRetry: 'loading-retry',
    conn: 'conn', kitRow: 'kit-row', kitName: 'kit-name', profile: 'profile', audio: 'audio', kitOff: 'kit-off',
    banner: 'banner', bannerSub: 'banner-sub',
    pairForm: 'pair-form', pairCode: 'pair-code', pairName: 'pair-name', pairMsg: 'pair-msg', pairSubmit: 'pair-submit',
    presetSelect: 'preset-select', presetApply: 'preset-apply', kitStatus: 'kit-status', pieces: 'pieces',
    deviceName: 'device-name', logout: 'logout',
    picker: 'picker', pickerTitle: 'picker-title', pickerClose: 'picker-close', pickerSearch: 'picker-search', pickerInfo: 'picker-info', pickerList: 'picker-list',
    confirm: 'confirm', confirmTitle: 'confirm-title', confirmText: 'confirm-text', confirmOk: 'confirm-ok', confirmCancel: 'confirm-cancel',
    toast: 'toast', tplCard: 'tpl-card',
  };
  for (const [k, id] of Object.entries(ids)) ui[k] = $(id);

  ui.pairForm.addEventListener('submit', onPairSubmit);
  ui.pairCode.addEventListener('input', () => {
    const v = ui.pairCode.value.replace(/\D/g, '').slice(0, 6);
    if (v !== ui.pairCode.value) ui.pairCode.value = v;
  });
  ui.loadingRetry.addEventListener('click', start);
  ui.presetSelect.addEventListener('change', () => { app.presetTouched = true; renderPresets(); });
  ui.presetApply.addEventListener('click', onApplyPreset);
  ui.logout.addEventListener('click', onLogout);
  ui.pickerClose.addEventListener('click', () => closePicker(false));
  ui.pickerSearch.addEventListener('input', renderPickerList);
  ui.confirmOk.addEventListener('click', () => app.confirmResolve && app.confirmResolve(true));
  ui.confirmCancel.addEventListener('click', () => app.confirmResolve && app.confirmResolve(false));
  ui.confirm.addEventListener('click', (e) => { if (e.target === ui.confirm && app.confirmResolve) app.confirmResolve(false); });

  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    if (app.confirmResolve) app.confirmResolve(false);
    else if (app.picker) closePicker(false);
  });
  window.addEventListener('popstate', () => { if (app.picker) closePicker(true); });
  window.addEventListener('offline', () => { if (app.view === 'main') { markDisconnected(); setConn('offline'); } });
  window.addEventListener('online', () => { if (app.view === 'main' && app.conn !== 'online') { app.retryCount = 0; probe(); } });
  document.addEventListener('visibilitychange', () => {
    // Tela do celular desbloqueada: o navegador pode ter matado a conexão enquanto dormia.
    if (!document.hidden && app.view === 'main' && Date.now() - app.lastEventAt > 8000) probe();
  });
  setInterval(watchdog, 5000);
}

function init() {
  bind();
  const code = readPairFragment();
  if (code) ui.pairCode.value = code;
  ui.pairName.value = loadName();
  start();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();
