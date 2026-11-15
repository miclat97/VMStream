// ============================================================================
// AudioBridgeWorker.cpp - Guest Bidirectional Audio & KSSTATE_RUN Gate Bridge
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#include "AudioBridgeWorker.h"
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <algorithm>
#include <vector>
#include <cstdio>
#include <cstring>

#pragma comment(lib, "ole32.lib")

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

#ifndef AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
#define AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM 0x80000000
#endif

#ifndef AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
#define AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY 0x08000000
#endif

namespace {

// Lossless continuous linear resampler for incoming Host Mic PCM -> Guest Capture rate
struct CaptureStreamResampler {
    uint32_t lastSrcRate = 0;
    uint32_t lastDstRate = 0;
    uint16_t lastChannels = 0;
    double   phase = 0.0;
    int16_t  prevL = 0;
    int16_t  prevR = 0;
    std::vector<uint8_t> outputBuf;

    void Reset()
    {
        lastSrcRate = 0;
        lastDstRate = 0;
        lastChannels = 0;
        phase = 0.0;
        prevL = 0;
        prevR = 0;
    }

    const uint8_t* Process16BitPcm(
        const uint8_t* srcBytes,
        DWORD srcLenBytes,
        uint32_t srcRate,
        uint16_t srcChannels,
        uint32_t dstRate,
        DWORD* outLenBytes
    )
    {
        *outLenBytes = 0;
        if (srcBytes == nullptr || srcLenBytes == 0 || srcRate == 0 || dstRate == 0) {
            return nullptr;
        }
        if (srcChannels != HV_AUDIO_CHANNELS_MONO && srcChannels != HV_AUDIO_CHANNELS_STEREO) {
            return nullptr;
        }

        const size_t srcBlockAlign = static_cast<size_t>(srcChannels) * HV_AUDIO_BYTES_PER_SAMPLE_16;
        const size_t srcFrames = srcLenBytes / srcBlockAlign;
        if (srcFrames == 0) {
            return nullptr;
        }

        const auto* srcSamples = reinterpret_cast<const int16_t*>(srcBytes);

        // Bit-exact passthrough when rates and stereo layout already match
        if (srcRate == dstRate && srcChannels == HV_AUDIO_CHANNELS_STEREO) {
            *outLenBytes = static_cast<DWORD>(srcFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16);
            return srcBytes;
        }

        if (lastSrcRate != srcRate || lastDstRate != dstRate || lastChannels != srcChannels) {
            phase = 0.0;
            prevL = 0;
            prevR = 0;
            lastSrcRate = srcRate;
            lastDstRate = dstRate;
            lastChannels = srcChannels;
        }

        const double step = static_cast<double>(srcRate) / static_cast<double>(dstRate);
        const size_t maxOutFrames = static_cast<size_t>(
            (static_cast<double>(srcFrames) + 2.0) / step
        ) + 8;
        outputBuf.resize(maxOutFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16);
        auto* dstSamples = reinterpret_cast<int16_t*>(outputBuf.data());

        auto readFrame = [&](int64_t idx, int16_t& outL, int16_t& outR) {
            if (idx < 0) {
                outL = prevL;
                outR = prevR;
            } else {
                const size_t uIdx = static_cast<size_t>(idx);
                if (srcChannels == HV_AUDIO_CHANNELS_MONO) {
                    const int16_t m = srcSamples[uIdx];
                    outL = m;
                    outR = m;
                } else {
                    outL = srcSamples[uIdx * 2];
                    outR = srcSamples[(uIdx * 2) + 1];
                }
            }
        };

        size_t outFrames = 0;
        const double limit = static_cast<double>(srcFrames - 1);
        while (phase < limit && outFrames < maxOutFrames) {
            int64_t i0 = 0;
            double frac = 0.0;
            if (phase < 0.0) {
                i0 = -1;
                frac = phase + 1.0;
            } else {
                i0 = static_cast<int64_t>(phase);
                frac = phase - static_cast<double>(i0);
            }

            int16_t l0 = 0, r0 = 0, l1 = 0, r1 = 0;
            readFrame(i0, l0, r0);
            readFrame(i0 + 1, l1, r1);

            const double interpL = static_cast<double>(l0) + (static_cast<double>(l1) - static_cast<double>(l0)) * frac;
            const double interpR = static_cast<double>(r0) + (static_cast<double>(r1) - static_cast<double>(r0)) * frac;

            const long roundedL = static_cast<long>(interpL + (interpL >= 0.0 ? 0.5 : -0.5));
            const long roundedR = static_cast<long>(interpR + (interpR >= 0.0 ? 0.5 : -0.5));
            dstSamples[outFrames * 2] = static_cast<int16_t>(std::clamp(roundedL, -32768L, 32767L));
            dstSamples[(outFrames * 2) + 1] = static_cast<int16_t>(std::clamp(roundedR, -32768L, 32767L));
            outFrames++;
            phase += step;
        }

        // Advance coordinate frame to next chunk: old index (srcFrames - 1) becomes index -1
        readFrame(static_cast<int64_t>(srcFrames - 1), prevL, prevR);
        phase -= static_cast<double>(srcFrames);

        *outLenBytes = static_cast<DWORD>(outFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16);
        return outputBuf.data();
    }
};

class WasapiLoopbackFallback {
public:
    WasapiLoopbackFallback() = default;
    ~WasapiLoopbackFallback() { Close(); }

    bool EnsureInitialized(uint32_t desiredSampleRate)
    {
        if (m_CaptureClient != nullptr) {
            if (desiredSampleRate != 0 && m_ActiveSampleRate != desiredSampleRate) {
                Close();
                m_LastRetryTickMs = 0;
            } else {
                return true;
            }
        }

        const ULONGLONG now = GetTickCount64();
        if (now - m_LastRetryTickMs < 2500) {
            return false;
        }
        m_LastRetryTickMs = now;

        HRESULT hr = CoCreateInstance(
            __uuidof(MMDeviceEnumerator),
            nullptr,
            CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator),
            reinterpret_cast<void**>(&m_Enumerator)
        );
        if (FAILED(hr) || m_Enumerator == nullptr) {
            Close();
            return false;
        }

        hr = m_Enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &m_Device);
        if (FAILED(hr) || m_Device == nullptr) {
            Close();
            return false;
        }

        hr = m_Device->Activate(
            __uuidof(IAudioClient),
            CLSCTX_ALL,
            nullptr,
            reinterpret_cast<void**>(&m_AudioClient)
        );
        if (FAILED(hr) || m_AudioClient == nullptr) {
            Close();
            return false;
        }

        uint32_t targetRate = (desiredSampleRate == HV_AUDIO_SAMPLE_RATE_44100 ||
                               desiredSampleRate == HV_AUDIO_SAMPLE_RATE_48000)
            ? desiredSampleRate
            : 0;

        if (targetRate == 0) {
            WAVEFORMATEX* mixFmt = nullptr;
            if (SUCCEEDED(m_AudioClient->GetMixFormat(&mixFmt)) && mixFmt != nullptr) {
                if (mixFmt->nSamplesPerSec == HV_AUDIO_SAMPLE_RATE_44100 ||
                    mixFmt->nSamplesPerSec == HV_AUDIO_SAMPLE_RATE_48000)
                {
                    targetRate = mixFmt->nSamplesPerSec;
                }
                CoTaskMemFree(mixFmt);
            }
        }

        if (targetRate == 0) {
            targetRate = HV_AUDIO_SAMPLE_RATE_48000;
        }

        WAVEFORMATEX wfx{};
        wfx.wFormatTag = WAVE_FORMAT_PCM;
        wfx.nChannels = HV_AUDIO_CHANNELS_STEREO;
        wfx.nSamplesPerSec = targetRate;
        wfx.wBitsPerSample = HV_AUDIO_BITS_PER_SAMPLE_16;
        wfx.nBlockAlign = HV_AUDIO_BLOCK_ALIGN_STEREO_16;
        wfx.nAvgBytesPerSec = targetRate * HV_AUDIO_BLOCK_ALIGN_STEREO_16;
        wfx.cbSize = 0;

        DWORD streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK |
                            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        hr = m_AudioClient->Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            streamFlags,
            300000, // 30 ms buffer in 100-ns units
            0,
            &wfx,
            nullptr
        );
        if (FAILED(hr)) {
            Close();
            return false;
        }

        hr = m_AudioClient->GetService(
            __uuidof(IAudioCaptureClient),
            reinterpret_cast<void**>(&m_CaptureClient)
        );
        if (FAILED(hr) || m_CaptureClient == nullptr) {
            Close();
            return false;
        }

        hr = m_AudioClient->Start();
        if (FAILED(hr)) {
            Close();
            return false;
        }

        m_ActiveSampleRate = targetRate;
        m_PendingBytes.clear();
        std::printf(
            "[HvGuestBridge][Audio] WASAPI Loopback fallback initialized on default Guest render endpoint (%u Hz 16-bit Stereo).\n",
            m_ActiveSampleRate
        );
        return true;
    }

    DWORD ReadPeriodPcm(
        uint8_t* outBuf,
        DWORD periodBytes,
        uint32_t desiredSampleRate,
        uint32_t* outSampleRate
    )
    {
        if (!EnsureInitialized(desiredSampleRate) || m_CaptureClient == nullptr) {
            return 0;
        }

        // Recompute periodBytes for the active loopback rate so packets align cleanly with 10ms frames
        const DWORD activePeriodBytes = HvAudioPeriodBytesStereo16ForRate(m_ActiveSampleRate);
        const DWORD readSize = (periodBytes >= activePeriodBytes) ? activePeriodBytes : periodBytes;

        PumpAvailablePackets();

        if (m_PendingBytes.size() < readSize) {
            return 0;
        }

        std::memcpy(outBuf, m_PendingBytes.data(), readSize);
        m_PendingBytes.erase(m_PendingBytes.begin(), m_PendingBytes.begin() + readSize);

        if (outSampleRate != nullptr) {
            *outSampleRate = m_ActiveSampleRate;
        }
        return readSize;
    }

    void Close()
    {
        if (m_AudioClient != nullptr) {
            m_AudioClient->Stop();
        }
        if (m_CaptureClient != nullptr) {
            m_CaptureClient->Release();
            m_CaptureClient = nullptr;
        }
        if (m_AudioClient != nullptr) {
            m_AudioClient->Release();
            m_AudioClient = nullptr;
        }
        if (m_Device != nullptr) {
            m_Device->Release();
            m_Device = nullptr;
        }
        if (m_Enumerator != nullptr) {
            m_Enumerator->Release();
            m_Enumerator = nullptr;
        }
        m_PendingBytes.clear();
    }

private:
    void PumpAvailablePackets()
    {
        if (m_CaptureClient == nullptr) {
            return;
        }

        UINT32 packetLength = 0;
        while (SUCCEEDED(m_CaptureClient->GetNextPacketSize(&packetLength)) && packetLength > 0) {
            BYTE* data = nullptr;
            UINT32 numFrames = 0;
            DWORD flags = 0;

            HRESULT hr = m_CaptureClient->GetBuffer(&data, &numFrames, &flags, nullptr, nullptr);
            if (FAILED(hr)) {
                Close();
                break;
            }

            const size_t bytesInPacket = static_cast<size_t>(numFrames) * HV_AUDIO_BLOCK_ALIGN_STEREO_16;
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != nullptr && bytesInPacket > 0) {
                constexpr size_t kMaxPendingBytes = 48000;
                constexpr size_t kTrimTargetBytes = 19200;
                if (m_PendingBytes.size() + bytesInPacket > kMaxPendingBytes &&
                    m_PendingBytes.size() > kTrimTargetBytes)
                {
                    const size_t trim = (m_PendingBytes.size() - kTrimTargetBytes) & ~size_t(HV_AUDIO_BLOCK_ALIGN_STEREO_16 - 1);
                    m_PendingBytes.erase(m_PendingBytes.begin(), m_PendingBytes.begin() + trim);
                }
                m_PendingBytes.insert(m_PendingBytes.end(), data, data + bytesInPacket);
            }

            m_CaptureClient->ReleaseBuffer(numFrames);
        }
    }

    IMMDeviceEnumerator* m_Enumerator      = nullptr;
    IMMDevice*           m_Device          = nullptr;
    IAudioClient*        m_AudioClient     = nullptr;
    IAudioCaptureClient* m_CaptureClient   = nullptr;
    uint32_t             m_ActiveSampleRate = HV_AUDIO_SAMPLE_RATE;
    ULONGLONG            m_LastRetryTickMs = 0;
    std::vector<uint8_t> m_PendingBytes;
};

} // namespace

AudioBridgeWorker::AudioBridgeWorker(const std::string& tcpFallbackHost)
    : m_TcpFallbackHost(tcpFallbackHost),
      m_Transport(std::make_unique<HvSocketTransport>(
          HV_STREAM_AUDIO_SERVICE_GUID,
          HV_STREAM_TCP_PORT_AUDIO,
          tcpFallbackHost
      )),
      m_DriverHandle(INVALID_HANDLE_VALUE),
      m_Running(false),
      m_HostMicEnabled(false),
      m_GuestRenderActive(false),
      m_GuestCaptureActive(false),
      m_GuestRenderSampleRate(HV_AUDIO_SAMPLE_RATE),
      m_GuestCaptureSampleRate(HV_AUDIO_SAMPLE_RATE),
      m_HostPreferredSampleRate(0)
{
}

AudioBridgeWorker::~AudioBridgeWorker()
{
    Stop();
}

void AudioBridgeWorker::Start()
{
    bool expected = false;
    if (!m_Running.compare_exchange_strong(expected, true)) {
        return;
    }

    m_MainThread = std::thread(&AudioBridgeWorker::MainConnectionLoop, this);
}

void AudioBridgeWorker::Stop()
{
    bool expected = true;
    if (!m_Running.compare_exchange_strong(expected, false)) {
        return;
    }

    if (m_Transport) {
        m_Transport->Disconnect();
    }

    if (m_MainThread.joinable()) {
        m_MainThread.join();
    }

    CloseDriverHandle();
}

bool AudioBridgeWorker::EnsureDriverHandleOpen()
{
    if (m_DriverHandle != INVALID_HANDLE_VALUE) {
        return true;
    }

    m_DriverHandle = CreateFileW(
        HVAUDIO_WIN32_DEVICE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
        nullptr
    );

    return (m_DriverHandle != INVALID_HANDLE_VALUE);
}

void AudioBridgeWorker::CloseDriverHandle()
{
    if (m_DriverHandle != INVALID_HANDLE_VALUE) {
        CancelIoEx(m_DriverHandle, nullptr);
        CloseHandle(m_DriverHandle);
        m_DriverHandle = INVALID_HANDLE_VALUE;
    }
}

bool AudioBridgeWorker::IoctlSync(
    DWORD ioctlCode,
    const void* inBuf,
    DWORD inLen,
    void* outBuf,
    DWORD outLen,
    DWORD* bytesReturned,
    DWORD timeoutMs,
    HANDLE abortEvent
)
{
    if (!EnsureDriverHandleOpen()) {
        return false;
    }

    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (ov.hEvent == nullptr) {
        return false;
    }

    DWORD transferred = 0;
    BOOL ok = DeviceIoControl(
        m_DriverHandle,
        ioctlCode,
        const_cast<void*>(inBuf),
        inLen,
        outBuf,
        outLen,
        &transferred,
        &ov
    );

    if (!ok) {
        DWORD err = GetLastError();
        if (err == ERROR_IO_PENDING) {
            HANDLE waits[2] = { ov.hEvent, abortEvent };
            DWORD handleCount = (abortEvent != nullptr) ? 2 : 1;
            DWORD waitRes = WaitForMultipleObjects(handleCount, waits, FALSE, timeoutMs);
            if (waitRes == WAIT_OBJECT_0) {
                ok = GetOverlappedResult(m_DriverHandle, &ov, &transferred, FALSE);
            } else {
                CancelIoEx(m_DriverHandle, &ov);
                GetOverlappedResult(m_DriverHandle, &ov, &transferred, TRUE);
                ok = FALSE;
            }
        } else if (err == ERROR_INVALID_HANDLE) {
            CloseDriverHandle();
        }
    }

    CloseHandle(ov.hEvent);
    if (bytesReturned != nullptr) {
        *bytesReturned = transferred;
    }
    return (ok == TRUE);
}

// ============================================================================
// MainConnectionLoop - Connects AF_HYPERV Audio & Receives Host Mic PCM
// ============================================================================
void AudioBridgeWorker::MainConnectionLoop()
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
    std::vector<uint8_t> payloadBuf(8192);
    CaptureStreamResampler captureResampler;
    bool loggedMissingAudioDriver = false;

    while (m_Running.load(std::memory_order_acquire)) {
        if (!EnsureDriverHandleOpen() && !loggedMissingAudioDriver) {
            loggedMissingAudioDriver = true;
            std::printf("[HvGuestBridge][Audio] Note: \\\\.\\HvVirtualAudio is not loaded yet. Run Install-GuestDrivers.ps1 as Admin in the Guest VM to install 'Hyper-V Console Speakers' & 'Hyper-V Console Microphone'.\n");
        }

        if (!m_Transport->Connect(1500)) {
            Sleep(500);
            continue;
        }

        std::printf("[HvGuestBridge][Audio] Connected to Host AF_HYPERV Audio Service.\n");
        captureResampler.Reset();

        HANDLE stopSessionEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::thread renderThread(&AudioBridgeWorker::RenderSenderLoop, this, stopSessionEvent);
        std::thread stateThread(&AudioBridgeWorker::StateMonitorLoop, this, stopSessionEvent);

        while (m_Running.load(std::memory_order_acquire) && m_Transport->IsConnected()) {
            HV_PACKET_HEADER hdr{};
            if (!m_Transport->RecvExact(&hdr, sizeof(hdr))) {
                break;
            }

            if (hdr.Magic != HV_PACKET_MAGIC || hdr.Version != HV_PROTOCOL_VERSION) {
                m_Transport->Disconnect();
                break;
            }

            const auto packetType = static_cast<HV_PACKET_TYPE>(hdr.Type);

            if (packetType == HvPacketType_AudioCapturePcm &&
                hdr.PayloadLength >= sizeof(HV_AUDIO_FRAME_HEADER))
            {
                if (payloadBuf.size() < hdr.PayloadLength) {
                    payloadBuf.resize(hdr.PayloadLength);
                }
                if (!m_Transport->RecvExact(payloadBuf.data(), hdr.PayloadLength)) {
                    break;
                }

                const auto* audioHdr = reinterpret_cast<const HV_AUDIO_FRAME_HEADER*>(payloadBuf.data());
                const uint8_t* pcmBytes = payloadBuf.data() + sizeof(HV_AUDIO_FRAME_HEADER);
                const DWORD pcmLen = static_cast<DWORD>(hdr.PayloadLength - sizeof(HV_AUDIO_FRAME_HEADER));

                if ((audioHdr->Flags & HvAudioFlag_Silence) == 0 &&
                    pcmLen > 0 &&
                    audioHdr->BitsPerSample == HV_AUDIO_BITS_PER_SAMPLE_16)
                {
                    const uint32_t targetCaptureRate = m_GuestCaptureSampleRate.load(std::memory_order_acquire);
                    const uint32_t srcRate = (audioHdr->SampleRate > 0) ? audioHdr->SampleRate : targetCaptureRate;

                    DWORD writeLen = 0;
                    const uint8_t* writeData = captureResampler.Process16BitPcm(
                        pcmBytes,
                        pcmLen,
                        srcRate,
                        audioHdr->Channels,
                        targetCaptureRate,
                        &writeLen
                    );

                    if (writeData != nullptr && writeLen > 0) {
                        DWORD written = 0;
                        IoctlSync(
                            IOCTL_HVAUDIO_WRITE_CAPTURE_PCM,
                            writeData,
                            writeLen,
                            nullptr,
                            0,
                            &written,
                            50,
                            stopSessionEvent
                        );
                    }
                }
            }
            else if (packetType == HvPacketType_AudioControl &&
                     hdr.PayloadLength == sizeof(HV_AUDIO_CONTROL_PAYLOAD))
            {
                HV_AUDIO_CONTROL_PAYLOAD ctrl{};
                if (!m_Transport->RecvExact(&ctrl, sizeof(ctrl))) {
                    break;
                }
                m_HostMicEnabled.store(ctrl.HostMicEnabled != 0, std::memory_order_release);

                const uint8_t prefCode = ctrl.SampleRateConfig & 0x0Fu;
                uint32_t preferredRate = 0;
                if (prefCode == HvAudioRate_44100) {
                    preferredRate = HV_AUDIO_SAMPLE_RATE_44100;
                } else if (prefCode == HvAudioRate_48000) {
                    preferredRate = HV_AUDIO_SAMPLE_RATE_48000;
                }
                m_HostPreferredSampleRate.store(preferredRate, std::memory_order_release);
            }
            else if (packetType == HvPacketType_Ping &&
                     hdr.PayloadLength == sizeof(HV_PING_PAYLOAD))
            {
                HV_PING_PAYLOAD ping{};
                if (!m_Transport->RecvExact(&ping, sizeof(ping))) {
                    break;
                }
                LARGE_INTEGER qpc{};
                QueryPerformanceCounter(&qpc);
                ping.GuestTimestampQpc = static_cast<uint64_t>(qpc.QuadPart);
                m_Transport->SendPacket(HvPacketType_Pong, &ping, sizeof(ping));
            }
            else {
                if (hdr.PayloadLength > 0) {
                    if (payloadBuf.size() < hdr.PayloadLength) {
                        payloadBuf.resize(hdr.PayloadLength);
                    }
                    if (!m_Transport->RecvExact(payloadBuf.data(), hdr.PayloadLength)) {
                        break;
                    }
                }
            }
        }

        // Signal helper threads to exit cleanly
        SetEvent(stopSessionEvent);
        m_Transport->Disconnect();

        if (renderThread.joinable()) {
            renderThread.join();
        }
        if (stateThread.joinable()) {
            stateThread.join();
        }
        CloseHandle(stopSessionEvent);

        if (m_Running.load(std::memory_order_acquire)) {
            Sleep(250);
        }
    }
}

// ============================================================================
// RenderSenderLoop - Pumps Speaker PCM Frames (44.1 / 48 kHz 16-bit Stereo) to Host
// Uses a 5ms high-res pump to eliminate 10ms timer phase-beat bursts/gaps vs DPC.
// Reads from HvVirtualAudio.sys WaveRT ring buffer, with automatic fallback to
// WASAPI Loopback capture if another audio endpoint is active in the Guest VM.
// ============================================================================
void AudioBridgeWorker::RenderSenderLoop(HANDLE stopSessionEvent)
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
    HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    constexpr DWORD kPumpIntervalMs = 5;
    HANDLE hTimer = CreateWaitableTimerExW(
        nullptr,
        nullptr,
        CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
        TIMER_ALL_ACCESS
    );
    if (hTimer == nullptr) {
        hTimer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    }

    if (hTimer != nullptr) {
        LARGE_INTEGER dueTime{};
        dueTime.QuadPart = -50000LL; // 5 ms
        SetWaitableTimer(hTimer, &dueTime, kPumpIntervalMs, nullptr, nullptr, FALSE);
    }

    WasapiLoopbackFallback loopbackFallback;
    uint8_t pcmBuffer[HV_AUDIO_PERIOD_BYTES_STEREO_16 * 2]{};
    HANDLE waitHandles[2] = { stopSessionEvent, hTimer };
    DWORD waitCount = (hTimer != nullptr) ? 2 : 1;

    while (m_Running.load(std::memory_order_acquire) && m_Transport->IsConnected()) {
        DWORD waitResult = WaitForMultipleObjects(waitCount, waitHandles, FALSE, kPumpIntervalMs);
        if (waitResult == WAIT_OBJECT_0) {
            break; // stopSessionEvent signaled
        }

        if (EnsureDriverHandleOpen()) {
            loopbackFallback.Close();

            // Primary path: Drain up to 4 periods from HvVirtualAudio.sys kernel WaveRT ring buffer
            for (int pass = 0; pass < 4; ++pass) {
                const uint32_t activeRate = m_GuestRenderSampleRate.load(std::memory_order_acquire);
                const DWORD periodBytes = HvAudioPeriodBytesStereo16ForRate(activeRate);

                DWORD bytesRead = 0;
                bool ok = IoctlSync(
                    IOCTL_HVAUDIO_READ_RENDER_PCM,
                    nullptr,
                    0,
                    pcmBuffer,
                    periodBytes,
                    &bytesRead,
                    20,
                    stopSessionEvent
                );

                if (!ok || bytesRead < HV_AUDIO_BLOCK_ALIGN_STEREO_16) {
                    break;
                }

                HV_AUDIO_FRAME_HEADER frameHdr{};
                frameHdr.SampleRate = activeRate;
                frameHdr.Channels = HV_AUDIO_CHANNELS_STEREO;
                frameHdr.BitsPerSample = HV_AUDIO_BITS_PER_SAMPLE_16;
                frameHdr.FrameSamples = static_cast<uint16_t>(bytesRead / HV_AUDIO_BLOCK_ALIGN_STEREO_16);
                frameHdr.Flags = HvAudioFlag_None;

                if (!m_Transport->SendPacket(
                        HvPacketType_AudioRenderPcm,
                        &frameHdr,
                        sizeof(frameHdr),
                        pcmBuffer,
                        static_cast<uint16_t>(bytesRead)))
                {
                    break;
                }
            }
        } else {
            // Fallback path: HvVirtualAudio.sys is not open yet -> WASAPI Loopback on default Guest render endpoint
            const uint32_t preferredRate = m_HostPreferredSampleRate.load(std::memory_order_acquire);
            const DWORD requestedPeriodBytes = (preferredRate != 0)
                ? HvAudioPeriodBytesStereo16ForRate(preferredRate)
                : HV_AUDIO_PERIOD_BYTES_STEREO_16;

            for (int pass = 0; pass < 2; ++pass) {
                uint32_t actualRate = HV_AUDIO_SAMPLE_RATE;
                DWORD loopbackBytes = loopbackFallback.ReadPeriodPcm(
                    pcmBuffer,
                    requestedPeriodBytes,
                    preferredRate,
                    &actualRate
                );
                if (loopbackBytes < HV_AUDIO_BLOCK_ALIGN_STEREO_16) {
                    break;
                }

                HV_AUDIO_FRAME_HEADER frameHdr{};
                frameHdr.SampleRate = actualRate;
                frameHdr.Channels = HV_AUDIO_CHANNELS_STEREO;
                frameHdr.BitsPerSample = HV_AUDIO_BITS_PER_SAMPLE_16;
                frameHdr.FrameSamples = static_cast<uint16_t>(loopbackBytes / HV_AUDIO_BLOCK_ALIGN_STEREO_16);
                frameHdr.Flags = HvAudioFlag_None;

                if (!m_Transport->SendPacket(
                        HvPacketType_AudioRenderPcm,
                        &frameHdr,
                        sizeof(frameHdr),
                        pcmBuffer,
                        static_cast<uint16_t>(loopbackBytes)))
                {
                    break;
                }
            }
        }
    }

    loopbackFallback.Close();

    if (hTimer != nullptr) {
        CancelWaitableTimer(hTimer);
        CloseHandle(hTimer);
    }

    if (SUCCEEDED(hrCom)) {
        CoUninitialize();
    }
}

// ============================================================================
// StateMonitorLoop - Inverted Call Monitor for KSSTATE_RUN & Format Transitions
// Immediately notifies Host when Guest opens/closes Mic or changes 44.1/48 kHz!
// ============================================================================
void AudioBridgeWorker::StateMonitorLoop(HANDLE stopSessionEvent)
{
    HV_AUDIO_CONTROL_PAYLOAD currentState{};
    DWORD bytesReturned = 0;

    // 1. Send initial state immediately on connection
    if (IoctlSync(
            IOCTL_HVAUDIO_GET_STATE,
            nullptr,
            0,
            &currentState,
            sizeof(currentState),
            &bytesReturned,
            200,
            stopSessionEvent))
    {
        currentState.HostMicEnabled = m_HostMicEnabled.load(std::memory_order_acquire) ? 1 : 0;
        m_GuestCaptureActive.store(currentState.GuestCaptureActive != 0, std::memory_order_release);
        m_GuestRenderActive.store(currentState.GuestRenderActive != 0, std::memory_order_release);
        m_GuestRenderSampleRate.store(HvDecodeRenderRateFromConfig(currentState.SampleRateConfig), std::memory_order_release);
        m_GuestCaptureSampleRate.store(HvDecodeCaptureRateFromConfig(currentState.SampleRateConfig), std::memory_order_release);
        m_Transport->SendPacket(HvPacketType_AudioControl, &currentState, sizeof(currentState));
    }

    // 2. Inverted-call wait loop on IOCTL_HVAUDIO_WAIT_STATE_CHANGE
    while (m_Running.load(std::memory_order_acquire) && m_Transport->IsConnected()) {
        if (WaitForSingleObject(stopSessionEvent, 0) == WAIT_OBJECT_0) {
            break;
        }

        HV_AUDIO_CONTROL_PAYLOAD updatedState = currentState;
        bytesReturned = 0;
        bool completed = IoctlSync(
            IOCTL_HVAUDIO_WAIT_STATE_CHANGE,
            &currentState,
            sizeof(currentState),
            &updatedState,
            sizeof(updatedState),
            &bytesReturned,
            2000,
            stopSessionEvent
        );

        if (completed && bytesReturned == sizeof(HV_AUDIO_CONTROL_PAYLOAD)) {
            if (updatedState.SequenceGeneration != currentState.SequenceGeneration ||
                updatedState.GuestCaptureActive != currentState.GuestCaptureActive ||
                updatedState.GuestRenderActive  != currentState.GuestRenderActive  ||
                updatedState.SampleRateConfig   != currentState.SampleRateConfig)
            {
                currentState = updatedState;
                currentState.HostMicEnabled = m_HostMicEnabled.load(std::memory_order_acquire) ? 1 : 0;

                const uint32_t renderRate = HvDecodeRenderRateFromConfig(currentState.SampleRateConfig);
                const uint32_t captureRate = HvDecodeCaptureRateFromConfig(currentState.SampleRateConfig);

                m_GuestCaptureActive.store(currentState.GuestCaptureActive != 0, std::memory_order_release);
                m_GuestRenderActive.store(currentState.GuestRenderActive != 0, std::memory_order_release);
                m_GuestRenderSampleRate.store(renderRate, std::memory_order_release);
                m_GuestCaptureSampleRate.store(captureRate, std::memory_order_release);

                std::printf(
                    "[HvGuestBridge][Audio] Guest state: Capture=%u (%u Hz), Render=%u (%u Hz) (Gen=%u)\n",
                    currentState.GuestCaptureActive,
                    captureRate,
                    currentState.GuestRenderActive,
                    renderRate,
                    currentState.SequenceGeneration
                );

                m_Transport->SendPacket(HvPacketType_AudioControl, &currentState, sizeof(currentState));
            }
        } else if (!EnsureDriverHandleOpen()) {
            // Driver not yet loaded; wait briefly before retrying
            if (WaitForSingleObject(stopSessionEvent, 500) == WAIT_OBJECT_0) {
                break;
            }
        }
    }
}
