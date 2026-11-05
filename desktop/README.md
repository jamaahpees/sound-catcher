# Sound Catcher (Windows)

Aplikasi desktop untuk merekam audio yang keluar ke speaker atau headphone, lalu menyimpannya sebagai **WAV** atau **MP3**. Perekaman memakai WASAPI loopback, jadi yang ditangkap adalah hasil akhir dari perangkat output, bukan satu aplikasi tertentu.

Ini pengganti jalur ekstensi Chrome: tidak ada dialog pemilih sumber, tidak ada batas tab, dan audio Spotify desktop maupun konten ber-DRM ikut terekam karena diambil setelah proses mixing.

## Jalankan

```
desktop\dist\SoundCatcher.exe
```

Build bawaan bersifat framework-dependent, jadi butuh .NET 8 Desktop Runtime. Di mesin ini runtime tersebut sudah ada karena .NET SDK terpasang. Untuk versi yang bisa jalan tanpa .NET, lihat bagian Build di bawah.

## Pakai

1. Pilih **Sumber audio**. Bawaannya perangkat output default; daftar bisa dibuka kalau ada beberapa speaker atau headset.
2. Pilih **MP3** (128/192/256/320 kbps) atau **WAV**.
3. Atur folder simpan lewat tombol **Ubah**.
4. Klik **Mulai Rekam**, putar lagunya, lalu **Stop & Simpan**.

Setelah stop, rekaman otomatis dirapikan: senyap di awal dan akhir dibuang (bisa dimatikan lewat centang **Rapikan otomatis**). Kalau ingin memilih bagian sendiri, klik **Potong...** untuk membuka editor waveform, geser dua garis oranye, lalu simpan potongannya sebagai berkas baru.

Judul dan artis diambil dari Windows (SMTC, sumber yang sama dengan widget media di taskbar). Kalau ada lagu yang terdeteksi, nama file menjadi `Artis - Judul (2026-11-05 21.14).mp3`. Kalau tidak, `Rekaman 2026-11-05 21.14.mp3`.

Rekaman ditulis dulu ke WAV sementara di `%TEMP%\SoundCatcher`, lalu di-encode ke format pilihan setelah stop. Dengan begitu pemotongan tidak menambah kompresi lossy kedua kali, dan rekaman panjang tidak dibatasi memori. Format WAV 48 kHz stereo memakai sekitar 11,5 MB per menit; MP3 192 kbps sekitar 1,4 MB per menit.

## Format

| Format | Ukuran per menit | Catatan |
| --- | --- | --- |
| MP3 128 kbps | ± 1 MB | paling kecil |
| MP3 192 kbps | ± 1,4 MB | bawaan |
| MP3 256/320 kbps | ± 1,9 / 2,4 MB | kualitas lebih tinggi |
| WAV 48 kHz stereo | ± 11,5 MB | PCM 16-bit, tanpa kompresi |

MP3 memakai LAME (`NAudio.Lame`), WAV ditulis sebagai PCM 16-bit. Sampel float dari WASAPI dikonversi ke 16-bit sebelum ditulis.

## Build

```powershell
# butuh .NET 8 Desktop Runtime di mesin tujuan
.\desktop\publish.ps1

# mandiri, tanpa perlu install .NET (folder lebih besar, sekitar 180 MB)
.\desktop\publish.ps1 -Standalone
```

Hasil ada di `desktop\dist\SoundCatcher.exe`. Proyek memakai WPF, NAudio 2.2.1, dan NAudio.Lame 2.1.0, semuanya dipulihkan lewat NuGet.

## Uji

```powershell
dotnet run --project desktop\tests\AudioEngineTest\AudioEngineTest.csproj -c Release
```

Uji ini memutar nada 660 Hz selama 2,5 detik ke perangkat default, merekamnya lewat loopback, lalu memeriksa: rekaman berisi sinyal, deteksi senyap menemukan rentang yang benar, ekspor MP3 dan WAV berdurasi sesuai, dan pemotongan 1 detik menghasilkan berkas sekitar 1 detik. Keluar dengan kode 0 kalau semuanya lolos.

## Struktur

```
SoundCatcher/
  App.xaml, App.xaml.cs        resource tema dan handler error global
  MainWindow.xaml, .cs         UI, timer, level meter, penyimpanan
  TrimWindow.xaml, .cs         editor potong dengan waveform dan preview
  Audio/AudioRecorder.cs       WASAPI loopback ke WAV sementara
  Audio/AudioExporter.cs       deteksi senyap, encode WAV/MP3, potong rentang
  Audio/TrackWatcher.cs        metadata lagu dari Windows (SMTC)
  AppSettings.cs               pengaturan di %APPDATA%\SoundCatcher
  Controls/LevelMeter.cs       meter level kustom
  Controls/WaveformView.cs     gambar waveform dan handle pemilihan
tests/AudioEngineTest/         uji engine tanpa UI
publish.ps1                    skrip build
dist/                          hasil publish
```

## Batasan

- Hanya Windows. WASAPI loopback tidak ada di macOS atau Linux.
- Yang direkam adalah semua suara yang keluar ke perangkat itu, termasuk notifikasi dan suara aplikasi lain.
- Kalau perangkat output berganti di tengah rekaman (misalnya headset Bluetooth pindah profil), rekaman bisa berhenti. Mulai ulang setelah perangkat stabil.
- Kalau tidak ada audio yang diputar, Windows mematikan audio engine dan tidak ada paket yang masuk. Rekaman baru mulai terisi saat ada suara.
- Belum ada perekaman mikrofon. Bisa ditambah dengan `WasapiCapture` kalau diperlukan.

Gunakan hanya untuk konten yang Anda berhak merekamnya.
