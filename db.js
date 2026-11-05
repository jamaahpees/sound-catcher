const DB_NAME = 'songcatcher';
const DB_VERSION = 1;
const META = 'meta';
const BLOBS = 'blobs';

let dbPromise = null;

function open() {
  if (dbPromise) return dbPromise;
  dbPromise = new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(META)) {
        db.createObjectStore(META, { keyPath: 'id' });
      }
      if (!db.objectStoreNames.contains(BLOBS)) {
        db.createObjectStore(BLOBS);
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => {
      dbPromise = null;
      reject(req.error);
    };
  });
  return dbPromise;
}

function request(req) {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function transactionDone(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error || new Error('Transaksi dibatalkan'));
  });
}

export async function saveRecording(meta, blob) {
  const db = await open();
  const tx = db.transaction([META, BLOBS], 'readwrite');
  tx.objectStore(META).put(meta);
  tx.objectStore(BLOBS).put(blob, meta.id);
  await transactionDone(tx);
  return meta;
}

export async function listRecordings() {
  const db = await open();
  const tx = db.transaction(META, 'readonly');
  const items = await request(tx.objectStore(META).getAll());
  await transactionDone(tx);
  return items.sort((a, b) => b.createdAt - a.createdAt);
}

export async function getRecordingBlob(id) {
  const db = await open();
  const tx = db.transaction(BLOBS, 'readonly');
  const blob = await request(tx.objectStore(BLOBS).get(id));
  await transactionDone(tx);
  return blob || null;
}

export async function deleteRecording(id) {
  const db = await open();
  const tx = db.transaction([META, BLOBS], 'readwrite');
  tx.objectStore(META).delete(id);
  tx.objectStore(BLOBS).delete(id);
  await transactionDone(tx);
}

export async function clearRecordings() {
  const db = await open();
  const tx = db.transaction([META, BLOBS], 'readwrite');
  tx.objectStore(META).clear();
  tx.objectStore(BLOBS).clear();
  await transactionDone(tx);
}
