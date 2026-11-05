import { listRecordings, deleteRecording, getRecordingBlob } from './db.js';
import { downloadBlob, formatBytes, formatDuration } from './downloads.js';

const $ = (id) => document.getElementById(id);

let state = null;
let settings = { format: 'mp3', bitrate: 192, autoDownload: true };
let tickId = null;

function notice(message) {
  const el = $('notice');
  el.textContent = message || '';
  el.hidden = !message;
}

function setStatus(text, tone) {
  const pill = $('statusPill');
  pill.textContent = text;
  pill.dataset.tone = tone || 'idle';
}

function syncBitrate() {
  const isWav = $('format').value === 'wav';
  $('bitrateRow').hidden = isWav;
  $('bitrate').disabled = isWav;
}

function renderNow(now) {
  if (now && now.title) {
    $('trackTitle').textContent = now.title;
    $('trackArtist').textContent = now.artist || 'Artis tidak diketahui';
    if (now.artwork) {
      const img = document.createElement('img');
      img.alt = '';
      img.src = now.artwork;
      img.addEventListener('error', () => img.remove());
      const art = $('art');
      art.textContent = '';
      art.appendChild(img);
    }
  }
}

function renderState() {
  if (!state) return;
  if (state.status === 'recording') {
    setStatus('Merekam', 'rec');
    $('openBtn').textContent = 'Buka jendela rekaman';
    $('openBtn').classList.add('stop');
    startTicker();
  } else {
    setStatus('Siap', 'idle');
    $('openBtn').textContent = 'Buka jendela rekam';
    $('openBtn').classList.remove('stop');
    stopTicker();
  }
  if (state.title) {
    $('trackTitle').textContent = state.title;
    $('trackArtist').textContent = state.artist || 'Artis tidak diketahui';
  }
}

function startTicker() {
  stopTicker();
  const update = () => {
    if (!state || state.status !== 'recording' || !state.startedAt) return;
    const seconds = (Date.now() - state.startedAt) / 1000;
    $('openBtn').textContent = `Buka jendela rekaman · ${formatDuration(seconds)}`;
  };
  update();
  tickId = setInterval(update, 500);
}

function stopTicker() {
  if (tickId) clearInterval(tickId);
  tickId = null;
}

async function renderLibrary() {
  const items = await listRecordings();
  const list = $('list');
  list.textContent = '';
  $('empty').hidden = items.length > 0;
  $('libCount').textContent = items.length ? `${items.length} berkas` : '';

  for (const item of items) {
    const row = document.createElement('li');
    row.className = 'item';

    const main = document.createElement('div');
    main.className = 'item-main';

    const title = document.createElement('p');
    title.className = 'item-title';
    title.textContent = item.name;
    title.title = item.name;

    const sub = document.createElement('p');
    sub.className = 'sub';
    sub.textContent = `${formatDuration(item.duration)} · ${formatBytes(item.size)} · ${String(
      item.format || ''
    ).toUpperCase()}`;

    main.append(title, sub);

    const actions = document.createElement('div');
    actions.className = 'item-actions';

    const dl = document.createElement('button');
    dl.className = 'icon-btn';
    dl.type = 'button';
    dl.title = 'Unduh';
    dl.textContent = '↓';
    dl.addEventListener('click', async () => {
      try {
        const blob = await getRecordingBlob(item.id);
        if (!blob) throw new Error('Berkas tidak ditemukan');
        await downloadBlob(blob, item.name);
      } catch (err) {
        notice('Gagal mengunduh: ' + String((err && err.message) || err));
      }
    });

    const del = document.createElement('button');
    del.className = 'icon-btn danger';
    del.type = 'button';
    del.title = 'Hapus';
    del.textContent = '×';
    del.addEventListener('click', async () => {
      await deleteRecording(item.id);
      await renderLibrary();
    });

    actions.append(dl, del);
    row.append(main, actions);
    list.appendChild(row);
  }
}

async function boot() {
  const res = await chrome.runtime.sendMessage({ type: 'GET_STATE' });
  if (!res || !res.ok) throw new Error((res && res.error) || 'Gagal memuat status');

  state = res.state;
  settings = res.settings;

  $('format').value = settings.format;
  $('bitrate').value = String(settings.bitrate);
  $('autoDownload').checked = settings.autoDownload;
  syncBitrate();

  renderNow(res.now);
  renderState();
  await renderLibrary();

  chrome.runtime.onMessage.addListener((msg) => {
    if (!msg || msg.target !== 'popup') return;
    if (msg.type === 'STATE') {
      state = msg.state;
      renderState();
    } else if (msg.type === 'SAVED') {
      renderLibrary();
    }
  });
}

$('openBtn').addEventListener('click', async () => {
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    const res = await chrome.runtime.sendMessage({ type: 'OPEN_RECORDER', tabId: tab && tab.id });
    if (!res || !res.ok) throw new Error((res && res.error) || 'Gagal membuka jendela');
    window.close();
  } catch (err) {
    notice('Gagal membuka jendela rekam: ' + String((err && err.message) || err));
  }
});

async function saveSettings(patch) {
  try {
    const res = await chrome.runtime.sendMessage({ type: 'SET_SETTINGS', patch });
    if (res && res.ok) settings = res.settings;
  } catch (err) {
    notice('Gagal menyimpan pengaturan: ' + String((err && err.message) || err));
  }
}

$('format').addEventListener('change', () => {
  syncBitrate();
  saveSettings({ format: $('format').value });
});

$('bitrate').addEventListener('change', () => {
  saveSettings({ bitrate: Number($('bitrate').value) });
});

$('autoDownload').addEventListener('change', () => {
  saveSettings({ autoDownload: $('autoDownload').checked });
});

boot().catch((err) => {
  notice('Gagal memuat popup: ' + String((err && err.message) || err));
});
