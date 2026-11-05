using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundCatcher.Audio;

public enum AudioFormat
{
    Mp3,
    Wav,
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Merekam audio yang dikirim ke perangkat output (WASAPI loopback) ke berkas WAV
/// sementara. Encoding ke MP3 dan pemotongan dilakukan setelah rekaman selesai.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private short[] _pcm = new short[8192];
    private byte[] _bytes = new byte[16384];

    public bool IsRecording => _capture != null;
    public TimeSpan Elapsed => _clock.Elapsed;
    public string? CurrentFile { get; private set; }
    public WaveFormat? SourceFormat { get; private set; }

    /// <summary>Puncak sinyal 0..1 dari blok terakhir.</summary>
    public event EventHandler<float>? Level;

    public event EventHandler<Exception>? Error;
    public event EventHandler? Stopped;

    public IReadOnlyList<AudioDevice> GetRenderDevices()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            var defaultId = string.Empty;
            try
            {
                defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch
            {
                // tidak ada perangkat default, lanjut saja
            }

            var list = new List<AudioDevice>();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                device.Dispose();
            }
            return list;
        }
        catch
        {
            return Array.Empty<AudioDevice>();
        }
    }

    public void Start(string wavPath, string? deviceId)
    {
        if (IsRecording) throw new InvalidOperationException("Rekaman masih berjalan.");

        _enumerator ??= new MMDeviceEnumerator();
        _device = ResolveDevice(deviceId);
        _capture = new WasapiLoopbackCapture(_device);
        SourceFormat = _capture.WaveFormat;

        var source = _capture.WaveFormat;
        var pcmFormat = new WaveFormat(source.SampleRate, 16, source.Channels);
        _writer = new WaveFileWriter(wavPath, pcmFormat);

        CurrentFile = wavPath;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _clock.Restart();
        _capture.StartRecording();
    }

    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
    }

    private MMDevice ResolveDevice(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            foreach (var device in _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (device.ID == deviceId) return device;
                device.Dispose();
            }
        }
        return _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var writer = _writer;
        if (capture is null || writer is null || e.BytesRecorded == 0) return;

        var format = capture.WaveFormat;
        var sampleCount = e.BytesRecorded / (format.BitsPerSample / 8);
        if (sampleCount <= 0) return;

        float peak;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            if (_pcm.Length < sampleCount) _pcm = new short[sampleCount];
            peak = 0f;
            for (var i = 0; i < sampleCount; i++)
            {
                var value = BitConverter.ToSingle(e.Buffer, i * 4);
                if (value > 1f) value = 1f;
                else if (value < -1f) value = -1f;

                var magnitude = Math.Abs(value);
                if (magnitude > peak) peak = magnitude;
                _pcm[i] = (short)(value < 0 ? value * 0x8000 : value * 0x7fff);
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            if (_pcm.Length < sampleCount) _pcm = new short[sampleCount];
            Buffer.BlockCopy(e.Buffer, 0, _pcm, 0, sampleCount * 2);
            peak = 0f;
            for (var i = 0; i < sampleCount; i++)
            {
                var magnitude = Math.Abs(_pcm[i] / 32768f);
                if (magnitude > peak) peak = magnitude;
            }
        }
        else
        {
            return;
        }

        var bytes = sampleCount * 2;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        Buffer.BlockCopy(_pcm, 0, _bytes, 0, bytes);

        try
        {
            writer.Write(_bytes, 0, bytes);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
            Stop();
            return;
        }

        Level?.Invoke(this, peak);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _clock.Stop();

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // berkas mungkin tidak lengkap, tetap lanjut
        }
        finally
        {
            _writer = null;
        }

        _device?.Dispose();
        _device = null;

        if (e.Exception is not null)
        {
            Error?.Invoke(this, e.Exception);
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try
        {
            if (IsRecording) Stop();
        }
        catch
        {
            // diabaikan saat keluar
        }

        _writer?.Dispose();
        _capture?.Dispose();
        _device?.Dispose();
        _enumerator?.Dispose();
        _writer = null;
        _capture = null;
        _device = null;
        _enumerator = null;
    }
}
