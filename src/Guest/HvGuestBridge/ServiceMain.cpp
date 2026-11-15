// ============================================================================
// ServiceMain.cpp - Guest Windows Service Entry Point (HvGuestBridge.exe)
// Connects AF_HYPERV (HV_GUID_PARENT) sockets to HvTouchHid & HvVirtualAudio
// Includes native SetupAPI + newdev.dll Root PnP DevNode auto-installer.
// ============================================================================
#include "TouchBridgeWorker.h"
#include "AudioBridgeWorker.h"

#include <windows.h>
#include <winsvc.h>
#include <cfgmgr32.h>
#include <setupapi.h>
#include <newdev.h>
#include <mmsystem.h>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>

#ifndef MAX_CLASS_NAME_LEN
#define MAX_CLASS_NAME_LEN 32
#endif

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "newdev.lib")
#pragma comment(lib, "winmm.lib")

static constexpr const wchar_t* kServiceName        = L"HvGuestBridge";
static constexpr const wchar_t* kServiceDisplayName = L"Hyper-V Console Audio & Multi-Touch Guest Bridge";
static constexpr const wchar_t* kServiceDescription =
    L"Streams low-latency WaveRT audio and 10-point multi-touch HID digitizer frames over Hyper-V Sockets (AF_HYPERV) for VMStream.";

static SERVICE_STATUS_HANDLE g_ServiceStatusHandle = nullptr;
static SERVICE_STATUS        g_ServiceStatus{};
static HANDLE                g_StopEvent           = nullptr;
static std::string           g_TcpFallbackHost;

// ============================================================================
// Native Root-Enumerated PnP Driver Installer (SetupAPI + newdev.dll)
// Creates Root\HvTouchHid and Root\HvVirtualAudio DevNodes without devcon.exe
// ============================================================================
static bool DoesRootDeviceNodeExist(const wchar_t* targetHardwareId)
{
    HDEVINFO devInfo = SetupDiGetClassDevsW(nullptr, L"ROOT", nullptr, DIGCF_ALLCLASSES);
    if (devInfo == INVALID_HANDLE_VALUE) {
        return false;
    }

    bool found = false;
    SP_DEVINFO_DATA devData{};
    devData.cbSize = sizeof(SP_DEVINFO_DATA);

    for (DWORD index = 0; SetupDiEnumDeviceInfo(devInfo, index, &devData); ++index) {
        wchar_t hwidBuf[512]{};
        DWORD regType = 0;
        if (SetupDiGetDeviceRegistryPropertyW(
                devInfo,
                &devData,
                SPDRP_HARDWAREID,
                &regType,
                reinterpret_cast<PBYTE>(hwidBuf),
                sizeof(hwidBuf) - sizeof(wchar_t),
                nullptr))
        {
            for (const wchar_t* p = hwidBuf; *p != L'\0'; p += std::wcslen(p) + 1) {
                if (_wcsicmp(p, targetHardwareId) == 0) {
                    found = true;
                    break;
                }
            }
        }
        if (found) {
            break;
        }
    }

    SetupDiDestroyDeviceInfoList(devInfo);
    return found;
}

static bool InstallRootEnumeratedDriver(const wchar_t* hardwareId, const wchar_t* infPath)
{
    wchar_t fullInfPath[MAX_PATH]{};
    if (GetFullPathNameW(infPath, MAX_PATH, fullInfPath, nullptr) == 0) {
        return false;
    }

    if (GetFileAttributesW(fullInfPath) == INVALID_FILE_ATTRIBUTES) {
        std::fprintf(stderr, "[HvGuestBridge] INF file not found: %ls\n", fullInfPath);
        return false;
    }

    GUID classGuid{};
    wchar_t className[MAX_CLASS_NAME_LEN]{};
    DWORD requiredSize = 0;
    if (!SetupDiGetINFClassW(fullInfPath, &classGuid, className, MAX_CLASS_NAME_LEN, &requiredSize)) {
        std::fprintf(stderr, "[HvGuestBridge] SetupDiGetINFClassW failed for %ls (error=%lu)\n", fullInfPath, GetLastError());
        return false;
    }

    // Create the Root\<Device> PnP DevNode if it does not already exist
    if (!DoesRootDeviceNodeExist(hardwareId)) {
        HDEVINFO devInfoSet = SetupDiCreateDeviceInfoList(&classGuid, nullptr);
        if (devInfoSet == INVALID_HANDLE_VALUE) {
            std::fprintf(stderr, "[HvGuestBridge] SetupDiCreateDeviceInfoList failed (error=%lu)\n", GetLastError());
            return false;
        }

        SP_DEVINFO_DATA devInfoData{};
        devInfoData.cbSize = sizeof(SP_DEVINFO_DATA);

        if (!SetupDiCreateDeviceInfoW(
                devInfoSet,
                className,
                &classGuid,
                nullptr,
                nullptr,
                DICD_GENERATE_ID,
                &devInfoData))
        {
            std::fprintf(stderr, "[HvGuestBridge] SetupDiCreateDeviceInfoW failed (error=%lu)\n", GetLastError());
            SetupDiDestroyDeviceInfoList(devInfoSet);
            return false;
        }

        // Build REG_MULTI_SZ double-null-terminated hardware ID buffer
        std::vector<wchar_t> multiSz(std::wcslen(hardwareId) + 2, L'\0');
        std::memcpy(multiSz.data(), hardwareId, std::wcslen(hardwareId) * sizeof(wchar_t));

        if (!SetupDiSetDeviceRegistryPropertyW(
                devInfoSet,
                &devInfoData,
                SPDRP_HARDWAREID,
                reinterpret_cast<const BYTE*>(multiSz.data()),
                static_cast<DWORD>(multiSz.size() * sizeof(wchar_t))))
        {
            std::fprintf(stderr, "[HvGuestBridge] SetupDiSetDeviceRegistryPropertyW failed (error=%lu)\n", GetLastError());
            SetupDiDestroyDeviceInfoList(devInfoSet);
            return false;
        }

        if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devInfoSet, &devInfoData)) {
            std::fprintf(stderr, "[HvGuestBridge] SetupDiCallClassInstaller(DIF_REGISTERDEVICE) failed (error=%lu)\n", GetLastError());
            SetupDiDestroyDeviceInfoList(devInfoSet);
            return false;
        }

        SetupDiDestroyDeviceInfoList(devInfoSet);
        std::printf("[HvGuestBridge] Created Root PnP device node: %ls\n", hardwareId);
    }

    BOOL rebootRequired = FALSE;
    if (!UpdateDriverForPlugAndPlayDevicesW(
            nullptr,
            hardwareId,
            fullInfPath,
            INSTALLFLAG_FORCE,
            &rebootRequired))
    {
        DWORD err = GetLastError();
        std::fprintf(
            stderr,
            "[HvGuestBridge] UpdateDriverForPlugAndPlayDevicesW(%ls) returned error=0x%08lX (%lu).\n",
            hardwareId,
            err,
            err
        );
        return false;
    }

    std::printf("[HvGuestBridge] Successfully installed WDK driver '%ls' from %ls\n", hardwareId, fullInfPath);
    return true;
}

static bool IsDeviceSymbolicLinkAvailable(const wchar_t* devicePath)
{
    HANDLE h = CreateFileW(
        devicePath,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );
    if (h != INVALID_HANDLE_VALUE) {
        CloseHandle(h);
        return true;
    }
    return false;
}

static bool DoesHvTouchSupportMouseScroll()
{
    HANDLE h = CreateFileW(
        HVTOUCH_WIN32_DEVICE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );
    if (h == INVALID_HANDLE_VALUE) {
        return false;
    }

    HV_MOUSE_SCROLL_PAYLOAD probe{};
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(
        h,
        IOCTL_HVTOUCH_SUBMIT_MOUSE_SCROLL,
        &probe,
        sizeof(probe),
        nullptr,
        0,
        &bytesReturned,
        nullptr
    );
    const DWORD err = ok ? ERROR_SUCCESS : GetLastError();
    CloseHandle(h);

    return ok || err != ERROR_INVALID_FUNCTION;
}

static void AutoInstallAdjacentDriversIfNeeded()
{
    wchar_t modulePath[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, modulePath, MAX_PATH) == 0) {
        return;
    }

    std::wstring exeDir(modulePath);
    size_t lastSlash = exeDir.find_last_of(L"\\/");
    if (lastSlash != std::wstring::npos) {
        exeDir.resize(lastSlash);
    }

    // 1. Check HvTouchHid.sys (install if missing or upgrade if older build lacks Mouse Scroll IOCTL)
    const bool touchPresent = IsDeviceSymbolicLinkAvailable(HVTOUCH_WIN32_DEVICE_PATH);
    if (!touchPresent || !DoesHvTouchSupportMouseScroll()) {
        std::wstring touchInf = exeDir + L"\\HvTouchHid\\HvTouchHid.inf";
        std::wstring touchSys = exeDir + L"\\HvTouchHid\\HvTouchHid.sys";
        if (GetFileAttributesW(touchInf.c_str()) != INVALID_FILE_ATTRIBUTES &&
            GetFileAttributesW(touchSys.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            std::printf(
                "[HvGuestBridge] %s adjacent Multi-Touch + Hi-Res Scroll WDK driver (%ls)...\n",
                touchPresent ? "Upgrading" : "Auto-installing",
                touchInf.c_str()
            );
            InstallRootEnumeratedDriver(L"Root\\HvTouchHid", touchInf.c_str());
        }
    }

    // 2. Check HvVirtualAudio.sys
    if (!IsDeviceSymbolicLinkAvailable(HVAUDIO_WIN32_DEVICE_PATH)) {
        std::wstring audioInf = exeDir + L"\\HvVirtualAudio\\HvVirtualAudio.inf";
        std::wstring audioSys = exeDir + L"\\HvVirtualAudio\\HvVirtualAudio.sys";
        if (GetFileAttributesW(audioInf.c_str()) != INVALID_FILE_ATTRIBUTES &&
            GetFileAttributesW(audioSys.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            std::printf("[HvGuestBridge] Auto-installing adjacent Virtual Audio WDK driver (%ls)...\n", audioInf.c_str());
            InstallRootEnumeratedDriver(L"Root\\HvVirtualAudio", audioInf.c_str());
        }
    }
}

// ============================================================================
// Service Status Helper
// ============================================================================
static void ReportServiceStatus(DWORD currentState, DWORD win32ExitCode, DWORD waitHintMs)
{
    static DWORD checkPoint = 1;

    g_ServiceStatus.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    g_ServiceStatus.dwCurrentState = currentState;
    g_ServiceStatus.dwWin32ExitCode = win32ExitCode;
    g_ServiceStatus.dwWaitHint = waitHintMs;

    if (currentState == SERVICE_START_PENDING) {
        g_ServiceStatus.dwControlsAccepted = 0;
    } else {
        g_ServiceStatus.dwControlsAccepted = SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN;
    }

    if (currentState == SERVICE_RUNNING || currentState == SERVICE_STOPPED) {
        g_ServiceStatus.dwCheckPoint = 0;
    } else {
        g_ServiceStatus.dwCheckPoint = checkPoint++;
    }

    if (g_ServiceStatusHandle != nullptr) {
        SetServiceStatus(g_ServiceStatusHandle, &g_ServiceStatus);
    }
}

// ============================================================================
// Service Control Handler
// ============================================================================
static DWORD WINAPI ServiceCtrlHandlerEx(
    DWORD control,
    DWORD eventType,
    LPVOID eventData,
    LPVOID context
)
{
    UNREFERENCED_PARAMETER(eventType);
    UNREFERENCED_PARAMETER(eventData);
    UNREFERENCED_PARAMETER(context);

    switch (control) {
    case SERVICE_CONTROL_STOP:
    case SERVICE_CONTROL_SHUTDOWN:
        ReportServiceStatus(SERVICE_STOP_PENDING, NO_ERROR, 3000);
        if (g_StopEvent != nullptr) {
            SetEvent(g_StopEvent);
        }
        return NO_ERROR;

    case SERVICE_CONTROL_INTERROGATE:
        return NO_ERROR;

    default:
        return ERROR_CALL_NOT_IMPLEMENTED;
    }
}

// ============================================================================
// Core Bridge Runtime (Used by both SCM Service Mode and --console Mode)
// ============================================================================
static int RunBridgeEngine(HANDLE stopSignalEvent)
{
    timeBeginPeriod(1);

    WSADATA wsaData{};
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        std::fprintf(stderr, "[HvGuestBridge] WSAStartup failed: %d\n", WSAGetLastError());
        timeEndPeriod(1);
        return 1;
    }

    // Automatically install HvTouchHid.sys and HvVirtualAudio.sys if staged next to HvGuestBridge.exe
    AutoInstallAdjacentDriversIfNeeded();

    TouchBridgeWorker touchWorker(g_TcpFallbackHost);
    AudioBridgeWorker audioWorker(g_TcpFallbackHost);

    touchWorker.Start();
    audioWorker.Start();

    if (g_ServiceStatusHandle != nullptr) {
        ReportServiceStatus(SERVICE_RUNNING, NO_ERROR, 0);
    }

    std::printf("[HvGuestBridge] Running Touch + Audio AF_HYPERV bridge workers.\n");
    WaitForSingleObject(stopSignalEvent, INFINITE);
    std::printf("[HvGuestBridge] Stopping bridge workers and resetting active touch contacts...\n");

    touchWorker.Stop();
    audioWorker.Stop();

    WSACleanup();
    timeEndPeriod(1);
    return 0;
}

// ============================================================================
// SCM ServiceMain Callback
// ============================================================================
static VOID WINAPI ServiceMain(DWORD argc, LPWSTR* argv)
{
    UNREFERENCED_PARAMETER(argc);
    UNREFERENCED_PARAMETER(argv);

    g_ServiceStatusHandle = RegisterServiceCtrlHandlerExW(
        kServiceName,
        ServiceCtrlHandlerEx,
        nullptr
    );
    if (g_ServiceStatusHandle == nullptr) {
        return;
    }

    ReportServiceStatus(SERVICE_START_PENDING, NO_ERROR, 3000);

    g_StopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (g_StopEvent == nullptr) {
        ReportServiceStatus(SERVICE_STOPPED, GetLastError(), 0);
        return;
    }

    int rc = RunBridgeEngine(g_StopEvent);

    CloseHandle(g_StopEvent);
    g_StopEvent = nullptr;

    ReportServiceStatus(SERVICE_STOPPED, (rc == 0) ? NO_ERROR : ERROR_SERVICE_SPECIFIC_ERROR, 0);
}

// ============================================================================
// Service Installation / Removal Helpers (--install / --uninstall)
// ============================================================================
static int InstallWindowsService()
{
    wchar_t modulePath[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, modulePath, MAX_PATH) == 0) {
        return 1;
    }

    std::wstring quotedPath = L"\"";
    quotedPath += modulePath;
    quotedPath += L"\"";

    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CREATE_SERVICE);
    if (scm == nullptr) {
        std::fprintf(stderr, "[HvGuestBridge] OpenSCManagerW failed (error=%lu). Run as Administrator.\n", GetLastError());
        return 1;
    }

    SC_HANDLE svc = CreateServiceW(
        scm,
        kServiceName,
        kServiceDisplayName,
        SERVICE_ALL_ACCESS,
        SERVICE_WIN32_OWN_PROCESS,
        SERVICE_AUTO_START,
        SERVICE_ERROR_NORMAL,
        quotedPath.c_str(),
        nullptr,
        nullptr,
        nullptr,
        nullptr, // LocalSystem
        nullptr
    );

    if (svc == nullptr && GetLastError() == ERROR_SERVICE_EXISTS) {
        svc = OpenServiceW(scm, kServiceName, SERVICE_ALL_ACCESS);
        if (svc != nullptr) {
            ChangeServiceConfigW(
                svc,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                quotedPath.c_str(),
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                kServiceDisplayName
            );
        }
    }

    if (svc == nullptr) {
        std::fprintf(stderr, "[HvGuestBridge] Failed to create/open service (error=%lu).\n", GetLastError());
        CloseServiceHandle(scm);
        return 1;
    }

    SERVICE_DESCRIPTIONW desc{};
    desc.lpDescription = const_cast<LPWSTR>(kServiceDescription);
    ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, &desc);

    // Configure automatic restart on failure
    SC_ACTION actions[3]{};
    actions[0].Type = SC_ACTION_RESTART;
    actions[0].Delay = 1000;
    actions[1].Type = SC_ACTION_RESTART;
    actions[1].Delay = 2000;
    actions[2].Type = SC_ACTION_RESTART;
    actions[2].Delay = 5000;

    SERVICE_FAILURE_ACTIONSW failureActions{};
    failureActions.dwResetPeriod = 86400;
    failureActions.cActions = 3;
    failureActions.lpsaActions = actions;
    ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, &failureActions);

    StartServiceW(svc, 0, nullptr);

    std::printf("[HvGuestBridge] Service '%ls' installed and started as LocalSystem.\n", kServiceName);
    CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return 0;
}

static int UninstallWindowsService()
{
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (scm == nullptr) {
        return 1;
    }

    SC_HANDLE svc = OpenServiceW(scm, kServiceName, SERVICE_STOP | DELETE | SERVICE_QUERY_STATUS);
    if (svc != nullptr) {
        SERVICE_STATUS status{};
        ControlService(svc, SERVICE_CONTROL_STOP, &status);
        DeleteService(svc);
        CloseServiceHandle(svc);
        std::printf("[HvGuestBridge] Service '%ls' stopped and uninstalled.\n", kServiceName);
    }

    CloseServiceHandle(scm);
    return 0;
}

// ============================================================================
// Console Ctrl+C Handler & Entry Point
// ============================================================================
static BOOL WINAPI ConsoleCtrlHandler(DWORD ctrlType)
{
    if (ctrlType == CTRL_C_EVENT || ctrlType == CTRL_BREAK_EVENT || ctrlType == CTRL_CLOSE_EVENT) {
        if (g_StopEvent != nullptr) {
            SetEvent(g_StopEvent);
        }
        return TRUE;
    }
    return FALSE;
}

static std::wstring Utf8ToWide(const char* str)
{
    if (str == nullptr || *str == '\0') return std::wstring();
    int len = MultiByteToWideChar(CP_UTF8, 0, str, -1, nullptr, 0);
    if (len <= 1) return std::wstring();
    std::wstring res(len - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, str, -1, res.data(), len);
    return res;
}

int main(int argc, char* argv[])
{
    bool consoleMode = false;

    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--console") == 0 ||
            std::strcmp(argv[i], "--debug") == 0 ||
            std::strcmp(argv[i], "-c") == 0)
        {
            consoleMode = true;
        }
        else if (std::strcmp(argv[i], "--install") == 0) {
            return InstallWindowsService();
        }
        else if (std::strcmp(argv[i], "--uninstall") == 0) {
            return UninstallWindowsService();
        }
        else if (std::strcmp(argv[i], "--install-driver") == 0 && (i + 2) < argc) {
            std::wstring hwid = Utf8ToWide(argv[++i]);
            std::wstring inf  = Utf8ToWide(argv[++i]);
            return InstallRootEnumeratedDriver(hwid.c_str(), inf.c_str()) ? 0 : 1;
        }
        else if (std::strcmp(argv[i], "--tcp-fallback") == 0 && (i + 1) < argc) {
            g_TcpFallbackHost = argv[++i];
        }
    }

    if (consoleMode) {
        g_StopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        SetConsoleCtrlHandler(ConsoleCtrlHandler, TRUE);
        std::printf("[HvGuestBridge] Running in interactive console mode. Press Ctrl+C to stop.\n");
        int rc = RunBridgeEngine(g_StopEvent);
        CloseHandle(g_StopEvent);
        g_StopEvent = nullptr;
        return rc;
    }

    SERVICE_TABLE_ENTRYW serviceTable[] = {
        { const_cast<LPWSTR>(kServiceName), ServiceMain },
        { nullptr, nullptr }
    };

    if (!StartServiceCtrlDispatcherW(serviceTable)) {
        DWORD err = GetLastError();
        if (err == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT) {
            // Launched directly from an interactive terminal without --console flag
            g_StopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            SetConsoleCtrlHandler(ConsoleCtrlHandler, TRUE);
            std::printf("[HvGuestBridge] Detected interactive launch; running in console mode.\n");
            int rc = RunBridgeEngine(g_StopEvent);
            CloseHandle(g_StopEvent);
            g_StopEvent = nullptr;
            return rc;
        }
        return static_cast<int>(err);
    }

    return 0;
}
