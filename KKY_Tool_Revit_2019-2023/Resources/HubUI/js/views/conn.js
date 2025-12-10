// Resources/HubUI/js/views/conn.js
// Connector Diagnostics view (fix2 규약 준수)
// - 버튼/이벤트: connector:run / connector:save-excel
// - 단위: 서버는 inch 고정, UI가 mm 선택 시 전송 전에 inch로 변환 / 표시 시 mm 변환
// - ParamName은 표에서 숨김(설정에 존재하므로)
// - UX: 결과영역은 검토 시작 전 숨김 → [검토 시작] 후 안내문 노출 → 데이터 수신 시 필터+표 노출
// - 강조: Status별 톤은 Value1/Value2/Status 셀만 '캡슐형 테두리'로 표시

import { clear, div, tdText, toast, setBusy, showExcelSavedDialog } from '../core/dom.js';
import { renderTopbar } from '../core/topbar.js';
import { post, onHost } from '../core/bridge.js';

const SKEY = 'kky_conn_opts';
const INCH_TO_MM = 25.4;
const MAX_PREVIEW_ROWS = 150;

/* ---------- 옵션 ---------- */
function loadOpts() {
  try {
    return Object.assign({ tol: 1.0, unit: 'inch', param: 'Comments' }, JSON.parse(localStorage.getItem(SKEY) || '{}'));
  } catch { return { tol: 1.0, unit: 'inch', param: 'Comments' }; }
}
function saveOpts(o) { localStorage.setItem(SKEY, JSON.stringify(o)); }

/* ---------- 페어 정규화/중복제거 ---------- */
function asInt(v){ const n=Number(v); return Number.isFinite(n)?n:Number(String(v).replace(/[^\d-]/g,''))||Number.MAX_SAFE_INTEGER; }
function canonPairRow(r){
  const row={...r}; const a=row.Id1, b=row.Id2; const aN=asInt(a), bN=asInt(b);
  if (bN<aN || (bN===aN && String(b)<String(a))) {
    [row.Id1,row.Id2]=[row.Id2,row.Id1];
    [row.Category1,row.Category2]=[row.Category2,row.Category1];
    [row.Family1,row.Family2]=[row.Family2,row.Family1];
    [row.Value1,row.Value2]=[row.Value2,row.Value1];
  }
  return row;
}
function dedupRows(input){
  const seen=new Set(), out=[];
  for (const raw of (Array.isArray(input)?input:[])) {
    const r=canonPairRow(raw);
    const key=[r.Id1??'', r.Id2??'', r.ConnectionType??'', r.ParamName??''].join('|');
    if (!seen.has(key)) { seen.add(key); out.push(r); }
  }
  return out;
}

/* ---------- 단위 ---------- */
const toMm = (inch)=> Number.isFinite(+inch) ? (+inch * INCH_TO_MM) : inch;

/* ---------- Status 매핑 ---------- */
function statusKind(s){
  const t = String(s||'').trim().toLowerCase();
  if (/\b(mis-?match|error|err|fail|invalid|false)\b/.test(t)) return 'bad';
  if (/\b(warn|warning|minor|check)\b/.test(t)) return 'warn';
  if (/\b(ok|connected|valid|true)\b/.test(t)) return 'ok';
  return 'info';
}

/* ---------- 렌더 ---------- */
export function renderConn() {
  const root = document.getElementById('app'); clear(root);
  renderTopbar(root, true);
  const topbar = root.firstElementChild; if (topbar) topbar.classList.add('hub-topbar');

  const opts = loadOpts();
  const state = {
    rowsInch: [],
    mismatchRows: [],
    mismatchTotal: 0,
    mismatchPreviewCount: 0,
    mismatchHasMore: false,
    notConnectedRows: [],
    notConnectedTotal: 0,
    notConnectedPreviewCount: 0,
    notConnectedHasMore: false,
    hasRun: false,
    tab: 'mismatch',
    totalCount: 0
  };
  const page = div('conn-page feature-shell');

  const header = div('feature-header');
  const heading = div('feature-heading');
  heading.innerHTML = `
    <span class="feature-kicker">Connector Diagnostics</span>
    <h2 class="feature-title">커넥터 진단</h2>
    <p class="feature-sub">허용범위, 단위, 파라미터명을 입력하고 파이프/덕트 커넥터 매칭을 진단합니다.</p>`;

  const run = cardBtn('검토 시작', onRun);
  const save = cardBtn('엑셀 내보내기', () =>
    post('connector:save-excel', { })
  );
  save.id = 'btnConnSave';
  save.disabled = true;

  const actions = div('feature-actions');
  actions.append(run, save);
  header.append(heading, actions);
  page.append(header);

  // 설정/작업 (sticky)
  const rowSettings = div('conn-row settings conn-sticky feature-controls');

  const cardSettings = div('conn-card section section-settings');
  const grid = div('conn-grid');
  grid.append(
    kv('허용범위', makeNumber(opts.tol ?? 1.0)),
    kv('단위', makeUnit(opts.unit || 'inch')),
    kv('파라미터', makeText(opts.param || 'Comments'))
  );
  cardSettings.append(h1('설정'), grid);

  const cardActions = div('conn-card section section-actions');
  cardActions.innerHTML = '<div class="conn-title">결과 검토</div>';
  const excelHelp = document.createElement('ul');
  excelHelp.className = 'conn-excel-hint';
  excelHelp.innerHTML = `
    <li><strong>Connection Type</strong>: Near - 허용범위 내 객체 대상으로 검토(미연결) Connected -  물리적 연결된 상태</li>
    <li><strong>Status</strong>: Mismatch - 값 불일치, OK - 일치</li>
    <li><strong>Value1 / Value2</strong>: 허용범위 내 비교 대상들의 Parameter 값</li>`;
  cardActions.append(excelHelp);

  rowSettings.append(cardSettings, cardActions);

  // 검토 결과 (sticky)
  const cardResults = div('conn-card section section-results conn-sticky feature-results-panel');
  const resultsTitle = h1('검토 결과');
  const summary = div('conn-summary');
  const badgeAll = chip('총 결과', '0');
  const badgeFiltered = chip('표시 중', '0');
  summary.append(badgeAll, badgeFiltered);

  const tabBar = div('conn-tabs');
  const tabs = [
    { key: 'mismatch', label: 'Mismatch' },
    { key: 'not-connected', label: 'Not Connected' }
  ];
  const tabButtons = new Map();

  tabs.forEach(({ key, label }) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'conn-tab';
    btn.dataset.tab = key;
    btn.textContent = label;
    btn.addEventListener('click', () => setTab(key));
    tabButtons.set(key, btn);
    tabBar.append(btn);
  });

  const resultHead = div('feature-results-head');
  resultHead.append(resultsTitle, tabBar, summary);

  // 안내문(최초 숨김 – [검토 시작] 때만 표시)
  const emptyGuide = div('conn-empty');
  emptyGuide.setAttribute('aria-live','polite');
  emptyGuide.textContent = '🧩 검토를 시작하려면 상단에서 기준을 설정하고 [검토 시작]을 눌러주세요.';
  const previewNotice = div('conn-preview-note');
  previewNotice.style.display = 'none';

  cardResults.append(resultHead, emptyGuide, previewNotice);

  // 결과 표 (최초 숨김)
  const tableWrap = div('conn-tablewrap');
  const table = document.createElement('table'); table.className = 'conn-table';
  const thead = document.createElement('thead');
  const tbody = document.createElement('tbody');
  table.append(thead, tbody);
  tableWrap.append(table);

  // 최초엔 결과 섹션 자체를 숨김
  cardResults.style.display = 'none';
  tableWrap.style.display = 'none';
  emptyGuide.style.display = 'none';

  cardResults.append(tableWrap);
  page.append(rowSettings, cardResults);
  root.append(page);

  // refs
  const tol = grid.querySelector('input[type="number"]');
  const unit = grid.querySelector('select');
  const param = grid.querySelector('input[type="text"]');

  const commit = () => saveOpts({
    tol: parseFloat(tol.value || '1') || 1,
    unit: String(unit.value),
    param: String(param.value || 'Comments')
  });
  tol.addEventListener('change', () => { commit(); if(state.hasRun) paint(); });
  unit.addEventListener('change', () => { commit(); if(state.hasRun) paint(); });
  param.addEventListener('change', commit);

  /* ---- Head (ParamName 숨김) ---- */
  function paintHead() {
    const isMm = String(unit.value) === 'mm';
    const distHeader = isMm ? 'Distance (mm)' : 'Distance (inch)';
    thead.innerHTML = '<tr>'
      + '<th class="mono">Id1</th><th class="mono">Id2</th>'
      + '<th>Category1</th><th>Category2</th>'
      + '<th class="dim">Family1</th><th class="dim">Family2</th>'
      + `<th class="num">${distHeader}</th>`
      + '<th>ConnectionType</th>'
      + '<th class="dim">Value1</th><th class="dim">Value2</th>'
      + '<th>Status</th>'
      + '</tr>';
  }

  /* ---- Body ---- */
  function paintBody() {
    while (tbody.firstChild) tbody.removeChild(tbody.firstChild);
    const isMm = String(unit.value) === "mm";

    const { rows: activeRows, total: activeTotal, previewCount, hasMore } = getActiveMeta();

    badgeAll.querySelector(".num").textContent = String(activeTotal);
    badgeFiltered.querySelector(".num").textContent = String(activeRows.length);

    if (hasMore) {
      previewNotice.textContent = `미리보기에서는 상위 ${previewCount}건만 표시됩니다. 전체 ${activeTotal}건은 엑셀 내보내기로 확인하세요.`;
      previewNotice.style.display = 'block';
    } else {
      previewNotice.textContent = '';
      previewNotice.style.display = 'none';
    }

    if (activeRows.length === 0) {
      const tr = document.createElement("tr");
      const td = document.createElement("td");
      td.colSpan = 11;
      td.textContent = "해당 조건의 결과가 없습니다.";
      td.className = "conn-empty-row";
      tr.append(td);
      tbody.append(tr);
      updateSaveDisabled();
      return;
    }

    if (activeRows.length > MAX_PREVIEW_ROWS) {
      const tr = document.createElement("tr");
      const td = document.createElement("td");
      td.colSpan = 11;
      td.textContent = "결과가 150개 이상입니다. 미리보기 대신 엑셀 내보내기를 이용해 주세요.";
      td.className = "conn-empty-row";
      tr.append(td);
      tbody.append(tr);
      updateSaveDisabled();
      return;
    }

    activeRows.forEach(r => {
      const tr = document.createElement("tr");

      let dist = (r["Distance (inch)"] ?? r.DistanceInch ?? "");
      if (isMm && dist !== "") {
        const converted = toMm(dist);
        dist = Number.isFinite(converted) ? converted.toFixed(4) : converted;
      }

      const cells = [
        r.Id1, r.Id2, r.Category1, r.Category2, r.Family1, r.Family2,
        dist, r.ConnectionType, r.Value1, r.Value2, r.Status
      ];

      cells.forEach((v, idx) => {
        const td = tdText(v);
        if (idx <= 1) td.classList.add("mono");
        if (idx === 6) td.classList.add("num");
        if (idx === 8 || idx === 9) td.classList.add("dim");

        if (idx === 8 || idx === 9 || idx === 10) {
          const kind = statusKind(cells[10]);
          td.classList.add("tone-cell",
            kind==='ok'?'tone-ok':kind==='warn'?'tone-warn':kind==='bad'?'tone-bad':'tone-info');
        }
        tr.append(td);
      });

      tbody.append(tr);
    });

    updateSaveDisabled();
  }

  function paint(){
    paintHead();
    paintBody();
  }

  function applyIncomingRows(payload){
    const rows = (payload && Array.isArray(payload.rows)) ? payload.rows : [];
    const mismatchSection = (payload && payload.mismatch) || {};
    const nearSection = (payload && payload.near) || {};

    const cleaned = dedupRows(rows);
    const mismatchFromCleaned = cleaned.filter(r => normalizeStatus(r) === 'MISMATCH');
    const nearFromCleaned = cleaned.filter(r => normalizeConnectionType(r).toUpperCase() === 'NEAR');

    const mismatchPreview = dedupRows(Array.isArray(mismatchSection.rows) ? mismatchSection.rows : mismatchFromCleaned);
    const nearPreview = dedupRows(Array.isArray(nearSection.rows) ? nearSection.rows : nearFromCleaned);

    state.rowsInch = cleaned;
    state.mismatchTotal = Number(mismatchSection.total) || mismatchFromCleaned.length;
    state.notConnectedTotal = Number(nearSection.total) || nearFromCleaned.length;

    state.mismatchRows = mismatchPreview.slice(0, MAX_PREVIEW_ROWS);
    state.notConnectedRows = nearPreview.slice(0, MAX_PREVIEW_ROWS);

    state.mismatchPreviewCount = Number(mismatchSection.previewCount) || Math.min(state.mismatchRows.length, Math.max(state.mismatchTotal, state.mismatchRows.length), MAX_PREVIEW_ROWS);
    state.notConnectedPreviewCount = Number(nearSection.previewCount) || Math.min(state.notConnectedRows.length, Math.max(state.notConnectedTotal, state.notConnectedRows.length), MAX_PREVIEW_ROWS);

    state.mismatchHasMore = (mismatchSection.hasMore === true) || state.mismatchTotal > MAX_PREVIEW_ROWS;
    state.notConnectedHasMore = (nearSection.hasMore === true) || state.notConnectedTotal > MAX_PREVIEW_ROWS;

    const totalFromPayload = Number(payload && payload.total);
    state.totalCount = (Number.isFinite(totalFromPayload) && totalFromPayload > 0)
      ? totalFromPayload
      : (cleaned.length > 0 ? cleaned.length : (state.mismatchTotal + state.notConnectedTotal));

    setTab('mismatch', { silent: true });

    // 전환: 안내문 숨김 → 표 표시
    emptyGuide.style.display = 'none';
    tableWrap.style.display = 'block';

    paint();
  }


  function onRun(){
    commit(); setBusy(true);
    state.hasRun = true;

    // 결과 섹션 오픈 + 안내문 보이기
    cardResults.style.display = 'block';
    emptyGuide.style.display = 'flex';
    tableWrap.style.display = 'none';

    let sendTol = parseFloat(tol.value || '1');
    let sendUnit = String(unit.value || 'inch');
    if (sendUnit === 'mm') { if (!isFinite(sendTol)) sendTol = 1; sendTol = sendTol / INCH_TO_MM; sendUnit = 'inch'; }
    post('connector:run', { tol: sendTol, unit: sendUnit, param: String(param.value || 'Comments') });
  }


  onHost(({ ev, payload }) => {
    switch (ev) {
      case 'connector:done':
      case 'connector:loaded':
        setBusy(false); 
        // 결과 섹션 보장
        cardResults.style.display = 'block';
        applyIncomingRows(payload || {});
        break;
      case 'connector:saved': {
        const p = (payload && payload.path) || '';
        if (p) {
          showExcelSavedDialog('엑셀 파일을 저장했습니다.', p, (path) => {
            if (path) post('excel:open', { path });
          });
        } else {
          toast('엑셀 파일이 저장되었습니다.', 'ok', 2600);
        }
        break;
      }
      case 'revit:error':
        setBusy(false); toast((payload && payload.message) || '오류가 발생했습니다.', 'err', 3200); break;
      default: break;
    }
  });

  /* helpers */
  function normalizeStatus(row){
    return String((row && (row.Status ?? row.status)) || '').trim().toUpperCase();
  }

  function normalizeConnectionType(row){
    return String((row && (row.ConnectionType ?? row.connectionType ?? row.Type ?? row.type)) || '').trim();
  }

  function getActiveRows(){
    const base = state.tab === 'mismatch'
      ? state.mismatchRows
      : state.notConnectedRows;

    return Array.isArray(base) ? base : [];
  }

  function getActiveMeta(){
    if (state.tab === 'mismatch') {
      return {
        rows: getActiveRows(),
        total: state.mismatchTotal,
        previewCount: state.mismatchPreviewCount || getActiveRows().length,
        hasMore: state.mismatchHasMore
      };
    }
    return {
      rows: getActiveRows(),
      total: state.notConnectedTotal,
      previewCount: state.notConnectedPreviewCount || getActiveRows().length,
      hasMore: state.notConnectedHasMore
    };
  }

  function updateSaveDisabled(){
    const saveBtn = document.getElementById('btnConnSave');
    if (saveBtn) saveBtn.disabled = state.totalCount === 0;
  }


  function setTab(tab, opts = {}){
    if (!tabButtons.has(tab)) return;
    state.tab = tab;
    tabButtons.forEach((btn, key) => {
      if (key === tab) btn.classList.add('is-active'); else btn.classList.remove('is-active');
    });
    if (!opts.silent) {
      paintBody();
    }
  }

  setTab('mismatch', { silent: true });

  function h1(t){ const e=document.createElement('div'); e.className='conn-title'; e.textContent=t; return e; }
  function kv(label, inputEl){ const wrap=document.createElement('div'); wrap.className='conn-kv'; const cap=document.createElement('label'); cap.textContent=label; wrap.append(cap,inputEl); return wrap; }
  function chip(label, numText){ const el=document.createElement('span'); el.className='conn-chip'; const t=document.createElement('span'); t.textContent=label; const n=document.createElement('span'); n.className='num'; n.textContent=numText; el.append(t,n); return el; }
  function cardBtn(text, onClick) {
    const b = document.createElement('button');
    b.textContent = text;
    b.className = 'card-action-btn';
    if (typeof onClick === 'function') b.addEventListener('click', onClick);
    return b;
  }
  function makeNumber(v){ const i=document.createElement('input'); i.type='number'; i.step='0.0001'; i.value=String(v); return i; }
  function makeUnit(v){ const s=document.createElement('select'); s.className='kkyt-select'; s.innerHTML='<option value="inch">inch</option><option value="mm">mm</option>'; s.value=String(v); return s; }
  function makeText(v){ const i=document.createElement('input'); i.type='text'; i.value=String(v); return i; }
}
