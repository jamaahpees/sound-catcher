# capture_sound

Dua cara merekam audio yang diputar di komputer, keduanya bisa menyimpan **WAV** atau **MP3**. Pakai salah satu, tidak perlu keduanya.

## 1. Aplikasi Windows (utama)

Ada di folder `desktop/`. Merekam audio output lewat WASAPI loopback, jadi apa pun yang keluar ke speaker ikut terekam: Spotify desktop, YouTube di browser, VLC, termasuk audio ber-DRM.

```
desktop\dist\SoundCatcher.exe
```

Tidak ada dialog pemilih sumber, tidak ada batas tab, dan rekaman ditulis langsung ke disk. Metadata lagu diambil dari Windows (SMTC) untuk penamaan file otomatis, senyap di awal dan akhir dibuang otomatis, dan ada editor potong dengan waveform kalau ingin memilih bagian sendiri.

Panduan lengkap, cara build, dan batasannya ada di [desktop/README.md](desktop/README.md).

## 2. Ekstensi Chrome (alternatif)

Ada di root proyek. Merekam audio lewat `getDisplayMedia` dari jendela recorder, cocok kalau Anda ingin merekam satu tab tanpa menangkap suara aplikasi lain, atau memakai sistem selain Windows.

Cara pasang: `chrome://extensions` → Developer mode → **Load unpacked** → pilih folder ini.

Panduan lengkap ada di bagian bawah berkas ini.

---

# Ekstensi Chrome: Song Catcher

Ekstensi Chrome untuk merekam output audio (seluruh layar, satu jendela, atau satu tab) lalu menyimpannya sebagai WAV atau MP3.

## Pasang

1. Buka `chrome://extensions`.
2. Nyalakan **Developer mode**.
3. Klik **Load unpacked**, pilih folder proyek ini.
4. Pin ikon Song Catcher di toolbar.

## Pakai

1. Klik ikon ekstensi, lalu **Buka jendela rekam**.
2. Pilih format (MP3 atau WAV) dan bitrate MP3.
3. Klik **Mulai Rekam**. Chrome menampilkan pemilih sumber.
4. Pilih **Seluruh layar** atau **Jendela**, lalu **centang "Bagikan audio sistem"**. Tanpa centang itu, audio tidak ikut terekam.
   Untuk merekam satu tab saja, pilih tab tersebut dan centang **"Bagikan audio tab"**.
5. Putar lagu. Klik **Stop & Simpan** setelah selesai.

Hasil rekaman masuk ke daftar di popup ekstensi dan otomatis diunduh ke folder Downloads kalau opsi **Unduh otomatis** aktif.

Kalau judul lagu tersedia di halaman (banyak situs streaming mengisi `navigator.mediaSession`), nama file otomatis menjadi `Artis - Judul.mp3`. Kalau tidak, dipakai `Rekaman 2026-11-05 21.14.mp3`.

## Format

| Format | Ukuran per menit | Catatan |
| --- | --- | --- |
| MP3 128 kbps | ± 1 MB | paling kecil |
| MP3 192 kbps | ± 1,4 MB | default |
| MP3 256/320 kbps | ± 1,9 / 2,4 MB | kualitas lebih tinggi |
| WAV 48 kHz stereo | ± 11,5 MB | PCM 16-bit, tanpa kompresi |

MP3 di-encode dengan `lamejs` yang dibundel di `lib/`. Chrome tidak punya encoder MP3 bawaan dan MV3 melarang memuat script dari CDN, jadi pustaka ini harus ada di dalam paket ekstensi.

## Cara kerja

1. `getDisplayMedia({ video, audio, systemAudio: 'include' })` di jendela recorder mengambil audio sumber.
2. `AudioWorklet` (`worklet/pcm-capture.js`) mengubah stream menjadi blok PCM 4096 sampel, lalu dikirim ke Web Worker.
3. Worker (`worker/encoder.js`) menulis WAV (header RIFF + PCM 16-bit) atau meng-encode MP3 per blok, sehingga UI tidak tersendat.
4. Hasil disimpan sebagai Blob di IndexedDB (`db.js`), lalu diunduh lewat `chrome.downloads`.

Tidak ada audio yang diputar ulang oleh ekstensi, jadi tidak ada suara dobel atau echo.

## Batasan

- **Konten DRM tidak bisa direkam.** Spotify Web, Netflix, dan Apple Music memakai Widevine; audio yang tertangkap akan kosong.
- **Audio sistem hanya tersedia di Windows dan ChromeOS.** Di macOS Chrome tidak menyediakan audio layar tanpa driver virtual; yang bisa direkam hanya audio tab. Di Linux butuh PipeWire.
- Jendela recorder harus tetap terbuka selama merekam. Menutupnya di tengah rekaman akan membuang hasil rekaman (ada dialog konfirmasi).
- WAV menyimpan seluruh audio di memori sebelum disimpan. Rekaman 10 menit sekitar 115 MB; setelah 30 menit recorder memberi peringatan.
- Rekaman mengikuti kualitas audio yang keluar dari sumber, bukan file asli dari server.

Gunakan hanya untuk konten yang Anda berhak merekamnya.

## Uji

```powershell
node tools/smoke-test.mjs
```

Skrip memuat ekstensi ke Chrome headless, lalu memeriksa: halaman popup dan recorder termuat, service worker merespons, encoder menghasilkan WAV dan MP3 yang bisa didekode kembali, IndexedDB menyimpan dan menghapus berkas, serta unduhan blob berjalan.

Butuh Chrome terpasang di `C:\Program Files\Google\Chrome\Application\chrome.exe`. Kalau lokasinya lain, set `CHROME_PATH`.

## Struktur

```
manifest.json          MV3, izin minimal
background.js          service worker: state, buka jendela rekam, metadata lagu
recorder.html/.js      jendela rekam: getDisplayMedia, meter, timer, simpan
popup.html/.js         daftar rekaman, pengaturan, now playing
ui.css                 tampilan kedua halaman
db.js                  IndexedDB (meta + blob)
downloads.js           unduh blob, format ukuran/durasi/nama file
worker/encoder.js      encoder WAV dan MP3
worklet/pcm-capture.js pengambil PCM dari stream
lib/lame.min.js        encoder MP3
icons/                 ikon ekstensi
desktop/               aplikasi Windows (lihat desktop/README.md)
```
