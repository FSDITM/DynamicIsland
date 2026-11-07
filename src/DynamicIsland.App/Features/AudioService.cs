using NAudio.CoreAudioApi;

namespace DynamicIsland.Features;

/// <summary>Громкость и микрофон через системный аудио-стек (WASAPI).</summary>
internal sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Lock _gate = new();

    private MMDevice? Render() => Default(DataFlow.Render);
    private MMDevice? Capture() => Default(DataFlow.Capture);

    private MMDevice? Default(DataFlow flow)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : null;
        }
        catch { return null; }
    }

    private MMDevice? Named(DataFlow flow, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default(flow);
        try
        {
            return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Default(flow);
        }
        catch { return Default(flow); }
    }

    public string VolumeDevice { get; set; } = "";
    public string MicDevice { get; set; } = "";

    public List<string> OutputNames() => Names(DataFlow.Render);
    public List<string> InputNames() => Names(DataFlow.Capture);

    private List<string> Names(DataFlow flow)
    {
        try { return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => d.FriendlyName).ToList(); }
        catch { return []; }
    }

    public float? Volume
    {
        get
        {
            lock (_gate)
            {
                try { using var d = Named(DataFlow.Render, VolumeDevice); return d?.AudioEndpointVolume.MasterVolumeLevelScalar; }
                catch { return null; }
            }
        }
        set
        {
            if (value is null) return;
            lock (_gate)
            {
                try
                {
                    using var d = Named(DataFlow.Render, VolumeDevice);
                    if (d is null) return;
                    d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value.Value, 0f, 1f);
                    if (value.Value > 0f && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
                }
                catch { /* устройство могли отключить */ }
            }
        }
    }

    public bool? Muted
    {
        get
        {
            lock (_gate)
            {
                try { using var d = Named(DataFlow.Render, VolumeDevice); return d?.AudioEndpointVolume.Mute; }
                catch { return null; }
            }
        }
    }

    public bool? MicMuted
    {
        get
        {
            lock (_gate)
            {
                try { using var d = Named(DataFlow.Capture, MicDevice); return d?.AudioEndpointVolume.Mute; }
                catch { return null; }
            }
        }
        set
        {
            if (value is null) return;
            lock (_gate)
            {
                try
                {
                    using var d = Named(DataFlow.Capture, MicDevice);
                    if (d is not null) d.AudioEndpointVolume.Mute = value.Value;
                }
                catch { /* нет микрофона */ }
            }
        }
    }

    public void ToggleMute()
    {
        lock (_gate)
        {
            try
            {
                using var d = Named(DataFlow.Render, VolumeDevice);
                if (d is not null) d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute;
            }
            catch { /* ничего */ }
        }
    }

    public void Dispose() => _enumerator.Dispose();
}
