using NAudio.Wave;
using SoundCatcher.Audio;

var folder = Path.Combine(Path.GetTempPath(), "songcatcher-engine-test");
if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
Directory.CreateDirectory(folder);

var failures = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

var tonePath = Path.Combine(folder, "tone.wav");
CreateWave(tonePath, 2.5, 660, 0.35, leadSilence: 0, tailSilence: 0);

var recorder = new AudioRecorder();
var devices = recorder.GetRenderDevices();
Console.WriteLine($"Perangkat output aktif: {devices.Count}");
foreach (var device in devices)
{
    Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}");
}

// 1. Rekam nada lewat loopback ke WAV sementara.
var recordedWav = Path.Combine(folder, "rekaman.wav");
var stopped = new TaskCompletionSource();
void OnStopped(object? sender, EventArgs args) => stopped.TrySetResult();
recorder.Stopped += OnStopped;
recorder.Error += (_, ex) => Console.WriteLine("  error: " + ex.Message);

using (var player = new WaveOutEvent())
{
    player.Init(new AudioFileReader(tonePath));
    player.Play();
    recorder.Start(recordedWav, null);
    await Task.Delay(2500);
    recorder.Stop();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
    player.Stop();
}
recorder.Stopped -= OnStopped;
recorder.Dispose();

Check(File.Exists(recordedWav), "WAV hasil rekaman tidak dibuat");
var (recSeconds, recPeak) = Analyze(recordedWav);
Console.WriteLine($"rekaman WAV: {new FileInfo(recordedWav).Length} bytes, {recSeconds:0.00}s, puncak {recPeak:0.000}");
Check(recSeconds > 1.8, $"durasi rekaman hanya {recSeconds:0.00}s");
Check(recPeak > 0.05, $"rekaman tidak berisi sinyal (puncak {recPeak:0.000})");

// 2. Rentang otomatis harus mencakup seluruh nada.
var (rangeStart, rangeEnd) = AudioExporter.FindAudibleRange(recordedWav);
Console.WriteLine($"rentang otomatis: {rangeStart.TotalSeconds:0.00}s - {rangeEnd.TotalSeconds:0.00}s");
Check(rangeEnd > rangeStart, "rentang otomatis tidak valid");

// 3. Ekspor seluruh rentang ke MP3.
var fullMp3 = Path.Combine(folder, "full.mp3");
await AudioExporter.ExportAsync(recordedWav, fullMp3, AudioFormat.Mp3, 192, rangeStart, rangeEnd);
var (fullSeconds, fullPeak) = Analyze(fullMp3);
var head = ReadHead(fullMp3);
Console.WriteLine($"MP3 penuh: {new FileInfo(fullMp3).Length} bytes, {fullSeconds:0.00}s, puncak {fullPeak:0.000}, header {Convert.ToHexString(head)}");
Check(head[0] == 0xFF, "MP3 tidak diawali frame sync");
Check(fullSeconds > 1.8, $"durasi MP3 hanya {fullSeconds:0.00}s");
Check(fullPeak > 0.05, $"MP3 tidak berisi sinyal (puncak {fullPeak:0.000})");

// 4. Potong 1 detik di tengah.
var cutMp3 = Path.Combine(folder, "potong.mp3");
await AudioExporter.ExportAsync(recordedWav, cutMp3, AudioFormat.Mp3, 192,
    TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
var (cutSeconds, cutPeak) = Analyze(cutMp3);
Console.WriteLine($"MP3 potongan: {new FileInfo(cutMp3).Length} bytes, {cutSeconds:0.00}s, puncak {cutPeak:0.000}");
Check(cutSeconds is > 0.85 and < 1.2, $"durasi potongan {cutSeconds:0.00}s, seharusnya sekitar 1 detik");
Check(cutPeak > 0.05, "potongan tidak berisi sinyal");

// 5. Ekspor potongan ke WAV.
var cutWav = Path.Combine(folder, "potong.wav");
await AudioExporter.ExportAsync(recordedWav, cutWav, AudioFormat.Wav, 0,
    TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));
var (cutWavSeconds, _) = Analyze(cutWav);
var wavHead = ReadHead(cutWav);
Console.WriteLine($"WAV potongan: {new FileInfo(cutWav).Length} bytes, {cutWavSeconds:0.00}s, header {Convert.ToHexString(wavHead)}");
Check(wavHead[0] == (byte)'R' && wavHead[8] == (byte)'W', "header WAV potongan tidak valid");
Check(cutWavSeconds is > 0.85 and < 1.2, $"durasi WAV potongan {cutWavSeconds:0.00}s");

// 6. Deteksi senyap pada berkas dengan jeda di kedua ujung.
var paddedPath = Path.Combine(folder, "padded.wav");
CreateWave(paddedPath, 1.0, 440, 0.4, leadSilence: 0.6, tailSilence: 0.6);
var (paddedStart, paddedEnd) = AudioExporter.FindAudibleRange(paddedPath);
Console.WriteLine($"deteksi senyap: {paddedStart.TotalSeconds:0.00}s - {paddedEnd.TotalSeconds:0.00}s dari 2.20s");
Check(paddedStart.TotalSeconds is > 0.3 and <= 0.6, $"awal rentang {paddedStart.TotalSeconds:0.00}s");
Check(paddedEnd.TotalSeconds is >= 1.6 and < 2.0, $"akhir rentang {paddedEnd.TotalSeconds:0.00}s");

if (failures.Count > 0)
{
    Console.WriteLine("GAGAL:");
    foreach (var failure in failures) Console.WriteLine("  - " + failure);
    return 1;
}

Console.WriteLine("OK: rekam, deteksi senyap, ekspor, dan potong berjalan benar.");
return 0;

static void CreateWave(string path, double toneSeconds, double frequency, double amplitude, double leadSilence, double tailSilence)
{
    var format = new WaveFormat(44100, 16, 2);
    var totalSeconds = leadSilence + toneSeconds + tailSilence;
    var samples = (int)(format.SampleRate * totalSeconds);
    var toneStart = (int)(format.SampleRate * leadSilence);

    using var writer = new WaveFileWriter(path, format);
    var buffer = new byte[samples * 4];
    for (var i = 0; i < samples; i++)
    {
        short value = 0;
        if (i >= toneStart && i < toneStart + (int)(format.SampleRate * toneSeconds))
        {
            value = (short)(Math.Sin(2 * Math.PI * frequency * (i - toneStart) / format.SampleRate) * amplitude * short.MaxValue);
        }
        var low = (byte)(value & 0xFF);
        var high = (byte)((value >> 8) & 0xFF);
        buffer[i * 4] = low;
        buffer[i * 4 + 1] = high;
        buffer[i * 4 + 2] = low;
        buffer[i * 4 + 3] = high;
    }
    writer.Write(buffer, 0, buffer.Length);
}

static (double Seconds, float Peak) Analyze(string path)
{
    using var reader = new AudioFileReader(path);
    var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
    var peak = 0f;
    long total = 0;
    int read;
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        for (var i = 0; i < read; i++)
        {
            var magnitude = Math.Abs(buffer[i]);
            if (magnitude > peak) peak = magnitude;
        }
        total += read;
    }
    var seconds = total / (double)(reader.WaveFormat.SampleRate * reader.WaveFormat.Channels);
    return (seconds, peak);
}

static byte[] ReadHead(string path)
{
    var head = new byte[12];
    using var stream = File.OpenRead(path);
    _ = stream.Read(head, 0, head.Length);
    return head;
}
