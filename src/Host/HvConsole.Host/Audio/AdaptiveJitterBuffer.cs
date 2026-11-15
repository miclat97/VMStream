// ============================================================================
// AdaptiveJitterBuffer.cs - Click-Free Jitter Buffer & Cubic Drift Regulator
// Supports dynamic 44.1 kHz / 48 kHz / host native WASAPI mix rates:
//   - Bit-exact 1:1 fast path inside [18..40 ms] deadband when rates match
//   - 4-point Catmull-Rom cubic Hermite continuous fractional resampling
//     for both cross-rate conversion (44.1k <-> 48k <-> 96k) and subtle
//     oscillator drift (+/- 0.04%..0.4%, zero step clicks)
//   - Exponential decay on underrun + 3 ms soft fade-in on resume (no pops)
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HvConsole.Host.Protocol;

namespace HvConsole.Host.Audio;

public sealed class AdaptiveJitterBuffer
{
    private const int BytesPerStereo16Frame = HvProtocolConstants.AudioBlockAlignStereo16;
    private const int CapacityFrames = 16384; // Power of 2 (~341ms @ 48kHz, ~371ms @ 44.1kHz)
    private const int CapacityMask = CapacityFrames - 1;

    private const double PrebufferMs = 22.0;
    private const double DeadbandLowMs = 18.0;
    private const double DeadbandHighMs = 40.0;
    private const double OverflowLimitMs = 140.0;
    private const double OverflowTrimTargetMs = 32.0;

    private readonly float[] _leftSamples = new float[CapacityFrames];
    private readonly float[] _rightSamples = new float[CapacityFrames];
    private readonly object _lock = new();

    private int _inputSampleRate = HvProtocolConstants.AudioSampleRate;
    private int _outputSampleRate = HvProtocolConstants.AudioSampleRate;
    private int _readIndex;
    private int _writeIndex;
    private int _bufferedFrames;

    private double _srcPhase;
    private float _histL1;
    private float _histR1;
    private float _lastOutL;
    private float _lastOutR;
    private float _fadeGain;
    private double _driftSlipAccumulator;
    private bool _prebuffering = true;
    private bool _guestRenderActive;

    public long TotalDroppedFramesForDrift { get; private set; }
    public long TotalDuplicatedFramesForDrift { get; private set; }
    public long TotalUnderruns { get; private set; }

    public int InputSampleRate
    {
        get
        {
            lock (_lock) return _inputSampleRate;
        }
    }

    public int OutputSampleRate
    {
        get
        {
            lock (_lock) return _outputSampleRate;
        }
    }

    public double BufferedMilliseconds
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(0.0, (_bufferedFrames - _srcPhase) * 1000.0 / Math.Max(1, _inputSampleRate));
            }
        }
    }

    public void SetOutputSampleRate(int outputSampleRate)
    {
        if (outputSampleRate <= 0) return;
        lock (_lock)
        {
            _outputSampleRate = outputSampleRate;
        }
    }

    public void NotifyGuestRenderState(bool isActive)
    {
        lock (_lock)
        {
            _guestRenderActive = isActive;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _readIndex = 0;
            _writeIndex = 0;
            _bufferedFrames = 0;
            _srcPhase = 0.0;
            _histL1 = 0.0f;
            _histR1 = 0.0f;
            _lastOutL = 0.0f;
            _lastOutR = 0.0f;
            _fadeGain = 0.0f;
            _driftSlipAccumulator = 0.0;
            _prebuffering = true;
        }
    }

    /// <summary>
    /// Pushes interleaved 16-bit stereo PCM bytes from the AF_HYPERV audio socket.
    /// </summary>
    public void PushInterleavedStereo16(ReadOnlySpan<byte> pcmBytes, int inputSampleRate = HvProtocolConstants.AudioSampleRate)
    {
        int frameCount = pcmBytes.Length / BytesPerStereo16Frame;
        if (frameCount <= 0) return;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        const float Inv32768 = 1.0f / 32768.0f;

        lock (_lock)
        {
            if (inputSampleRate > 0 && inputSampleRate != _inputSampleRate)
            {
                _inputSampleRate = inputSampleRate;
                _readIndex = 0;
                _writeIndex = 0;
                _bufferedFrames = 0;
                _srcPhase = 0.0;
                _histL1 = 0.0f;
                _histR1 = 0.0f;
                _fadeGain = 0.0f;
                _prebuffering = true;
            }

            int maxAllowedFrames = Math.Min(CapacityFrames, (int)Math.Ceiling(_inputSampleRate * (OverflowLimitMs / 1000.0)));
            int trimTargetFrames = Math.Max(1, (int)Math.Ceiling(_inputSampleRate * (OverflowTrimTargetMs / 1000.0)));

            if (frameCount > CapacityFrames)
            {
                int skip = frameCount - trimTargetFrames;
                samples = samples.Slice(skip * 2);
                frameCount = trimTargetFrames;
                TotalDroppedFramesForDrift += skip;
            }

            if (_bufferedFrames + frameCount > maxAllowedFrames)
            {
                int keepExisting = Math.Max(0, trimTargetFrames - frameCount);
                int drop = Math.Max(0, _bufferedFrames - keepExisting);
                if (drop > 0 && drop <= _bufferedFrames)
                {
                    int lastDroppedIdx = (_readIndex + drop - 1) & CapacityMask;
                    _histL1 = _leftSamples[lastDroppedIdx];
                    _histR1 = _rightSamples[lastDroppedIdx];
                    _readIndex = (_readIndex + drop) & CapacityMask;
                    _bufferedFrames -= drop;
                    _srcPhase = 0.0;
                    _fadeGain = Math.Min(_fadeGain, 0.25f);
                    TotalDroppedFramesForDrift += drop;
                }
            }

            for (int i = 0; i < frameCount; i++)
            {
                _leftSamples[_writeIndex] = samples[i * 2] * Inv32768;
                _rightSamples[_writeIndex] = samples[(i * 2) + 1] * Inv32768;
                _writeIndex = (_writeIndex + 1) & CapacityMask;
                if (_bufferedFrames < CapacityFrames)
                {
                    _bufferedFrames++;
                }
            }

            int prebufferFrames = Math.Max(1, (int)Math.Round(_inputSampleRate * (PrebufferMs / 1000.0)));
            if (_prebuffering && _bufferedFrames >= prebufferFrames)
            {
                _prebuffering = false;
            }
        }
    }

    /// <summary>
    /// Pulls interleaved 32-bit IEEE Float stereo PCM frames directly for WASAPI playback.
    /// </summary>
    public void PullInterleavedFloat32(Span<float> destination, float volume, bool muted)
    {
        int requestedFrames = destination.Length / 2;
        if (requestedFrames <= 0) return;

        PullCore(destination, Span<short>.Empty, requestedFrames, volume, muted);
    }

    /// <summary>
    /// Pulls interleaved 16-bit stereo PCM bytes for WASAPI playback.
    /// </summary>
    public void PullInterleavedStereo16(Span<byte> destination, float volume, bool muted)
    {
        int requestedFrames = destination.Length / BytesPerStereo16Frame;
        if (requestedFrames <= 0) return;

        Span<short> outSamples = MemoryMarshal.Cast<byte, short>(destination);
        PullCore(Span<float>.Empty, outSamples, requestedFrames, volume, muted);
    }

    private void PullCore(
        Span<float> floatOut,
        Span<short> shortOut,
        int requestedFrames,
        float volume,
        bool muted)
    {
        bool writeFloat = !floatOut.IsEmpty;
        float effectiveGain = muted ? 0.0f : Math.Clamp(volume, 0.0f, 1.0f);

        lock (_lock)
        {
            if (_prebuffering || _bufferedFrames == 0)
            {
                if (_bufferedFrames == 0 && !_prebuffering)
                {
                    _prebuffering = true;
                    _fadeGain = 0.0f;
                    if (_guestRenderActive)
                    {
                        TotalUnderruns++;
                    }
                }

                FillDecaySilence(floatOut, shortOut, writeFloat, 0, requestedFrames, effectiveGain);
                return;
            }

            double bufferedMs = Math.Max(0.0, (_bufferedFrames - _srcPhase) * 1000.0 / Math.Max(1, _inputSampleRate));
            double baseStep = (double)_inputSampleRate / Math.Max(1, _outputSampleRate);

            double driftFactor = 0.0;
            if (bufferedMs > DeadbandHighMs)
            {
                driftFactor = Math.Min(0.004, 0.0004 + (bufferedMs - DeadbandHighMs) * 0.00005);
            }
            else if (bufferedMs < DeadbandLowMs)
            {
                driftFactor = -Math.Min(0.004, 0.0004 + (DeadbandLowMs - bufferedMs) * 0.00005);
            }

            double step = baseStep * (1.0 + driftFactor);
            float fadeStep = 1.0f / Math.Max(1.0f, _outputSampleRate * 0.003f); // 3 ms linear fade-in
            bool exactOneToOne = Math.Abs(step - 1.0) < 1e-9 && _srcPhase < 1e-7;
            int activeFramesProduced = 0;

            for (int outIdx = 0; outIdx < requestedFrames; outIdx++)
            {
                if (_bufferedFrames == 0)
                {
                    if (!_prebuffering)
                    {
                        _prebuffering = true;
                        _fadeGain = 0.0f;
                        if (_guestRenderActive)
                        {
                            TotalUnderruns++;
                        }
                    }

                    FillDecaySilence(floatOut, shortOut, writeFloat, outIdx, requestedFrames, effectiveGain);
                    break;
                }

                float sampleL;
                float sampleR;

                if (exactOneToOne)
                {
                    sampleL = _leftSamples[_readIndex];
                    sampleR = _rightSamples[_readIndex];
                    _histL1 = sampleL;
                    _histR1 = sampleR;
                    _readIndex = (_readIndex + 1) & CapacityMask;
                    _bufferedFrames--;
                }
                else
                {
                    int i1 = _readIndex;
                    int i2 = (_bufferedFrames >= 2) ? ((_readIndex + 1) & CapacityMask) : i1;
                    int i3 = (_bufferedFrames >= 3) ? ((_readIndex + 2) & CapacityMask) : i2;

                    float t = (float)_srcPhase;
                    sampleL = CatmullRom(_histL1, _leftSamples[i1], _leftSamples[i2], _leftSamples[i3], t);
                    sampleR = CatmullRom(_histR1, _rightSamples[i1], _rightSamples[i2], _rightSamples[i3], t);

                    _srcPhase += step;
                    while (_srcPhase >= 1.0 && _bufferedFrames > 0)
                    {
                        _histL1 = _leftSamples[_readIndex];
                        _histR1 = _rightSamples[_readIndex];
                        _readIndex = (_readIndex + 1) & CapacityMask;
                        _bufferedFrames--;
                        _srcPhase -= 1.0;
                    }

                    if (_bufferedFrames == 0 && _srcPhase > 0.0)
                    {
                        _srcPhase = 0.0;
                    }
                }

                activeFramesProduced++;

                if (_fadeGain < 1.0f)
                {
                    _fadeGain = Math.Min(1.0f, _fadeGain + fadeStep);
                }

                float shapedL = sampleL * _fadeGain;
                float shapedR = sampleR * _fadeGain;
                _lastOutL = shapedL;
                _lastOutR = shapedR;

                WriteScaledOutput(floatOut, shortOut, writeFloat, outIdx, shapedL, shapedR, effectiveGain);
            }

            if (activeFramesProduced > 0 && Math.Abs(step - baseStep) > 1e-9)
            {
                _driftSlipAccumulator += (step - baseStep) * activeFramesProduced;
                if (_driftSlipAccumulator >= 1.0)
                {
                    long dropped = (long)_driftSlipAccumulator;
                    TotalDroppedFramesForDrift += dropped;
                    _driftSlipAccumulator -= dropped;
                }
                else if (_driftSlipAccumulator <= -1.0)
                {
                    long duplicated = (long)(-_driftSlipAccumulator);
                    TotalDuplicatedFramesForDrift += duplicated;
                    _driftSlipAccumulator += duplicated;
                }
            }
        }
    }

    private void FillDecaySilence(
        Span<float> floatOut,
        Span<short> shortOut,
        bool writeFloat,
        int startFrame,
        int endFrame,
        float gain)
    {
        for (int i = startFrame; i < endFrame; i++)
        {
            _lastOutL *= 0.985f;
            _lastOutR *= 0.985f;
            if (MathF.Abs(_lastOutL) < 1e-6f) _lastOutL = 0.0f;
            if (MathF.Abs(_lastOutR) < 1e-6f) _lastOutR = 0.0f;
            WriteScaledOutput(floatOut, shortOut, writeFloat, i, _lastOutL, _lastOutR, gain);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteScaledOutput(
        Span<float> floatOut,
        Span<short> shortOut,
        bool writeFloat,
        int frameIdx,
        float left,
        float right,
        float gain)
    {
        float outL = Math.Clamp(left * gain, -1.0f, 1.0f);
        float outR = Math.Clamp(right * gain, -1.0f, 1.0f);

        int baseIdx = frameIdx * 2;
        if (writeFloat)
        {
            floatOut[baseIdx] = outL;
            floatOut[baseIdx + 1] = outR;
        }
        else
        {
            shortOut[baseIdx] = (short)Math.Clamp((int)MathF.Round(outL * 32767.0f), short.MinValue, short.MaxValue);
            shortOut[baseIdx + 1] = (short)Math.Clamp((int)MathF.Round(outR * 32767.0f), short.MinValue, short.MaxValue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CatmullRom(float y0, float y1, float y2, float y3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2.0f * y1) +
            (-y0 + y2) * t +
            (2.0f * y0 - 5.0f * y1 + 4.0f * y2 - y3) * t2 +
            (-y0 + 3.0f * y1 - 3.0f * y2 + y3) * t3
        );
    }
}
