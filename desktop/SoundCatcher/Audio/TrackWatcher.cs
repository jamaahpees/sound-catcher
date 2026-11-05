using System.IO;
using Windows.Media.Control;

namespace SoundCatcher.Audio;

public sealed record TrackSnapshot(string Title, string Artist, string Album, byte[]? Artwork);

/// <summary>
/// Membaca lagu yang sedang diputar dari Windows (SMTC), sumber yang sama
/// dengan yang dipakai widget media di taskbar.
/// </summary>
public sealed class TrackWatcher : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string _lastKey = "\u0000";
    private int _busy;

    public event Action<TrackSnapshot?>? Changed;

    public TrackWatcher()
    {
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, 700, 1500);
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager.GetCurrentSession();
            if (session is null)
            {
                Publish(null, string.Empty);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var key = $"{props.Artist}\u001f{props.Title}\u001f{props.AlbumTitle}";
            if (key == _lastKey) return;

            byte[]? artwork = null;
            if (props.Thumbnail is not null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var memory = new MemoryStream();
                    await stream.AsStreamForRead().CopyToAsync(memory);
                    artwork = memory.ToArray();
                }
                catch
                {
                    artwork = null;
                }
            }

            Publish(
                new TrackSnapshot(props.Title ?? string.Empty, props.Artist ?? string.Empty, props.AlbumTitle ?? string.Empty, artwork),
                key);
        }
        catch
        {
            // SMTC bisa gagal kalau tidak ada sesi aktif; abaikan.
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Publish(TrackSnapshot? snapshot, string key)
    {
        lock (_gate)
        {
            if (key == _lastKey) return;
            _lastKey = key;
        }
        Changed?.Invoke(snapshot);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
