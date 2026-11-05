const DEFAULT_SETTINGS = {
  format: 'mp3',
  bitrate: 192,
  autoDownload: true,
};

const IDLE_STATE = {
  status: 'idle',
  startedAt: null,
  format: 'mp3',
  bitrate: 192,
  duration: 0,
  title: '',
  artist: '',
  artwork: '',
  source: '',
  lastSaved: null,
};

let recorderWindowId = null;
let sourceTabId = null;

async function getSettings() {
  const { settings } = await chrome.storage.local.get('settings');
  return { ...DEFAULT_SETTINGS, ...(settings || {}) };
}

async function setSettings(patch) {
  const next = { ...(await getSettings()), ...patch };
  await chrome.storage.local.set({ settings: next });
  return next;
}

async function getState() {
  const { recorderState } = await chrome.storage.session.get('recorderState');
  return { ...IDLE_STATE, ...(recorderState || {}) };
}

async function setState(patch) {
  const next = { ...(await getState()), ...patch };
  await chrome.storage.session.set({ recorderState: next });
  updateBadge(next);
  notifyPopup({ type: 'STATE', state: next });
  return next;
}

function notifyPopup(payload) {
  chrome.runtime.sendMessage({ target: 'popup', ...payload }).catch(() => {});
}

function updateBadge(state) {
  const recording = state.status === 'recording';
  chrome.action.setBadgeText({ text: recording ? 'REC' : '' }).catch(() => {});
  chrome.action.setBadgeBackgroundColor({ color: '#e11d48' }).catch(() => {});
}

// Fungsi ini disuntikkan ke halaman, jadi harus mandiri (tanpa closure).
function readPageMedia() {
  const meta = navigator.mediaSession && navigator.mediaSession.metadata;
  const pickArtwork = (list) => {
    if (!list || !list.length) return '';
    const sorted = [...list].sort((a, b) => {
      const size = (s) => {
        const m = /(\d+)x(\d+)/.exec(s || '');
        return m ? Number(m[1]) * Number(m[2]) : 0;
      };
      return size(b.sizes) - size(a.sizes);
    });
    return sorted[0].src || '';
  };
  return {
    title: meta ? meta.title || '' : '',
    artist: meta ? meta.artist || '' : '',
    album: meta ? meta.album || '' : '',
    artwork: meta ? pickArtwork(meta.artwork) : '',
  };
}

async function readNowPlaying(tabId) {
  try {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId },
      func: readPageMedia,
    });
    const info = result && result.result;
    if (!info) return null;
    return {
      title: info.title || '',
      artist: info.artist || '',
      album: info.album || '',
      artwork: info.artwork || '',
    };
  } catch {
    return null;
  }
}

async function pickActiveTab() {
  if (sourceTabId != null) {
    try {
      const tab = await chrome.tabs.get(sourceTabId);
      if (tab && tab.id) return tab;
    } catch {
      sourceTabId = null;
    }
  }
  let [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab || !tab.id) {
    const tabs = await chrome.tabs.query({ active: true });
    tab = tabs.find((item) => /^https?:/.test(item.url || '')) || tabs[0];
  }
  return tab || null;
}

async function readNowPlayingActive() {
  try {
    const tab = await pickActiveTab();
    if (!tab || !tab.id) return null;
    const info = await readNowPlaying(tab.id);
    return info ? { ...info, tabTitle: tab.title || '', tabId: tab.id } : null;
  } catch {
    return null;
  }
}

async function openRecorder(tabId) {
  if (tabId != null) {
    sourceTabId = tabId;
    await chrome.storage.session.set({ sourceTabId });
  }
  if (recorderWindowId == null) {
    const { recorderWindowId: saved } = await chrome.storage.session.get('recorderWindowId');
    if (saved != null) recorderWindowId = saved;
  }
  if (recorderWindowId != null) {
    try {
      await chrome.windows.get(recorderWindowId);
      await chrome.windows.update(recorderWindowId, { focused: true });
      return { ok: true, windowId: recorderWindowId };
    } catch {
      recorderWindowId = null;
    }
  }
  const win = await chrome.windows.create({
    url: chrome.runtime.getURL('recorder.html'),
    type: 'popup',
    width: 480,
    height: 720,
    focused: true,
  });
  recorderWindowId = win.id;
  await chrome.storage.session.set({ recorderWindowId: win.id });
  return { ok: true, windowId: win.id };
}

chrome.windows.onRemoved.addListener(async (windowId) => {
  if (windowId !== recorderWindowId) return;
  recorderWindowId = null;
  await chrome.storage.session.remove('recorderWindowId');
  const state = await getState();
  if (state.status === 'recording') {
    await setState({ status: 'idle', startedAt: null });
  }
});

chrome.runtime.onInstalled.addListener(async () => {
  const { settings } = await chrome.storage.local.get('settings');
  if (!settings) await chrome.storage.local.set({ settings: DEFAULT_SETTINGS });
});

chrome.storage.session.get('sourceTabId').then(({ sourceTabId: saved }) => {
  if (saved != null) sourceTabId = saved;
});

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || msg.target === 'popup') return undefined;

  (async () => {
    switch (msg.type) {
      case 'GET_STATE': {
        const [state, settings, now] = await Promise.all([
          getState(),
          getSettings(),
          readNowPlayingActive(),
        ]);
        sendResponse({ ok: true, state, settings, now });
        break;
      }
      case 'GET_SETTINGS': {
        sendResponse({ ok: true, settings: await getSettings() });
        break;
      }
      case 'SET_SETTINGS': {
        sendResponse({ ok: true, settings: await setSettings(msg.patch || {}) });
        break;
      }
      case 'OPEN_RECORDER': {
        sendResponse(await openRecorder(msg.tabId));
        break;
      }
      case 'GET_NOW_PLAYING': {
        sendResponse({ ok: true, now: await readNowPlayingActive() });
        break;
      }
      case 'REC_STATE': {
        const next = await setState(msg.patch || {});
        sendResponse({ ok: true, state: next });
        break;
      }
      case 'REC_SAVED': {
        notifyPopup({ type: 'SAVED', recording: msg.recording || null });
        await setState({
          status: 'idle',
          startedAt: null,
          duration: (msg.recording && msg.recording.duration) || 0,
          lastSaved: msg.recording || null,
        });
        sendResponse({ ok: true });
        break;
      }
      default:
        sendResponse({ ok: false, error: 'Perintah tidak dikenal' });
    }
  })().catch((err) => {
    sendResponse({ ok: false, error: String((err && err.message) || err) });
  });

  return true;
});
