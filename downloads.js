const liveUrls = new Set();

export async function downloadBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  liveUrls.add(url);
  try {
    await chrome.downloads.download({ url, filename, saveAs: false });
  } catch (err) {
    // Fallback kalau downloads API menolak (mis. URL blob sudah tidak valid).
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    setTimeout(() => {
      URL.revokeObjectURL(url);
      liveUrls.delete(url);
    }, 120000);
  }
}

export function formatBytes(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  let value = bytes;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i += 1;
  }
  const digits = value >= 10 || i === 0 ? 0 : 1;
  return `${value.toFixed(digits).replace('.', ',')} ${units[i]}`;
}

export function formatDuration(seconds) {
  const total = Math.max(0, Math.round(seconds || 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}

export function safeFilename(name, fallback = 'rekaman') {
  const cleaned = String(name || '')
    .replace(/[\\/:*?"<>|]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 120);
  return cleaned || fallback;
}
