using System.IO;
using NAudio.Lame;
using NAudio.Wave;

namespace SoundCatcher.Audio;

public static class AudioExporter
{
    /// <summary>Ambang senyap, sekitar -48 dBFS.</summary>
    public const double SilenceThreshold = 0.004;

    public static TimeSpan GetDuration(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        return reader.TotalTime;
    }

    /// <summary>
    /// Mencari bagian pertama dan terakhir yang berisi suara, dengan sedikit jeda
    /// tambahan di kedua ujung agar tidak terasa terpotong.
    /// </summary>
    public static (TimeSpan Start, TimeSpan End) FindAudibleRange(
        string wavPath,
        double threshold = SilenceThreshold,
        double paddingSeconds = 0.25)
    {
        using var reader = new WaveFileReader(wavPath);
        var format = reader.WaveFormat;
        var blockAlign = format.BlockAlign;
        var duration = reader.TotalTime;
        var buffer = new byte[blockAlign * 8192];

        long firstActive = -1;
        long lastActive = -1;
        long frame = 0;

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var frames = read / blockAlign;
            for (var i = 0; i < frames; i++)
            {
                var offset = i * blockAlign;
                var active = false;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sample = BitConverter.ToInt16(buffer, offset + channel * 2);
                    if (Math.Abs(sample) > threshold * 32768)
                    {
                        active = true;
                        break;
                    }
                }

                if (active)
                {
                    if (firstActive < 0) firstActive = frame + i;
                    lastActive = frame + i;
                }
            }
            frame += frames;
        }

        if (firstActive < 0 || lastActive < firstActive)
        {
            return (TimeSpan.Zero, duration);
        }

        var start = Math.Max(0, firstActive / (double)format.SampleRate - paddingSeconds);
        var end = Math.Min(duration.TotalSeconds, lastActive / (double)format.SampleRate + paddingSeconds);

        if (end - start < 0.2)
        {
            return (TimeSpan.Zero, duration);
        }

        return (TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
    }

    public static Task ExportAsync(
        string sourceWav,
        string destination,
        AudioFormat format,
        int bitrateKbps,
        TimeSpan start,
        TimeSpan end,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Export(sourceWav, destination, format, bitrateKbps, start, end, progress, cancellationToken),
            cancellationToken);
    }

    private static void Export(
        string sourceWav,
        string destination,
        AudioFormat format,
        int bitrateKbps,
        TimeSpan start,
        TimeSpan end,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(sourceWav);
        var duration = reader.TotalTime;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (end <= start || end > duration) end = duration;

        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        if (format == AudioFormat.Wav && start <= TimeSpan.Zero && end >= duration)
        {
            File.Copy(sourceWav, destination, overwrite: true);
            progress?.Report(1);
            return;
        }

        var bytesPerFrame = reader.WaveFormat.BlockAlign;
        var totalBytes = (long)((end - start).TotalSeconds * reader.WaveFormat.AverageBytesPerSecond);
        totalBytes -= totalBytes % bytesPerFrame;

        reader.CurrentTime = start;

        Stream writer = format == AudioFormat.Mp3
            ? new LameMP3FileWriter(destination, reader.WaveFormat, bitrateKbps)
            : new WaveFileWriter(destination, reader.WaveFormat);

        try
        {
            var buffer = new byte[64 * 1024];
            long written = 0;
            var lastReport = -1.0;

            while (written < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var wanted = (int)Math.Min(buffer.Length, totalBytes - written);
                var read = reader.Read(buffer, 0, wanted);
                if (read <= 0) break;

                writer.Write(buffer, 0, read);
                written += read;

                if (totalBytes > 0)
                {
                    var ratio = written / (double)totalBytes;
                    if (ratio - lastReport >= 0.02)
                    {
                        lastReport = ratio;
                        progress?.Report(Math.Min(1, ratio));
                    }
                }
            }

            progress?.Report(1);
        }
        finally
        {
            writer.Flush();
            writer.Dispose();
        }
    }
}
