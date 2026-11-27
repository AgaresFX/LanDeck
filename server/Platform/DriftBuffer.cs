namespace PadNook.Platform;

/// <summary>
/// Bridges two audio clocks (microphone and output device) that run at slightly different real speeds.
/// Instead of dropping audio when the queue grows (an audible click), the read speed is adjusted by a tiny
/// amount (at most ±0.3%, inaudible) with cubic interpolation, so the queue stays around the target latency.
/// Single writer thread (capture) and single reader thread (output).
/// </summary>
internal sealed class DriftBuffer
{
    private const double MaxCorrection = 0.003; // ±0.3 % speed
    private const double Gain = 0.002;          // speed change per 100 % queue error
    private const double Smoothing = 0.02;      // EMA weight of each read (reads come every ~10 ms -> ~0.5 s)

    private readonly object _lock = new();
    private readonly int _ch;
    private readonly float[] _ring; // interleaved frames
    private readonly int _cap;      // frames
    private readonly int _target;   // frames
    private readonly int _emergency; // frames: above this something stalled; jump back with a fade
    private int _head;              // frame "s0" of the interpolation window
    private int _count;             // frames stored from _head
    private double _frac;           // position between s1 and s2
    private double _fill;           // smoothed queue length (frames)
    private bool _primed;
    private float _fadeIn = 1f;     // ramps in after (re)starting, so a restart is not a click either
    private readonly float _fadeStep; // ~10 ms

    public double Ratio { get; private set; } = 1.0;
    public int Queued { get { lock (_lock) return _count; } }
    public int Jumps { get; private set; }
    public int Underruns { get; private set; }

    public DriftBuffer(int sampleRate, int channels, int targetMs = 40)
    {
        _ch = channels;
        _cap = sampleRate; // 1 s
        _ring = new float[_cap * _ch];
        _target = sampleRate * targetMs / 1000;
        _emergency = _target + sampleRate * 300 / 1000;
        _fill = _target;
        _fadeStep = 100f / sampleRate;
    }

    public void Write(float[] samples, int n)
    {
        var frames = n / _ch;
        lock (_lock)
        {
            for (var f = 0; f < frames; f++)
            {
                if (_count == _cap) { _head = (_head + 1) % _cap; _count--; } // full (reader gone): keep the newest
                var dst = ((_head + _count) % _cap) * _ch;
                Array.Copy(samples, f * _ch, _ring, dst, _ch);
                _count++;
            }
        }
    }

    private float At(int frame, int c) => _ring[((_head + frame) % _cap) * _ch + c];

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / _ch;
        lock (_lock)
        {
            if (!_primed && _count >= _target + 4)
            {
                _primed = true;
                _fadeIn = 0f;
                _fill = _count;
            }

            // Something stalled (e.g. the PC froze): skip ahead once, fading in again.
            if (_primed && _count > _emergency)
            {
                var skip = _count - _target;
                _head = (_head + skip) % _cap;
                _count -= skip;
                _fill = _count;
                _fadeIn = 0f;
                Jumps++;
            }

            var f = 0;
            if (_primed)
            {
                for (; f < frames; f++)
                {
                    if (_count < 4)
                    {
                        // Mic stopped delivering: silence until it fills up again.
                        _primed = false;
                        Underruns++;
                        break;
                    }
                    var t = (float)_frac;
                    for (var c = 0; c < _ch; c++)
                    {
                        float s0 = At(0, c), s1 = At(1, c), s2 = At(2, c), s3 = At(3, c);
                        var c1 = 0.5f * (s2 - s0);
                        var c2 = s0 - 2.5f * s1 + 2f * s2 - 0.5f * s3;
                        var c3 = 0.5f * (s3 - s0) + 1.5f * (s1 - s2);
                        buffer[offset + f * _ch + c] = (((c3 * t + c2) * t + c1) * t + s1) * _fadeIn;
                    }
                    if (_fadeIn < 1f) _fadeIn = Math.Min(1f, _fadeIn + _fadeStep);
                    _frac += Ratio;
                    while (_frac >= 1.0)
                    {
                        _frac -= 1.0;
                        _head = (_head + 1) % _cap;
                        _count--;
                    }
                }

                // Speed controller: keep the smoothed queue at the target.
                _fill += Smoothing * (_count - _fill);
                var error = (_fill - _target) / _target;
                Ratio = 1.0 + Math.Clamp(Gain * error, -MaxCorrection, MaxCorrection);
            }
            Array.Clear(buffer, offset + f * _ch, count - f * _ch);
        }
        return count; // always "full": the mic never ends
    }
}
