using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundCatcher.Audio;

namespace SoundCatcher;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AudioRecorder _recorder = new();
    private readonly TrackWatcher _watcher;
    private readonly DispatcherTimer _uiTimer;

    private TrackSnapshot? _track;
    private TrackSnapshot? _recordingTrack;
    private bool _recording;
    private bool _busy;
    private bool _closeAfterStop;
    private double _targetLevel;
    private double _displayLevel;

    private string? _tempWav;
    private string? _lastBaseName;
    private double _lastStart;
    private double _lastEnd;

    private static string TempFolder => Path.Combine(Path.GetTempPath(), "SoundCatcher");

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        _recorder.Level += (_, level) =>
            Dispatcher.InvokeAsync(() => _targetLevel = Math.Max(_targetLevel, level));
        _recorder.Stopped += (_, _) => Dispatcher.InvokeAsync(OnRecorderStopped);
        _recorder.Error += (_, ex) => Dispatcher.InvokeAsync(() => ShowError(ex));

        _watcher = new TrackWatcher();
        _watcher.Changed += OnTrackChanged;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += (_, _) => UpdateTicker();
        _uiTimer.Start();

        LoadSettingsIntoUi();
        RefreshDevices();
        CleanupOldTempFiles();

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _uiTimer.Stop();
            _watcher.Dispose();
            _recorder.Dispose();
            DeleteTempFile();
        };
    }

    private void LoadSettingsIntoUi()
    {
        Mp3Radio.IsChecked = _settings.ToFormat() == AudioFormat.Mp3;
        WavRadio.IsChecked = _settings.ToFormat() == AudioFormat.Wav;

        Bitrate128.IsChecked = _settings.Bitrate == 128;
        Bitrate192.IsChecked = _settings.Bitrate == 192;
        Bitrate256.IsChecked = _settings.Bitrate == 256;
        Bitrate320.IsChecked = _settings.Bitrate == 320;
        if (Bitrate128.IsChecked != true && Bitrate192.IsChecked != true &&
            Bitrate256.IsChecked != true && Bitrate320.IsChecked != true)
        {
            Bitrate192.IsChecked = true;
            _settings.Bitrate = 192;
        }

        OpenFolderCheck.IsChecked = _settings.OpenFolderAfterStop;
        AutoTrimCheck.IsChecked = _settings.AutoTrim;
        FolderBox.Text = _settings.OutputFolder;
        UpdateBitrateVisibility();
    }

    private void RefreshDevices()
    {
        var devices = _recorder.GetRenderDevices();
        var menu = new ContextMenu();

        foreach (var device in devices)
        {
            var selected = string.Equals(device.Id, _settings.DeviceId, StringComparison.Ordinal) ||
                           (_settings.DeviceId is null && device.IsDefault);
            var item = new MenuItem
            {
                Header = (selected ? "● " : "     ") + device.Name,
                Tag = device.Id,
            };
            item.Click += (_, _) =>
            {
                _settings.DeviceId = device.Id;
                _settings.Save();
                RefreshDevices();
            };
            menu.Items.Add(item);
        }

        if (devices.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Tidak ada perangkat output aktif", IsEnabled = false });
        }

        var reload = new MenuItem { Header = "     Muat ulang daftar" };
        reload.Click += (_, _) =>
        {
            RefreshDevices();
            if (DeviceButton.ContextMenu is not null) DeviceButton.ContextMenu.IsOpen = true;
        };
        menu.Items.Add(reload);

        DeviceButton.ContextMenu = menu;

        var current = devices.FirstOrDefault(d => string.Equals(d.Id, _settings.DeviceId, StringComparison.Ordinal))
                      ?? devices.FirstOrDefault(d => d.IsDefault);
        DeviceText.Text = current?.Name ?? "Default";
    }

    private void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceButton.ContextMenu is not null) DeviceButton.ContextMenu.IsOpen = true;
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.Format = Mp3Radio.IsChecked == true ? "mp3" : "wav";
        _settings.Save();
        UpdateBitrateVisibility();
    }

    private void Bitrate_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var kbps))
        {
            _settings.Bitrate = kbps;
            _settings.Save();
        }
    }

    private void UpdateBitrateVisibility()
    {
        BitrateRow.Visibility = _settings.ToFormat() == AudioFormat.Mp3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pilih folder simpan",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : AppSettings.DefaultFolder(),
        };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.OutputFolder = dialog.FolderName;
            _settings.Save();
            FolderBox.Text = _settings.OutputFolder;
        }
    }

    private void OpenFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.OpenFolderAfterStop = OpenFolderCheck.IsChecked == true;
        _settings.Save();
    }

    private void AutoTrim_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.AutoTrim = AutoTrimCheck.IsChecked == true;
        _settings.Save();
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            Directory.CreateDirectory(TempFolder);
            DeleteTempFile();

            _tempWav = Path.Combine(TempFolder, $"rec-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _recordingTrack = _track;

            _recorder.Start(_tempWav, _settings.DeviceId);

            _recording = true;
            _displayLevel = 0;
            _targetLevel = 0;

            RecordButton.Content = "Stop & Simpan";
            RecordButton.Background = (Brush)FindResource("DangerGradient");
            SetOptionsEnabled(false);
            CropButton.Visibility = Visibility.Collapsed;
            SetPill("Merekam", recording: true);
            DetailText.Text = "Merekam audio output. Biarkan aplikasi ini berjalan.";
            StatusText.Text = "Merekam audio output...";
        }
        catch (Exception ex)
        {
            DeleteTempFile();
            ShowError(ex);
        }
    }

    private void StopRecording()
    {
        if (!_recording) return;
        RecordButton.IsEnabled = false;
        StatusText.Text = "Menyimpan rekaman...";
        _recorder.Stop();
    }

    private void OnRecorderStopped()
    {
        _recording = false;
        RecordButton.IsEnabled = true;
        RecordButton.Content = "Mulai Rekam";
        RecordButton.Background = (Brush)FindResource("AccentGradient");
        SetOptionsEnabled(true);
        SetPill("Siap", recording: false);
        DetailText.Text = "Tekan Mulai Rekam untuk merekam audio output.";
        TimerText.Text = "00:00";
        _displayLevel = 0;
        _targetLevel = 0;
        Meter.Level = 0;

        _ = FinishAsync();
    }

    private async Task FinishAsync()
    {
        var wav = _tempWav;
        if (wav is null || !File.Exists(wav))
        {
            StatusText.Text = "Rekaman dihentikan tanpa berkas.";
            return;
        }

        SetBusy(true);
        try
        {
            StatusText.Text = "Menyiapkan rekaman...";

            var (start, end, duration) = await Task.Run(() =>
            {
                var total = AudioExporter.GetDuration(wav);
                TimeSpan from = TimeSpan.Zero;
                TimeSpan to = total;
                if (_settings.AutoTrim && total.TotalSeconds > 0.2)
                {
                    (from, to) = AudioExporter.FindAudibleRange(wav);
                }
                return (from, to, total);
            });

            if (end <= start) end = duration;
            if (duration.TotalSeconds < 0.05)
            {
                StatusText.Text = "Rekaman terlalu pendek untuk disimpan.";
                return;
            }

            var format = _settings.ToFormat();
            var extension = format == AudioFormat.Wav ? "wav" : "mp3";
            var baseName = BuildBaseName(_recordingTrack);
            var path = UniquePath(baseName, extension);

            var progress = new Progress<double>(value =>
                StatusText.Text = $"Menyimpan... {value * 100:0}%");

            await AudioExporter.ExportAsync(wav, path, format, _settings.Bitrate, start, end, progress);

            _lastBaseName = baseName;
            _lastStart = start.TotalSeconds;
            _lastEnd = end.TotalSeconds;

            var info = new FileInfo(path);
            var trimmed = duration - (end - start);
            var note = trimmed.TotalSeconds > 1 ? $" (dipotong {FormatDuration(trimmed)})" : "";

            StatusText.Text = info.Length > 1200
                ? $"Tersimpan: {info.Name} ({FormatSize(info.Length)}){note}"
                : $"Berkas hampir kosong: {info.Name}. Pastikan ada audio yang diputar.";

            CropButton.Visibility = Visibility.Visible;

            if (info.Length > 0 && _settings.OpenFolderAfterStop) RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
            if (_closeAfterStop)
            {
                _closeAfterStop = false;
                Close();
            }
        }
    }

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_tempWav is null || !File.Exists(_tempWav) || _lastBaseName is null) return;

        var window = new TrimWindow(_tempWav, _lastBaseName, _settings, _lastStart, _lastEnd)
        {
            Owner = this,
        };

        if (window.ShowDialog() == true && window.SavedPath is not null)
        {
            var info = new FileInfo(window.SavedPath);
            StatusText.Text = $"Potongan tersimpan: {info.Name} ({FormatSize(info.Length)})";
            if (_settings.OpenFolderAfterStop) RevealInExplorer(window.SavedPath);
        }
    }

    private void UpdateTicker()
    {
        if (_recording)
        {
            TimerText.Text = FormatDuration(_recorder.Elapsed);
        }

        if (_targetLevel > _displayLevel)
        {
            _displayLevel += (_targetLevel - _displayLevel) * 0.55;
        }
        else
        {
            _displayLevel *= 0.88;
        }

        _targetLevel *= 0.62;
        if (_displayLevel < 0.004) _displayLevel = 0;
        Meter.Level = _displayLevel;
    }

    private void OnTrackChanged(TrackSnapshot? snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _track = snapshot;

            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Title))
            {
                TrackTitle.Text = "Tidak ada lagu terdeteksi";
                TrackArtist.Text = "Putar audio di aplikasi apa pun";
                ArtworkRect.Visibility = Visibility.Collapsed;
                ArtworkPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            TrackTitle.Text = snapshot.Title;
            TrackArtist.Text = !string.IsNullOrWhiteSpace(snapshot.Artist)
                ? snapshot.Artist
                : !string.IsNullOrWhiteSpace(snapshot.Album)
                    ? snapshot.Album
                    : "Artis tidak diketahui";

            if (snapshot.Artwork is { Length: > 0 })
            {
                try
                {
                    var bitmap = new BitmapImage();
                    using (var memory = new MemoryStream(snapshot.Artwork))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memory;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    ArtworkRect.Fill = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                    ArtworkRect.Visibility = Visibility.Visible;
                    ArtworkPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ArtworkRect.Visibility = Visibility.Collapsed;
                    ArtworkPlaceholder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ArtworkRect.Visibility = Visibility.Collapsed;
                ArtworkPlaceholder.Visibility = Visibility.Visible;
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RecordButton.IsEnabled = !busy;
        CropButton.IsEnabled = !busy;
        SetOptionsEnabled(!busy);
    }

    private void SetOptionsEnabled(bool enabled)
    {
        DeviceButton.IsEnabled = enabled;
        Mp3Radio.IsEnabled = enabled;
        WavRadio.IsEnabled = enabled;
        Bitrate128.IsEnabled = enabled;
        Bitrate192.IsEnabled = enabled;
        Bitrate256.IsEnabled = enabled;
        Bitrate320.IsEnabled = enabled;
        OpenFolderCheck.IsEnabled = enabled;
        AutoTrimCheck.IsEnabled = enabled;
    }

    private void SetPill(string text, bool recording)
    {
        StatusPillText.Text = text;
        if (recording)
        {
            StatusPill.Background = (Brush)FindResource("DangerBrush");
            StatusPill.BorderBrush = Brushes.Transparent;
            StatusPillText.Foreground = Brushes.White;
        }
        else
        {
            StatusPill.Background = (Brush)FindResource("PanelAltBrush");
            StatusPill.BorderBrush = (Brush)FindResource("LineBrush");
            StatusPillText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    private static string BuildBaseName(TrackSnapshot? track)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture);
        var title = track?.Title?.Trim();
        var artist = track?.Artist?.Trim();

        string name;
        if (!string.IsNullOrWhiteSpace(title))
        {
            name = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
            name = $"{name} ({stamp})";
        }
        else
        {
            name = $"Rekaman {stamp}";
        }

        return Sanitize(name);
    }

    private string UniquePath(string baseName, string extension)
    {
        var folder = _settings.OutputFolder;
        var path = Path.Combine(folder, baseName + "." + extension);
        var counter = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{baseName} ({counter++}).{extension}");
        }
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? ' ' : c);
        }

        var cleaned = Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.');
        if (cleaned.Length > 110) cleaned = cleaned[..110].Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Rekaman" : cleaned;
    }

    private static string FormatDuration(TimeSpan time)
    {
        var total = (int)Math.Max(0, time.TotalSeconds);
        var hours = total / 3600;
        var minutes = (total % 3600) / 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // membuka folder bersifat opsional
        }
    }

    private void DeleteTempFile()
    {
        if (_tempWav is null) return;
        try
        {
            if (File.Exists(_tempWav)) File.Delete(_tempWav);
        }
        catch
        {
            // berkas mungkin masih dipakai
        }
        _tempWav = null;
    }

    private static void CleanupOldTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempFolder)) return;
            foreach (var file in Directory.GetFiles(TempFolder, "rec-*.wav"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(12))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // lewati berkas yang masih dipakai
                }
            }
        }
        catch
        {
            // pembersihan bersifat opsional
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = "Gagal: " + ex.Message;
        MessageBox.Show(this, ex.Message, "Sound Catcher", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            StatusText.Text = "Tunggu sampai penyimpanan selesai.";
            return;
        }

        if (!_recording || _closeAfterStop) return;

        var result = MessageBox.Show(
            this,
            "Rekaman masih berjalan. Hentikan dan simpan sekarang?",
            "Sound Catcher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _closeAfterStop = true;
        StopRecording();
    }
}
