// ============================================================================
// AudioBridgeWorker.cpp - Guest Bidirectional Audio & KSSTATE_RUN Gate Bridge
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#include "AudioBridgeWorker.h"
#include <mmdeviceapi.h>
#include <audioclient.h>
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

class WasapiLoopbackFallback {
public:
    WasapiLoopbackFallback() = default;
    ~WasapiLoopbackFallback() { Close(); }

    bool EnsureInitialized()
    {
        if (m_CaptureClient != nullptr) {
            return true;
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

        WAVEFORMATEX wfx{};
        wfx.wFormatTag = WAVE_FORMAT_PCM;
        wfx.nChannels = HV_AUDIO_CHANNELS_STEREO;
        wfx.nSamplesPerSec = HV_AUDIO_SAMPLE_RATE;
        wfx.wBitsPerSample = HV_AUDIO_BITS_PER_SAMPLE_16;
        wfx.nBlockAlign = HV_AUDIO_BLOCK_ALIGN_STEREO_16;
        wfx.nAvgBytesPerSec = HV_AUDIO_SAMPLE_RATE * HV_AUDIO_BLOCK_ALIGN_STEREO_16;
        wfx.cbSize = 0;

        DWORD streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK |
                            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        hr = m_AudioClient->Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            streamFlags,
            200000, // 20 ms buffer in 100-ns units
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

        std::printf("[HvGuestBridge][Audio] WASAPI Loopback fallback initialized on default Guest render endpoint (48kHz 16-bit Stereo).\n");
        return true;
    }

    DWORD ReadAvailablePcm(uint8_t* outBuf, DWORD maxBytes)
    {
        if (!EnsureInitialized() || m_CaptureClient == nullptr) {
            return 0;
        }

        DWORD totalCopied = 0;
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

            DWORD bytesInPacket = numFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16;
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != nullptr && bytesInPacket > 0) {
                DWORD toCopy = (totalCopied + bytesInPacket <= maxBytes)
                    ? bytesInPacket
                    : (maxBytes - totalCopied);
                if (toCopy > 0) {
                    std::memcpy(outBuf + totalCopied, data, toCopy);
                    totalCopied += toCopy;
                }
            }

            m_CaptureClient->ReleaseBuffer(numFrames);
            if (totalCopied >= maxBytes) {
                break;
            }
        }

        return totalCopied;
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
    }

private:
    IMMDeviceEnumerator* m_Enumerator     = nullptr;
    IMMDevice*           m_Device         = nullptr;
    IAudioClient*        m_AudioClient    = nullptr;
    IAudioCaptureClient* m_CaptureClient  = nullptr;
    ULONGLONG            m_LastRetryTickMs = 0;
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
      m_GuestCaptureActive(false)
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

                if ((audioHdr->Flags & HvAudioFlag_Silence) == 0 && pcmLen > 0) {
                    DWORD written = 0;
                    IoctlSync(
                        IOCTL_HVAUDIO_WRITE_CAPTURE_PCM,
                        pcmBytes,
                        pcmLen,
                        nullptr,
                        0,
                        &written,
                        50,
                        stopSessionEvent
                    );
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
// RenderSenderLoop - Pumps 10ms Speaker PCM Frames (48kHz 16-bit Stereo) to Host
// Reads from HvVirtualAudio.sys WaveRT ring buffer, with automatic fallback to
// WASAPI Loopback capture if another audio endpoint is active in the Guest VM.
// ============================================================================
void AudioBridgeWorker::RenderSenderLoop(HANDLE stopSessionEvent)
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
    HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

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
        dueTime.QuadPart = -100000LL; // 10 ms
        SetWaitableTimer(hTimer, &dueTime, HV_AUDIO_PERIOD_MS, nullptr, nullptr, FALSE);
    }

    WasapiLoopbackFallback loopbackFallback;
    uint8_t pcmBuffer[HV_AUDIO_PERIOD_BYTES_STEREO_16 * 2]{};
    HANDLE waitHandles[2] = { stopSessionEvent, hTimer };
    DWORD waitCount = (hTimer != nullptr) ? 2 : 1;

    while (m_Running.load(std::memory_order_acquire) && m_Transport->IsConnected()) {
        DWORD waitResult = WaitForMultipleObjects(waitCount, waitHandles, FALSE, HV_AUDIO_PERIOD_MS);
        if (waitResult == WAIT_OBJECT_0) {
            break; // stopSessionEvent signaled
        }

        bool sentFromKernelWaveRt = false;

        // 1. Primary path: Drain up to 2 periods from HvVirtualAudio.sys kernel WaveRT ring buffer
        if (EnsureDriverHandleOpen()) {
            for (int pass = 0; pass < 2; ++pass) {
                DWORD bytesRead = 0;
                bool ok = IoctlSync(
                    IOCTL_HVAUDIO_READ_RENDER_PCM,
                    nullptr,
                    0,
                    pcmBuffer,
                    HV_AUDIO_PERIOD_BYTES_STEREO_16,
                    &bytesRead,
                    20,
                    stopSessionEvent
                );

                if (!ok || bytesRead == 0) {
                    break;
                }

                sentFromKernelWaveRt = true;

                HV_AUDIO_FRAME_HEADER frameHdr{};
                frameHdr.SampleRate = HV_AUDIO_SAMPLE_RATE;
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
        }

        // 2. Fallback path: If HvVirtualAudio.sys is not open yet, try WASAPI Loopback on any active Guest audio device
        if (!sentFromKernelWaveRt && !EnsureDriverHandleOpen()) {
            DWORD loopbackBytes = loopbackFallback.ReadAvailablePcm(pcmBuffer, HV_AUDIO_PERIOD_BYTES_STEREO_16);
            if (loopbackBytes > 0) {
                HV_AUDIO_FRAME_HEADER frameHdr{};
                frameHdr.SampleRate = HV_AUDIO_SAMPLE_RATE;
                frameHdr.Channels = HV_AUDIO_CHANNELS_STEREO;
                frameHdr.BitsPerSample = HV_AUDIO_BITS_PER_SAMPLE_16;
                frameHdr.FrameSamples = static_cast<uint16_t>(loopbackBytes / HV_AUDIO_BLOCK_ALIGN_STEREO_16);
                frameHdr.Flags = HvAudioFlag_None;

                m_Transport->SendPacket(
                    HvPacketType_AudioRenderPcm,
                    &frameHdr,
                    sizeof(frameHdr),
                    pcmBuffer,
                    static_cast<uint16_t>(loopbackBytes)
                );
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
// StateMonitorLoop - Inverted Call Monitor for KSSTATE_RUN Transitions
// Immediately notifies Host when a Guest app opens/closes the Microphone!
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
                updatedState.GuestRenderActive  != currentState.GuestRenderActive)
            {
                currentState = updatedState;
                currentState.HostMicEnabled = m_HostMicEnabled.load(std::memory_order_acquire) ? 1 : 0;
                m_GuestCaptureActive.store(currentState.GuestCaptureActive != 0, std::memory_order_release);
                m_GuestRenderActive.store(currentState.GuestRenderActive != 0, std::memory_order_release);

                std::printf(
                    "[HvGuestBridge][Audio] Guest KSSTATE transition: Capture=%u Render=%u (Gen=%u)\n",
                    currentState.GuestCaptureActive,
                    currentState.GuestRenderActive,
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
