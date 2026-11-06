#include "hooks.hpp"

// 初始化线程：不能在 DllMain 里干重活（loader lock），
// 起独立线程做 COM 初始化 + MinHook 初始化 + 装载所有 hook。
static DWORD WINAPI InitThread(LPVOID) {
    // WMI hook 里会 CoCreateInstance，需要先初始化 COM
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    if (MH_Initialize() != MH_OK)
        return 0;

    Spoofer::Initialize();
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        HANDLE h = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    }
    return TRUE;
}
