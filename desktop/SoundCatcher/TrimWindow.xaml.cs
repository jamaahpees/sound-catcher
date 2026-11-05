using System.IO;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;
using SoundCatcher.Audio;

namespace SoundCatcher;

public partial class TrimWindow : Window
{
    private readonly string _wavPath;
    private readonly string _baseName;
    private readonly AppSettings _settings;
    private readonly double _initialStart;
    private readonly double _initialEnd;

    private WaveOutEvent? _player;
    private AudioFileReader? _reader;
    private DispatcherTimer? _previewTimer;
    private bool _saving;

    public string? SavedPath { get; private set; }

    public TrimWindow(string wavPath, string baseName, AppSettings settings, double startSeconds, double endSeconds)
    {
        InitializeComponent();

        _wavPath = wavPath;
        _baseName = baseName;
        _settings = settings;
        _initialStart = startSeconds;
        _initialEnd = endSeconds;

        FileNameText.Text = Path.GetFileName(wavPath);
        Wave.SelectionChanged += (_, _) => UpdateTimes();
        Loaded += async (_, _) => await LoadWaveformAsync();
        Closing += (_, _) => StopPreview();
    }

    private async Task LoadWaveformAsync()
    {
        StatusText.Text = "Menyiapkan waveform...";
        SaveButton.IsEnabled = false;

        try
        {
            var (peaks, duration) = await Task.Run(() => ComputePeaks(_wavPath, 1400));

            Wave.Duration = duration;
            Wave.Peaks = peaks;

            var start = Math.Clamp(_initialStart, 0, Math.Max(0, duration - 0.1));
            var end = Math.Clamp(_initialEnd, start + 0.05, duration);
            Wave.StartSeconds = start;
            Wave.EndSeconds = end;

            UpdateTimes();
            StatusText.Text = "Geser garis oranye untuk memilih bagian yang disimpan.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Gagal membaca rekaman: " + ex.Message, "Sound Catcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private static (double[] Peaks, double Duration) ComputePeaks(string path, int buckets)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var duration = reader.TotalTime.TotalSeconds;
        var totalFrames = Math.Max(1, (long)(duration * reader.WaveFormat.SampleRate));

        var peaks = new double[buckets];
        var buffer = new float[reader.WaveFormat.SampleRate * channels];
        long frameIndex = 0;
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var frames = read / channels;
            for (var i = 0; i < frames; i++)
            {
                var bucket = (int)((frameIndex + i) * buckets / totalFrames);
                if (bucket >= buckets) bucket = buckets - 1;

                float peak = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var magnitude = Math.Abs(buffer[i * channels + channel]);
                    if (magnitude > peak) peak = magnitude;
                }

                if (peak > peaks[bucket]) peaks[bucket] = peak;
            }
            frameIndex += frames;
        }

        return (peaks, duration);
    }

    private void UpdateTimes()
    {
        var start = TimeSpan.FromSeconds(Wave.StartSeconds);
        var end = TimeSpan.FromSeconds(Wave.EndSeconds);
        StartText.Text = "Awal  " + Format(start);
        EndText.Text = "Akhir  " + Format(end);
        LengthText.Text = "Durasi  " + Format(end - start);
    }

    private static string Format(TimeSpan time)
    {
        var total = Math.Max(0, time.TotalSeconds);
        var hours = (int)(total / 3600);
        var minutes = (int)((total % 3600) / 60);
        var seconds = total % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00.0}"
            : $"{minutes:00}:{seconds:00.0}";
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null)
        {
            StopPreview();
            return;
        }

        try
        {
            _reader = new AudioFileReader(_wavPath) { CurrentTime = TimeSpan.FromSeconds(Wave.StartSeconds) };
            _player = new WaveOutEvent();
            _player.Init(_reader);
            _player.Play();

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _previewTimer.Tick += (_, _) =>
            {
                if (_reader is null) return;
                Wave.PlayheadSeconds = _reader.CurrentTime.TotalSeconds;
                if (_reader.CurrentTime.TotalSeconds >= Wave.EndSeconds) StopPreview();
            };
            _previewTimer.Start();
            PreviewButton.Content = "Stop";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Gagal memutar: " + ex.Message;
            StopPreview();
        }
    }

    private void StopPreview()
    {
        _previewTimer?.Stop();
        _previewTimer = null;

        try
        {
            _player?.Stop();
            _player?.Dispose();
        }
        catch
        {
            // diabaikan
        }

        _player = null;
        _reader?.Dispose();
        _reader = null;
        Wave.PlayheadSeconds = -1;
        PreviewButton.Content = "Putar pilihan";
    }

    private void AutoTrim_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (start, end) = AudioExporter.FindAudibleRange(_wavPath);
            Wave.StartSeconds = start.TotalSeconds;
            Wave.EndSeconds = end.TotalSeconds;
            UpdateTimes();
            StatusText.Text = "Senyap di awal dan akhir sudah dibuang.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Gagal memindai: " + ex.Message;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Wave.StartSeconds = 0;
        Wave.EndSeconds = Wave.Duration;
        UpdateTimes();
        StatusText.Text = "Seluruh rekaman dipilih.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;

        StopPreview();
        _saving = true;
        SaveButton.IsEnabled = false;
        ExportBar.Visibility = Visibility.Visible;
        ExportBar.Value = 0;

        try
        {
            var format = _settings.ToFormat();
            var extension = format == AudioFormat.Wav ? "wav" : "mp3";
            var path = BuildPath(extension);

            var progress = new Progress<double>(value =>
            {
                ExportBar.Value = value * 100;
                StatusText.Text = $"Menyimpan... {value * 100:0}%";
            });

            await AudioExporter.ExportAsync(
                _wavPath,
                path,
                format,
                _settings.Bitrate,
                TimeSpan.FromSeconds(Wave.StartSeconds),
                TimeSpan.FromSeconds(Wave.EndSeconds),
                progress);

            SavedPath = path;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Gagal menyimpan potongan: " + ex.Message, "Sound Catcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Gagal menyimpan.";
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
            ExportBar.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildPath(string extension)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        var baseName = _baseName + " (potong)";
        var path = Path.Combine(_settings.OutputFolder, baseName + "." + extension);
        var counter = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(_settings.OutputFolder, $"{baseName} ({counter++}).{extension}");
        }
        return path;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        DialogResult = false;
    }
}
