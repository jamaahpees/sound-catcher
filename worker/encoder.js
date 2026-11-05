importScripts('../lib/lame.min.js');

let format = 'mp3';
let bitrate = 192;
let channels = 2;
let sampleRate = 48000;

let mp3 = null;
let mp3Chunks = [];
let wavChunks = [];
let wavBytes = 0;
let totalFrames = 0;
let lastProgressAt = 0;

self.onmessage = (event) => {
  const msg = event.data || {};
  try {
    if (msg.type === 'start') init(msg);
    else if (msg.type === 'pcm') push(msg);
    else if (msg.type === 'stop') finish();
  } catch (err) {
    self.postMessage({ type: 'error', message: String((err && err.message) || err) });
  }
};

function init(msg) {
  format = msg.format === 'wav' ? 'wav' : 'mp3';
  bitrate = Number(msg.bitrate) || 192;
  channels = Number(msg.channels) || 2;
  sampleRate = Number(msg.sampleRate) || 48000;

  mp3 = null;
  mp3Chunks = [];
  wavChunks = [];
  wavBytes = 0;
  totalFrames = 0;
  lastProgressAt = 0;

  if (format === 'mp3') {
    if (typeof lamejs === 'undefined' || !lamejs.Mp3Encoder) {
      throw new Error('Encoder MP3 gagal dimuat');
    }
    mp3 = new lamejs.Mp3Encoder(channels, sampleRate, bitrate);
  }

  self.postMessage({ type: 'ready', format, bitrate, sampleRate, channels });
}

function toInt16(float32) {
  const out = new Int16Array(float32.length);
  for (let i = 0; i < float32.length; i += 1) {
    const sample = float32[i] < -1 ? -1 : float32[i] > 1 ? 1 : float32[i];
    out[i] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
  }
  return out;
}

function push(msg) {
  const data = msg.data || [];
  if (!data.length) return;

  const frames = data[0].length;
  if (!frames) return;
  totalFrames += frames;

  if (format === 'wav') {
    const channelCount = data.length;
    const interleaved = new Int16Array(frames * channelCount);
    const converted = data.map(toInt16);
    for (let i = 0; i < frames; i += 1) {
      const base = i * channelCount;
      for (let c = 0; c < channelCount; c += 1) {
        interleaved[base + c] = converted[c][i];
      }
    }
    wavChunks.push(interleaved);
    wavBytes += interleaved.byteLength;
  } else if (mp3) {
    const left = toInt16(data[0]);
    let right = data.length > 1 ? toInt16(data[1]) : null;
    if (!right && channels > 1) right = left;
    const encoded = right ? mp3.encodeBuffer(left, right) : mp3.encodeBuffer(left);
    if (encoded && encoded.length) mp3Chunks.push(encoded.slice());
  }

  const now = Date.now();
  if (now - lastProgressAt > 500) {
    lastProgressAt = now;
    self.postMessage({
      type: 'progress',
      seconds: totalFrames / sampleRate,
      bytes: format === 'wav' ? wavBytes : undefined,
    });
  }
}

function wavHeader(dataBytes, channelCount, rate) {
  const buffer = new ArrayBuffer(44);
  const view = new DataView(buffer);
  const writeString = (offset, text) => {
    for (let i = 0; i < text.length; i += 1) {
      view.setUint8(offset + i, text.charCodeAt(i));
    }
  };
  const byteRate = rate * channelCount * 2;

  writeString(0, 'RIFF');
  view.setUint32(4, 36 + dataBytes, true);
  writeString(8, 'WAVE');
  writeString(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channelCount, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, byteRate, true);
  view.setUint16(32, channelCount * 2, true);
  view.setUint16(34, 16, true);
  writeString(36, 'data');
  view.setUint32(40, dataBytes, true);
  return buffer;
}

function finish() {
  const duration = totalFrames / sampleRate;
  let blob;
  let ext;
  let mime;

  if (format === 'wav') {
    const header = wavHeader(wavBytes, channels, sampleRate);
    blob = new Blob([header, ...wavChunks], { type: 'audio/wav' });
    ext = 'wav';
    mime = 'audio/wav';
  } else {
    if (!mp3) throw new Error('Encoder MP3 belum siap');
    const tail = mp3.flush();
    if (tail && tail.length) mp3Chunks.push(tail.slice());
    blob = new Blob(mp3Chunks, { type: 'audio/mpeg' });
    ext = 'mp3';
    mime = 'audio/mpeg';
  }

  self.postMessage({
    type: 'done',
    blob,
    duration,
    frames: totalFrames,
    size: blob.size,
    ext,
    mime,
  });

  mp3Chunks = [];
  wavChunks = [];
  wavBytes = 0;
  totalFrames = 0;
}
