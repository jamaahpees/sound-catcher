// Smoke test: memuat ekstensi di Chrome, lalu menguji encoder dan penyimpanan.
// Jalankan: node tools/smoke-test.mjs
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

const EXT = path.resolve(import.meta.dirname, '..');
const CHROME =
  process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = Number(process.env.CDP_PORT || 9333);
const profile = await mkdtemp(path.join(tmpdir(), 'songcatcher-'));

const chrome = spawn(
  CHROME,
  [
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--headless=new',
    '--enable-unsafe-extension-debugging',
    '--autoplay-policy=no-user-gesture-required',
    'about:blank',
  ],
  { stdio: 'ignore' }
);

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForDevtools() {
  for (let i = 0; i < 80; i += 1) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/version`);
      if (res.ok) return res.json();
    } catch {
      /* belum siap */
    }
    await sleep(250);
  }
  throw new Error('DevTools tidak merespons');
}

class Cdp {
  constructor(wsUrl) {
    this.ws = new WebSocket(wsUrl);
    this.id = 0;
    this.pending = new Map();
    this.events = [];
    this.ws.addEventListener('message', (event) => {
      const msg = JSON.parse(event.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id);
        this.pending.delete(msg.id);
        if (msg.error) reject(new Error(msg.error.message));
        else resolve(msg.result);
      } else if (msg.method) {
        this.events.push(msg);
      }
    });
  }

  ready() {
    return new Promise((resolve, reject) => {
      this.ws.addEventListener('open', resolve, { once: true });
      this.ws.addEventListener('error', reject, { once: true });
    });
  }

  send(method, params = {}) {
    this.id += 1;
    const id = this.id;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression) {
    const result = await this.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
      userGesture: true,
    });
    if (result.exceptionDetails) {
      const detail = result.exceptionDetails;
      throw new Error(
        (detail.exception && detail.exception.description) || detail.text || 'Evaluasi gagal'
      );
    }
    return result.result.value;
  }
}

async function openPage(url) {
  const res = await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' });
  const target = await res.json();
  const cdp = new Cdp(target.webSocketDebuggerUrl);
  await cdp.ready();
  await cdp.send('Runtime.enable');
  await cdp.send('Page.enable');
  await cdp.send('Page.navigate', { url });
  await sleep(1500);
  return cdp;
}

const ENCODER_TEST = `(async () => {
  const analyze = async (blob) => {
    const ab = await blob.arrayBuffer();
    const ctx = new OfflineAudioContext(2, 48000, 48000);
    const buffer = await ctx.decodeAudioData(ab.slice(0));
    const data = buffer.getChannelData(0);
    const from = Math.floor(buffer.sampleRate * 0.4);
    const to = Math.floor(buffer.sampleRate * 1.4);
    let crossings = 0;
    let peak = 0;
    for (let i = 0; i < data.length; i += 1) {
      const v = Math.abs(data[i]);
      if (v > peak) peak = v;
      if (i > from && i < to && ((data[i - 1] < 0) !== (data[i] < 0))) crossings += 1;
    }
    return {
      duration: Number(buffer.duration.toFixed(3)),
      hz: Math.round(crossings / 2 / ((to - from) / buffer.sampleRate)),
      peak: Number(peak.toFixed(3)),
      channels: buffer.numberOfChannels,
    };
  };
  const out = {};
  for (const cfg of [{ format: 'wav', bitrate: 0 }, { format: 'mp3', bitrate: 192 }]) {
    const worker = new Worker('worker/encoder.js');
    const done = new Promise((resolve, reject) => {
      worker.onmessage = (event) => {
        const msg = event.data || {};
        if (msg.type === 'done') resolve(msg);
        else if (msg.type === 'error') reject(new Error(msg.message));
      };
      worker.onerror = (event) => reject(new Error('worker error: ' + event.message));
    });
    worker.postMessage({ type: 'start', sampleRate: 48000, channels: 2, format: cfg.format, bitrate: cfg.bitrate });
    const block = 4096;
    const blocks = Math.ceil((48000 * 2) / block);
    for (let b = 0; b < blocks; b += 1) {
      const left = new Float32Array(block);
      const right = new Float32Array(block);
      for (let i = 0; i < block; i += 1) {
        const t = (b * block + i) / 48000;
        const v = Math.sin(2 * Math.PI * 440 * t) * 0.5;
        left[i] = v;
        right[i] = v;
      }
      worker.postMessage({ type: 'pcm', channels: 2, data: [left, right] }, [left.buffer, right.buffer]);
    }
    worker.postMessage({ type: 'stop' });
    const result = await done;
    const head = new Uint8Array(await result.blob.slice(0, 12).arrayBuffer());
    out[cfg.format] = {
      size: result.size,
      duration: Number(result.duration.toFixed(3)),
      ext: result.ext,
      mime: result.mime,
      head: Array.from(head),
      decoded: await analyze(result.blob),
    };
    worker.terminate();
  }
  return out;
})()`;

const DB_TEST = `(async () => {
  const db = await import('./db.js');
  const blob = new Blob([new Uint8Array([1, 2, 3, 4])], { type: 'audio/mpeg' });
  const meta = { id: 'test-1', name: 'test.mp3', ext: 'mp3', size: 4, duration: 1, format: 'mp3', createdAt: Date.now(), title: '', artist: '' };
  await db.saveRecording(meta, blob);
  const items = await db.listRecordings();
  const stored = await db.getRecordingBlob('test-1');
  const bytes = stored ? Array.from(new Uint8Array(await stored.arrayBuffer())) : null;
  await db.deleteRecording('test-1');
  const after = await db.listRecordings();
  return { count: items.length, name: items[0] && items[0].name, bytes, afterDelete: after.length };
})()`;

const SW_TEST = `(async () => {
  const res = await chrome.runtime.sendMessage({ type: 'GET_STATE' });
  return { ok: res && res.ok, status: res && res.state && res.state.status, format: res && res.settings && res.settings.format };
})()`;

const OPEN_TEST = `(async () => {
  const res = await chrome.runtime.sendMessage({ type: 'OPEN_RECORDER' });
  if (res && res.windowId != null) {
    setTimeout(() => chrome.windows.remove(res.windowId), 500);
  }
  return res;
})()`;

const DOWNLOAD_TEST = `(async () => {
  const blob = new Blob([new Uint8Array([1, 2, 3, 4, 5])], { type: 'audio/mpeg' });
  const url = URL.createObjectURL(blob);
  const id = await chrome.downloads.download({ url, filename: 'songcatcher-test.mp3', saveAs: false });
  setTimeout(() => URL.revokeObjectURL(url), 2000);
  return { downloadId: id };
})()`;

const results = [];
async function record(name, fn) {
  try {
    results.push({ name, ok: true, value: await fn() });
  } catch (error) {
    results.push({ name, ok: false, error: String((error && error.message) || error) });
  }
}

try {
  const version = await waitForDevtools();
  const browser = new Cdp(version.webSocketDebuggerUrl);
  await browser.ready();

  const loaded = await browser.send('Extensions.loadUnpacked', { path: EXT });
  const extId = loaded.id;
  await sleep(1200);

  const cdpRecorder = await openPage(`chrome-extension://${extId}/recorder.html`);
  const cdpPopup = await openPage(`chrome-extension://${extId}/popup.html`);

  await record('lokasi halaman recorder', () => cdpRecorder.evaluate('location.href'));
  await record('halaman popup termuat', () =>
    cdpPopup.evaluate('document.getElementById("openBtn").textContent')
  );
  await record('service worker merespons', () => cdpPopup.evaluate(SW_TEST));
  await record('jendela rekam bisa dibuka', () => cdpPopup.evaluate(OPEN_TEST));
  await record('unduhan blob berjalan', () => cdpPopup.evaluate(DOWNLOAD_TEST));
  await record('encoder WAV + MP3', () => cdpRecorder.evaluate(ENCODER_TEST));
  await record('indexeddb simpan/baca/hapus', () => cdpRecorder.evaluate(DB_TEST));

  const exceptions = [...cdpRecorder.events, ...cdpPopup.events]
    .filter((event) => event.method === 'Runtime.exceptionThrown')
    .map((event) => event.params.exceptionDetails.text);
  results.push({ name: 'tanpa exception di console', ok: exceptions.length === 0, value: exceptions });

  console.log(JSON.stringify({ extId, results }, null, 2));
  if (results.some((item) => !item.ok)) process.exitCode = 1;
} catch (error) {
  console.error('GAGAL:', (error && error.message) || error);
  process.exitCode = 1;
} finally {
  chrome.kill();
  await sleep(400);
  await rm(profile, { recursive: true, force: true }).catch(() => {});
}
