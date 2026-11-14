// ============================================================================
// ServiceMain.cpp — Guest Windows Service Entry Point (HvGuestBridge.exe)
// Connects AF_HYPERV (HV_GUID_PARENT) sockets to HvTouchHid & HvVirtualAudio
// ============================================================================
#include "TouchBridgeWorker.h"
#include "AudioBridgeWorker.h"

#include <windows.h>
#include <winsvc.h>
#include <cstdio>
#include <cstring>
#include <string>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ws2_32.lib")

static constexpr const wchar_t* kServiceName        = L"HvGuestBridge";
static constexpr const wchar_t* kServiceDisplayName = L"Hyper-V Console Audio & Multi-Touch Guest Bridge";
static constexpr const wchar_t* kServiceDescription =
    L"Streams low-latency WaveRT audio and 10-point multi-touch HID digitizer frames over Hyper-V Sockets (AF_HYPERV) for VMStream.";

static SERVICE_STATUS_HANDLE g_ServiceStatusHandle = nullptr;
static SERVICE_STATUS        g_ServiceStatus{};
static HANDLE                g_StopEvent           = nullptr;
static std::string           g_TcpFallbackHost;

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
    WSADATA wsaData{};
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        std::fprintf(stderr, "[HvGuestBridge] WSAStartup failed: %d\n", WSAGetLastError());
        return 1;
    }

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
