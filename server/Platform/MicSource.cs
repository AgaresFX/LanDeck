using PadNook.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PadNook.Platform;

/// <summary>Live microphone capture as a sample source, with drift correction (see <see cref="DriftBuffer"/>).</summary>
internal sealed class MicSource : ISampleProvider, IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly WaveFormat _src;
    private readonly DriftBuffer _buffer;
    private float[] _conv = [];

    public string DeviceId { get; }
    public string Name { get; }
    public bool Alive { get; private set; } = true;
    public WaveFormat WaveFormat { get; }

    public MicSource(MMDevice device)
    {
        DeviceId = device.ID;
        Name = device.FriendlyName;
        _capture = new WasapiCapture(device, true, 20);
        _src = _capture.WaveFormat;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_src.SampleRate, _src.Channels);
        _buffer = new DriftBuffer(_src.SampleRate, _src.Channels);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            Alive = false;
            if (e.Exception is not null) Log.Error($"Soundpad mic stopped ({Name})", e.Exception);
        };
        _capture.StartRecording();
    }

    private bool IsFloat => _src.Encoding == WaveFormatEncoding.IeeeFloat
        || (_src is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var bytes = _src.BitsPerSample / 8;
        var n = e.BytesRecorded / bytes;
        if (_conv.Length < n) _conv = new float[n];
        var isFloat = IsFloat;
        var b = e.Buffer;
        for (var i = 0; i < n; i++)
        {
            var o = i * bytes;
            _conv[i] = isFloat && bytes == 4 ? BitConverter.ToSingle(b, o)
                : bytes == 2 ? BitConverter.ToInt16(b, o) / 32768f
                : bytes == 3 ? (b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16)) / 8388608f
                : bytes == 4 ? BitConverter.ToInt32(b, o) / 2147483648f
                : 0f;
        }
        _buffer.Write(_conv, n);
    }

    public int Read(float[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);

    public void Dispose()
    {
        Alive = false;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}
