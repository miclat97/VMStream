// ============================================================================
// AdaptiveJitterBuffer.cs — Low-Latency Circular Buffer & Clock Drift Regulator
// Maintains ~25ms target buffer level (48 kHz, 16-bit Stereo PCM):
//   - If buffer exceeds 35ms: drops 1 stereo sample per 250 samples (+0.4% rate)
//   - If buffer drops below 15ms: interpolates/duplicates 1 stereo sample per 250 (-0.4% rate)
// Eliminates crystal oscillator drift (50-100 ppm) between Host & Guest timers.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using HvConsole.Host.Protocol;

namespace HvConsole.Host.Audio;

public sealed class AdaptiveJitterBuffer
{
    private const int SampleRate = HvProtocolConstants.AudioSampleRate; // 48,000 Hz
    private const int BytesPerStereoFrame = HvProtocolConstants.AudioBlockAlignStereo16; // 4 bytes (L16 + R16)
    private const int DriftCadenceFrames = 250; // 1 frame adjustment per 250 frames (0.4%)

    // Buffer Watermarks in Stereo Frames (48 frames = 1 ms)
    private const int TargetFrames = 25 * 48; // 25 ms = 1,200 stereo frames (4,800 bytes)
    private const int HighWatermarkFrames = 35 * 48; // 35 ms = 1,680 stereo frames (6,720 bytes)
    private const int LowWatermarkFrames = 15 * 48;  // 15 ms = 720 stereo frames (2,880 bytes)
    private const int CapacityFrames = 120 * 48;     // 120 ms max capacity = 5,760 stereo frames

    private readonly short[] _leftSamples = new short[CapacityFrames];
    private readonly short[] _rightSamples = new short[CapacityFrames];
    private readonly object _lock = new();

    private int _readIndex;
    private int _writeIndex;
    private int _bufferedFrames;
    private int _cadenceCounter;
    private short _lastLeft;
    private short _lastRight;
    private bool _prebuffering = true;

    public long TotalDroppedFramesForDrift { get; private set; }
    public long TotalDuplicatedFramesForDrift { get; private set; }
    public long TotalUnderruns { get; private set; }

    public double BufferedMilliseconds
    {
        get
        {
            lock (_lock)
            {
                return _bufferedFrames / 48.0;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _readIndex = 0;
            _writeIndex = 0;
            _bufferedFrames = 0;
            _cadenceCounter = 0;
            _prebuffering = true;
        }
    }

    /// <summary>
    /// Pushes interleaved 16-bit stereo PCM bytes from the AF_HYPERV audio socket.
    /// </summary>
    public void PushInterleavedStereo16(ReadOnlySpan<byte> pcmBytes)
    {
        int frameCount = pcmBytes.Length / BytesPerStereoFrame;
        if (frameCount <= 0) return;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcmBytes);

        lock (_lock)
        {
            // If extreme burst arrives (> 80ms), trim oldest frames down to TargetFrames
            if (_bufferedFrames + frameCount > CapacityFrames)
            {
                int drop = (_bufferedFrames + frameCount) - TargetFrames;
                if (drop > 0 && drop <= _bufferedFrames)
                {
                    _readIndex = (_readIndex + drop) % CapacityFrames;
                    _bufferedFrames -= drop;
                    TotalDroppedFramesForDrift += drop;
                }
            }

            for (int i = 0; i < frameCount; i++)
            {
                _leftSamples[_writeIndex] = samples[i * 2];
                _rightSamples[_writeIndex] = samples[(i * 2) + 1];
                _writeIndex = (_writeIndex + 1) % CapacityFrames;
                if (_bufferedFrames < CapacityFrames)
                {
                    _bufferedFrames++;
                }
            }

            if (_prebuffering && _bufferedFrames >= TargetFrames)
            {
                _prebuffering = false;
            }
        }
    }

    /// <summary>
    /// Pulls interleaved 16-bit stereo PCM frames for WASAPI playback with
    /// automatic 25ms drift compensation and volume/mute scaling.
    /// </summary>
    public void PullInterleavedStereo16(Span<byte> destination, float volume, bool muted)
    {
        int requestedFrames = destination.Length / BytesPerStereoFrame;
        if (requestedFrames <= 0) return;

        Span<short> outSamples = MemoryMarshal.Cast<byte, short>(destination);
        float effectiveGain = muted ? 0.0f : Math.Clamp(volume, 0.0f, 1.0f);

        lock (_lock)
        {
            if (_prebuffering || _bufferedFrames == 0)
            {
                if (_bufferedFrames == 0 && !_prebuffering)
                {
                    _prebuffering = true;
                    TotalUnderruns++;
                }
                destination.Clear();
                return;
            }

            for (int outIdx = 0; outIdx < requestedFrames; outIdx++)
            {
                if (_bufferedFrames == 0)
                {
                    _prebuffering = true;
                    TotalUnderruns++;
                    outSamples[outIdx * 2] = 0;
                    outSamples[(outIdx * 2) + 1] = 0;
                    continue;
                }

                _cadenceCounter++;

                // 1. High-Watermark Drift Compensation (> 35 ms):
                // Skip 1 stereo sample every 250 samples to gently drain excess latency back to 25 ms
                if (_bufferedFrames > HighWatermarkFrames && _cadenceCounter >= DriftCadenceFrames && _bufferedFrames >= 2)
                {
                    _cadenceCounter = 0;
                    _readIndex = (_readIndex + 1) % CapacityFrames;
                    _bufferedFrames--;
                    TotalDroppedFramesForDrift++;
                }

                // 2. Low-Watermark Drift Compensation (< 15 ms):
                // Duplicate/interpolate 1 stereo sample every 250 samples to prevent buffer underrun
                if (_bufferedFrames < LowWatermarkFrames && _cadenceCounter >= DriftCadenceFrames)
                {
                    _cadenceCounter = 0;
                    short nextL = _leftSamples[_readIndex];
                    short nextR = _rightSamples[_readIndex];
                    short interpL = (short)((_lastLeft + nextL) / 2);
                    short interpR = (short)((_lastRight + nextR) / 2);

                    WriteScaledSample(outSamples, outIdx, interpL, interpR, effectiveGain);
                    TotalDuplicatedFramesForDrift++;
                    continue;
                }

                short curL = _leftSamples[_readIndex];
                short curR = _rightSamples[_readIndex];
                _readIndex = (_readIndex + 1) % CapacityFrames;
                _bufferedFrames--;

                _lastLeft = curL;
                _lastRight = curR;

                WriteScaledSample(outSamples, outIdx, curL, curR, effectiveGain);
            }
        }
    }

    private static void WriteScaledSample(Span<short> outSamples, int frameIdx, short left, short right, float gain)
    {
        if (gain <= 0.0001f)
        {
            outSamples[frameIdx * 2] = 0;
            outSamples[(frameIdx * 2) + 1] = 0;
        }
        else if (gain >= 0.999f)
        {
            outSamples[frameIdx * 2] = left;
            outSamples[(frameIdx * 2) + 1] = right;
        }
        else
        {
            outSamples[frameIdx * 2] = (short)Math.Clamp((int)(left * gain), short.MinValue, short.MaxValue);
            outSamples[(frameIdx * 2) + 1] = (short)Math.Clamp((int)(right * gain), short.MinValue, short.MaxValue);
        }
    }
}
