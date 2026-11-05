import { saveRecording } from './db.js';
import { downloadBlob, formatBytes, formatDuration, safeFilename } from './downloads.js';

const $ = (id) => document.getElementById(id);

const BAR_PATTERN = [
  0.35, 0.6, 0.85, 1, 0.7, 0.45, 0.9, 0.65, 0.4, 0.8,
  1, 0.75, 0.5, 0.7, 0.95, 0.6, 0.35, 0.8, 0.9, 0.55,
];

let session = null;
let recording = false;
let startedAt = 0;
let timerId = null;
let rafId = null;
let donePromise = null;
let flushedResolve = null;
let lastSaved = null;
let settings = { format: 'mp3', bitrate: 192, autoDownload: true };
let nowPlaying = null;

function notice(message) {
  const el = $('notice');
  el.textContent = message;
  el.hidden = !message;
}

function post(message) {
  chrome.runtime.sendMessage(message).catch(() => {});
}

function clearNotice() {
  notice('');
}

function setBusy(busy) {
  $('actionBtn').disabled = busy;
  $('format').disabled = busy;
  $('bitrate').disabled = busy;
}

function setStatus(text, tone = 'idle') {
  const pill = $('statusPill');
  pill.textContent = text;
  pill.dataset.tone = tone;
}

function renderRecordingUI() {
  const btn = $('actionBtn');
  if (recording) {
    btn.textContent = 'Stop & Simpan';
    btn.classList.add('stop');
    btn.classList.remove('big');
    setStatus('Merekam', 'rec');
  } else {
    btn.textContent = 'Mulai Rekam';
    btn.classList.remove('stop');
    btn.classList.add('big');
    setStatus('Siap', 'idle');
  }
}

function tick() {
  const elapsed = recording ? (Date.now() - startedAt) / 1000 : 0;
  $('timer').textContent = formatDuration(elapsed);
  if (recording && elapsed > 1800) {
    $('hint').textContent =
      'Rekaman sudah lewat 30 menit. Format WAV memakan banyak memori, sebaiknya stop dan simpan sekarang.';
  }
}

function startTimer() {
  stopTimer();
  tick();
  timerId = setInterval(tick, 250);
}

function stopTimer() {
  if (timerId) clearInterval(timerId);
  timerId = null;
}

function startMeter() {
  if (!session) return;
  const analyser = session.analyser;
  const buffer = new Float32Array(analyser.fftSize);
  const bars = Array.from(document.querySelectorAll('#meter span'));

  const loop = () => {
    if (!session) return;
    analyser.getFloatTimeDomainData(buffer);
    let sum = 0;
    for (let i = 0; i < buffer.length; i += 1) sum += buffer[i] * buffer[i];
    const rms = Math.sqrt(sum / buffer.length);
    const level = Math.min(1, Math.pow(rms * 3.4, 0.65));
    bars.forEach((bar, index) => {
      const pattern = BAR_PATTERN[index % BAR_PATTERN.length];
      const value = Math.max(0.05, level * pattern);
      bar.style.transform = `scaleY(${value.toFixed(3)})`;
    });
    rafId = requestAnimationFrame(loop);
  };
  rafId = requestAnimationFrame(loop);
}

function stopMeter() {
  if (rafId) cancelAnimationFrame(rafId);
  rafId = null;
  document.querySelectorAll('#meter span').forEach((bar) => {
    bar.style.transform = 'scaleY(0.05)';
  });
}

function sourceLabel(settingsInfo) {
  if (!settingsInfo) return '';
  const surface = settingsInfo.displaySurface;
  if (surface === 'monitor') return 'Audio sistem';
  if (surface === 'window') return 'Audio jendela';
  if (surface === 'browser') return 'Audio tab';
  return 'Audio output';
}

async function start() {
  if (recording) return;
  clearNotice();
  setBusy(true);
  lastSaved = null;
  $('result').hidden = true;

  const format = $('format').value;
  const bitrate = Number($('bitrate').value);

  let stream;
  try {
    stream = await navigator.mediaDevices.getDisplayMedia({
      video: { frameRate: 5 },
      audio: {
        echoCancellation: false,
        noiseSuppression: false,
        autoGainControl: false,
        channelCount: 2,
        sampleRate: 48000,
      },
      systemAudio: 'include',
      selfBrowserSurface: 'exclude',
      surfaceSwitching: 'include',
      monitorTypeSurfaces: 'include',
    });
  } catch (err) {
    setBusy(false);
    notice('Pemilihan sumber dibatalkan. Tekan "Mulai Rekam" untuk mencoba lagi.');
    return;
  }

  const audioTrack = stream.getAudioTracks()[0];
  if (!audioTrack) {
    stream.getTracks().forEach((track) => track.stop());
    setBusy(false);
    notice(
      'Sumber yang dipilih tidak membawa audio. Ulangi, lalu centang "Bagikan audio sistem" (Windows) atau "Bagikan audio tab".'
    );
    return;
  }

  let ctx;
  let worker;
  try {
    ctx = new AudioContext({ latencyHint: 'playback' });
    await ctx.resume();
    await ctx.audioWorklet.addModule('worklet/pcm-capture.js');

    const source = ctx.createMediaStreamSource(new MediaStream([audioTrack]));
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 1024;

    const node = new AudioWorkletNode(ctx, 'pcm-capture', {
      numberOfInputs: 1,
      numberOfOutputs: 1,
      outputChannelCount: [2],
      channelCount: 2,
      channelCountMode: 'explicit',
    });

    const sink = ctx.createGain();
    sink.gain.value = 0;

    source.connect(analyser);
    source.connect(node);
    node.connect(sink).connect(ctx.destination);

    worker = new Worker('worker/encoder.js');
    donePromise = new Promise((resolve, reject) => {
      worker.onmessage = (event) => {
        const msg = event.data || {};
        if (msg.type === 'done') resolve(msg);
        else if (msg.type === 'error') reject(new Error(msg.message));
        else if (msg.type === 'progress') {
          $('timer').textContent = formatDuration(msg.seconds);
        }
      };
    });
    donePromise.catch(() => {});

    node.port.onmessage = (event) => {
      const msg = event.data || {};
      if (msg.type === 'pcm') {
        worker.postMessage(
          { type: 'pcm', channels: msg.channels, data: msg.data },
          msg.data.map((channel) => channel.buffer)
        );
      } else if (msg.type === 'flushed' && flushedResolve) {
        flushedResolve();
      }
    };

    worker.postMessage({
      type: 'start',
      sampleRate: ctx.sampleRate,
      channels: 2,
      format,
      bitrate,
    });

    session = {
      ctx,
      stream,
      source,
      analyser,
      node,
      sink,
      worker,
      format,
      bitrate,
      audioTrack,
    };
  } catch (err) {
    try {
      stream.getTracks().forEach((track) => track.stop());
      if (worker) worker.terminate();
      if (ctx) await ctx.close();
    } catch {
      /* diabaikan */
    }
    setBusy(false);
    notice('Gagal memulai perekaman: ' + String((err && err.message) || err));
    return;
  }

  recording = true;
  startedAt = Date.now();
  startTimer();
  startMeter();
  renderRecordingUI();
  setBusy(false);

  const surface = audioTrack.getSettings();
  $('trackTitle').textContent = 'Merekam ' + sourceLabel(surface).toLowerCase();
  $('trackArtist').textContent = 'Biarkan jendela ini terbuka selama rekaman berjalan.';
  $('hint').textContent =
    'Rekaman berjalan. Klik "Stop & Simpan" kalau lagu sudah selesai.';

  const stopOnEnd = () => {
    if (recording) stop();
  };
  audioTrack.addEventListener('ended', stopOnEnd);
  stream.getVideoTracks().forEach((track) => track.addEventListener('ended', stopOnEnd));

  post({
    type: 'REC_STATE',
    patch: {
      status: 'recording',
      startedAt,
      format,
      bitrate,
      title: nowPlaying && nowPlaying.title ? nowPlaying.title : '',
      artist: nowPlaying && nowPlaying.artist ? nowPlaying.artist : '',
      artwork: nowPlaying && nowPlaying.artwork ? nowPlaying.artwork : '',
      source: sourceLabel(surface),
    },
  });

  loadNowPlaying();
}

async function loadNowPlaying() {
  try {
    const res = await chrome.runtime.sendMessage({ type: 'GET_NOW_PLAYING' });
    const now = res && res.ok ? res.now : null;
    if (!now) return;
    nowPlaying = now;
    if (now.title) {
      $('trackTitle').textContent = now.title;
      $('trackArtist').textContent = now.artist || 'Artis tidak diketahui';
      post({
        type: 'REC_STATE',
        patch: { title: now.title, artist: now.artist || '', artwork: now.artwork || '' },
      });
    }
  } catch {
    /* metadata opsional */
  }
}

function buildName(ext) {
  const stamp = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  const time = `${stamp.getFullYear()}-${pad(stamp.getMonth() + 1)}-${pad(stamp.getDate())} ${pad(
    stamp.getHours()
  )}.${pad(stamp.getMinutes())}`;
  const title = nowPlaying && nowPlaying.title ? nowPlaying.title : '';
  const artist = nowPlaying && nowPlaying.artist ? nowPlaying.artist : '';
  const base = title ? (artist ? `${artist} - ${title}` : title) : `Rekaman ${time}`;
  return `${safeFilename(base)}.${ext}`;
}

function cleanup(current) {
  try {
    current.node.port.postMessage({ type: 'flush' });
  } catch {
    /* diabaikan */
  }
  try {
    current.node.disconnect();
    current.source.disconnect();
    current.sink.disconnect();
    current.analyser.disconnect();
  } catch {
    /* diabaikan */
  }
  try {
    current.stream.getTracks().forEach((track) => track.stop());
  } catch {
    /* diabaikan */
  }
  try {
    current.worker.terminate();
  } catch {
    /* diabaikan */
  }
  try {
    current.ctx.close();
  } catch {
    /* diabaikan */
  }
}

async function stop() {
  if (!recording || !session) return;
  recording = false;
  setBusy(true);
  stopTimer();
  stopMeter();
  renderRecordingUI();
  setStatus('Memproses', 'busy');
  $('trackArtist').textContent = 'Menyimpan rekaman...';

  const current = session;
  session = null;

  try {
    const flushed = new Promise((resolve) => {
      flushedResolve = resolve;
      setTimeout(resolve, 500);
    });
    current.node.port.postMessage({ type: 'flush' });
    await flushed;
    flushedResolve = null;

    current.worker.postMessage({ type: 'stop' });
    const result = await donePromise;
    donePromise = null;

    if (!result.blob || result.blob.size < 1200) {
      throw new Error('Rekaman kosong. Pastikan ada audio yang diputar saat merekam.');
    }

    const meta = {
      id: crypto.randomUUID(),
      name: buildName(result.ext),
      ext: result.ext,
      mime: result.mime,
      size: result.size,
      duration: result.duration,
      format: current.format,
      bitrate: current.bitrate,
      createdAt: Date.now(),
      title: (nowPlaying && nowPlaying.title) || '',
      artist: (nowPlaying && nowPlaying.artist) || '',
    };

    await saveRecording(meta, result.blob);
    lastSaved = { meta, blob: result.blob };

    showResult(meta);
    post({ type: 'REC_SAVED', recording: meta });

    if ($('autoDownload').checked) {
      try {
        await downloadBlob(result.blob, meta.name);
      } catch (err) {
        notice('Rekaman tersimpan, tetapi unduhan otomatis gagal: ' + String((err && err.message) || err));
      }
    }
  } catch (err) {
    notice('Gagal menyelesaikan rekaman: ' + String((err && err.message) || err));
    post({ type: 'REC_STATE', patch: { status: 'idle', startedAt: null } });
  } finally {
    cleanup(current);
    setBusy(false);
    $('hint').textContent =
      'Di jendela pemilih Chrome, pilih "Seluruh layar" atau "Jendela", lalu centang "Bagikan audio sistem".';
    setStatus('Siap', 'idle');
  }
}

function showResult(meta) {
  $('result').hidden = false;
  $('resultName').textContent = meta.name;
  $('resultMeta').textContent = `${formatDuration(meta.duration)} · ${formatBytes(
    meta.size
  )} · ${meta.format.toUpperCase()}`;
  $('trackTitle').textContent = meta.title || 'Rekaman tersimpan';
  $('trackArtist').textContent = meta.artist || 'Buka popup ekstensi untuk melihat daftar rekaman.';
}

function reset() {
  $('result').hidden = true;
  $('timer').textContent = '00:00';
  $('trackTitle').textContent = 'Belum ada audio';
  $('trackArtist').textContent = 'Tekan Mulai Rekam, lalu pilih sumber audio.';
  nowPlaying = null;
}

async function init() {
  const res = await chrome.runtime.sendMessage({ type: 'GET_STATE' });
  if (res && res.ok) {
    settings = res.settings;
    if (res.now) nowPlaying = res.now;
    if (res.state && res.state.status === 'recording') {
      // Jendela ini baru dibuka, jadi tidak ada sesi aktif di sini.
      post({ type: 'REC_STATE', patch: { status: 'idle', startedAt: null } });
    }
  }

  $('format').value = settings.format;
  $('bitrate').value = String(settings.bitrate);
  $('autoDownload').checked = settings.autoDownload;
  syncBitrate();

  $('meter').innerHTML = '';
  for (let i = 0; i < BAR_PATTERN.length; i += 1) {
    const bar = document.createElement('span');
    bar.style.transform = 'scaleY(0.05)';
    $('meter').appendChild(bar);
  }

  if (nowPlaying && nowPlaying.title) {
    $('trackTitle').textContent = nowPlaying.title;
    $('trackArtist').textContent = nowPlaying.artist || 'Artis tidak diketahui';
  }

  renderRecordingUI();
}

function syncBitrate() {
  const isWav = $('format').value === 'wav';
  $('bitrateRow').hidden = isWav;
  $('bitrate').disabled = isWav;
}

$('actionBtn').addEventListener('click', () => {
  if (recording) stop();
  else start();
});

$('format').addEventListener('change', () => {
  syncBitrate();
  post({
    type: 'SET_SETTINGS',
    patch: { format: $('format').value },
  });
});

$('bitrate').addEventListener('change', () => {
  post({
    type: 'SET_SETTINGS',
    patch: { bitrate: Number($('bitrate').value) },
  });
});

$('autoDownload').addEventListener('change', () => {
  post({
    type: 'SET_SETTINGS',
    patch: { autoDownload: $('autoDownload').checked },
  });
});

$('downloadBtn').addEventListener('click', async () => {
  if (!lastSaved) return;
  try {
    await downloadBlob(lastSaved.blob, lastSaved.meta.name);
  } catch (err) {
    notice('Gagal mengunduh: ' + String((err && err.message) || err));
  }
});

$('newBtn').addEventListener('click', () => {
  reset();
  clearNotice();
});

window.addEventListener('beforeunload', (event) => {
  if (recording) {
    event.preventDefault();
    event.returnValue = '';
  }
});

init().catch((err) => {
  notice('Gagal memuat: ' + String((err && err.message) || err));
});
