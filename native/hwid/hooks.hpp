#pragma once
 
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <windows.h>
#include <wininet.h>
#include <winioctl.h>
#include <iphlpapi.h>
#include <setupapi.h> 
#include <wbemidl.h> 
#include <vector>
#include <string>
#include <random>
#include <time.h>
#include <comdef.h>
#include <sstream>
#include <iomanip>
#include <intrin.h>
#include <lm.h>
 
 
#pragma intrinsic(_ReturnAddress)
#pragma comment(lib, "Netapi32.lib")
#pragma comment(lib, "IPHLPAPI.lib")
#pragma comment(lib, "Setupapi.lib")
#pragma comment(lib, "wbemuuid.lib")
#pragma comment(lib, "Wininet.lib")
 
#include "minhook\minhook.h"
#include "Utils.hpp"
 
#define IWbemClassObject_Get_Index 4
#define IEnumWbemClassObject_Next_Index 4
 
class Spoofer {
public:
    static bool Initialize() {
        std::srand(static_cast<unsigned int>(std::time(nullptr)));
        m_fakeMAC = GenerateRandomMAC();
        m_fakeDiskSerial = GenerateRandomSerial(20);
        m_fakeGuid = GenerateRandomGUID();
        m_fakeVolumeSerial = static_cast<DWORD>(std::rand() | (std::rand() << 16));
        m_fakeComputerName = L"DESKTOP-" + Utils::s2ws(GenerateRandomSerial(8));
        m_fakeBaseboard = Utils::s2ws(GenerateRandomSerial(14));
        m_fakeGPUName = L"NVIDIA GeForce RTX " + std::to_wstring(1070 + (std::rand() % 20));
 
        CreateHook(L"kernel32", "GetSystemInfo", &DetourGetSystemInfo, (LPVOID*)&fpGetSystemInfo);
        CreateHook(L"kernel32", "GetVolumeInformationW", &DetourGetVolumeInformationW, (LPVOID*)&fpGetVolumeInformationW);
        CreateHook(L"kernel32", "GetVolumeInformationByHandleW", &DetourGetVolumeInformationByHandleW, (LPVOID*)&fpGetVolumeInformationByHandleW);
        CreateHook(L"kernel32", "GetComputerNameW", &DetourGetComputerNameW, (LPVOID*)&fpGetComputerNameW);
        CreateHook(L"advapi32", "RegQueryValueExW", &DetourRegQueryValueExW, (LPVOID*)&fpRegQueryValueExW);
        CreateHook(L"kernel32", "GetSystemFirmwareTable", &DetourGetSystemFirmwareTable, (LPVOID*)&fpGetSystemFirmwareTable);
        CreateHook(L"iphlpapi", "GetAdaptersAddresses", &DetourGetAdaptersAddresses, (LPVOID*)&fpGetAdaptersAddresses);
        CreateHook(L"iphlpapi", "GetAdaptersInfo", &DetourGetAdaptersInfo, (LPVOID*)&fpGetAdaptersInfo);
        CreateHook(L"netapi32", "NetWkstaGetInfo", &DetourNetWkstaGetInfo, (LPVOID*)&fpNetWkstaGetInfo);
        CreateHook(L"advapi32", "GetTokenInformation", &DetourGetTokenInformation, (LPVOID*)&fpGetTokenInformation);
 
        HMODULE hSetup = GetModuleHandleW(L"setupapi.dll");
        if (hSetup) {
            LPVOID pFunc = GetProcAddress(hSetup, "SetupDiGetDeviceRegistryPropertyW");
            if (pFunc) MH_CreateHook(pFunc, &DetourSetupDiGetDeviceRegistryPropertyW, (LPVOID*)&fpSetupDiGetDeviceRegistryPropertyW);
        }
        CreateHook(L"user32", "EnumDisplayDevicesW", &DetourEnumDisplayDevicesW, (LPVOID*)&fpEnumDisplayDevicesW);
 
        ApplyWMIHooks();
        return MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
    }
 
private:
    inline static std::vector<BYTE> m_fakeMAC;
    inline static std::string m_fakeDiskSerial;
    inline static std::wstring m_fakeGuid;
    inline static DWORD m_fakeVolumeSerial;
    inline static std::wstring m_fakeComputerName;
    inline static std::wstring m_fakeGPUName;
    inline static std::wstring m_fakeBaseboard;
 
    // 指针定义
    typedef NET_API_STATUS(NET_API_FUNCTION* NETWKSTAGETINFO)(LMSTR, DWORD, LPBYTE*);
    typedef HRESULT(WINAPI* IWbemClassObject_Get)(IWbemClassObject*, LPCWSTR, LONG, VARIANT*, CIMTYPE*, LONG*);
    typedef HRESULT(WINAPI* IEnumWbemClassObject_Next)(IEnumWbemClassObject*, LONG, ULONG, IWbemClassObject**, ULONG*);
    typedef ULONG(WINAPI* GETADAPTERSADDRESSES)(ULONG, ULONG, PVOID, PIP_ADAPTER_ADDRESSES, PULONG);
    typedef DWORD(WINAPI* GETADAPTERSINFO)(PIP_ADAPTER_INFO, PULONG);
    typedef LSTATUS(WINAPI* REGQUERYVALUEEXW)(HKEY, LPCWSTR, LPDWORD, LPDWORD, LPBYTE, LPDWORD);
    typedef BOOL(WINAPI* GETVOLINFOW)(LPCWSTR, LPWSTR, DWORD, LPDWORD, LPDWORD, LPDWORD, LPWSTR, DWORD);
    typedef BOOL(WINAPI* GETVOLBYHANDLEW)(HANDLE, LPWSTR, DWORD, LPDWORD, LPDWORD, LPDWORD, LPWSTR, DWORD);
    typedef BOOL(WINAPI* GETCOMPNAMEW)(LPWSTR, LPDWORD);
    typedef UINT(WINAPI* GETSYSTEMFIRMWARE)(DWORD, DWORD, PVOID, DWORD);
    typedef BOOL(WINAPI* ENUMDISPLAYDEVICESW)(LPCWSTR, DWORD, PDISPLAY_DEVICEW, DWORD);
    typedef BOOL(WINAPI* SETUPDIGETDEVICEREG)(HDEVINFO, PSP_DEVINFO_DATA, DWORD, PDWORD, PBYTE, DWORD, PDWORD);
    typedef void(WINAPI* GETSYSTEMINFO)(LPSYSTEM_INFO);
    typedef BOOL(WINAPI* GETTOKENINFO)(HANDLE, TOKEN_INFORMATION_CLASS, LPVOID, DWORD, PDWORD);
    typedef HANDLE(WINAPI* CREATEFILEW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
 
    inline static GETTOKENINFO fpGetTokenInformation = nullptr;
    inline static IWbemClassObject_Get fpWbemClassObjectGet = nullptr;
    inline static IEnumWbemClassObject_Next fpEnumWbemClassObjectNext = nullptr;
    inline static GETADAPTERSADDRESSES fpGetAdaptersAddresses = nullptr;
    inline static GETADAPTERSINFO fpGetAdaptersInfo = nullptr;
    inline static REGQUERYVALUEEXW fpRegQueryValueExW = nullptr;
    inline static GETVOLINFOW fpGetVolumeInformationW = nullptr;
    inline static GETVOLBYHANDLEW fpGetVolumeInformationByHandleW = nullptr;
    inline static GETCOMPNAMEW fpGetComputerNameW = nullptr;
    inline static GETSYSTEMFIRMWARE fpGetSystemFirmwareTable = nullptr;
    inline static ENUMDISPLAYDEVICESW fpEnumDisplayDevicesW = nullptr;
    inline static SETUPDIGETDEVICEREG fpSetupDiGetDeviceRegistryPropertyW = nullptr;
    inline static GETSYSTEMINFO fpGetSystemInfo = nullptr;
    inline static NETWKSTAGETINFO fpNetWkstaGetInfo = nullptr;
    // --- Detour 实现 ---
    static BOOL WINAPI DetourGetTokenInformation(HANDLE hToken, TOKEN_INFORMATION_CLASS tic, LPVOID pInfo, DWORD len, PDWORD rLen) {
        BOOL res = fpGetTokenInformation(hToken, tic, pInfo, len, rLen);
 
        // TokenUser 包含了用户的 SID
        if (res && tic == TokenUser && pInfo) {
            PTOKEN_USER pTokenUser = (PTOKEN_USER)pInfo;
            if (IsValidSid(pTokenUser->User.Sid)) {
                PUCHAR count = GetSidSubAuthorityCount(pTokenUser->User.Sid);
                if (count && *count > 0) {
                    DWORD* pLastSub = GetSidSubAuthority(pTokenUser->User.Sid, *count - 1);
                    *pLastSub += (rand() % 1000) + 500; // 随机偏移偏移量
                }
            }
        }
        return res;
    }
 
    static NET_API_STATUS NET_API_FUNCTION DetourNetWkstaGetInfo(LMSTR servername, DWORD level, LPBYTE* bufptr) {
        NET_API_STATUS res = fpNetWkstaGetInfo(servername, level, bufptr);
        if (res == NERR_Success && level == 100) {
            WKSTA_INFO_100* info = (WKSTA_INFO_100*)*bufptr;
            std::wstring fakeWG = L"WORKGROUP" + std::to_wstring(rand() % 10);
        }
        return res;
    }
 
    static void WINAPI DetourGetSystemInfo(LPSYSTEM_INFO lpSystemInfo) {
        fpGetSystemInfo(lpSystemInfo);
        lpSystemInfo->dwNumberOfProcessors = (rand() % 2 == 0) ? 8 : 12;
    }
 
    static UINT WINAPI DetourGetSystemFirmwareTable(DWORD sig, DWORD tid, PVOID buf, DWORD s) {
        UINT res = fpGetSystemFirmwareTable(sig, tid, buf, s);
        if (res > 0 && buf && sig == 'RSMB') {
            BYTE* data = (BYTE*)buf;
            for (UINT pos = 0; pos < res - 2; ) {
                BYTE type = data[pos]; BYTE len = data[pos + 1];
                if (pos + len > res) break;
                if (type == 1 || type == 2) {
                    char* strPool = (char*)(data + pos + len);
                    strncpy_s(strPool, 64, Utils::ws2s(m_fakeBaseboard).c_str(), _TRUNCATE);
                }
                pos += len;
                while (pos < res - 1 && !(data[pos] == 0 && data[pos + 1] == 0)) pos++;
                pos += 2;
            }
        }
        return res;
    }
 
    static HRESULT WINAPI DetourWbemClassObjectGet(IWbemClassObject* pThis, LPCWSTR wszName, LONG lFlags, VARIANT* pVal, CIMTYPE* pType, LONG* plFlavor) {
        HRESULT hr = fpWbemClassObjectGet(pThis, wszName, lFlags, pVal, pType, plFlavor);
        if (SUCCEEDED(hr) && wszName && pVal) {
            if (_wcsicmp(wszName, L"SerialNumber") == 0 || _wcsicmp(wszName, L"UUID") == 0) {
                VariantClear(pVal); pVal->vt = VT_BSTR;
                pVal->bstrVal = SysAllocString(m_fakeBaseboard.c_str());
            }
        }
        return hr;
    }
 
    static HRESULT WINAPI DetourEnumWbemClassObjectNext(IEnumWbemClassObject* pThis, LONG lTimeout, ULONG uCount, IWbemClassObject** apObjects, ULONG* puReturned) {
        HRESULT hr = fpEnumWbemClassObjectNext(pThis, lTimeout, uCount, apObjects, puReturned);
        if (SUCCEEDED(hr) && puReturned && *puReturned > 0) {
            for (ULONG i = 0; i < *puReturned; i++) {
                void** vtable = *(void***)apObjects[i];
                if (vtable[IWbemClassObject_Get_Index] != DetourWbemClassObjectGet) {
                    if (fpWbemClassObjectGet == nullptr) {
                        MH_CreateHook(vtable[IWbemClassObject_Get_Index], &DetourWbemClassObjectGet, (LPVOID*)&fpWbemClassObjectGet);
                        MH_EnableHook(vtable[IWbemClassObject_Get_Index]);
                    }
                }
            }
        }
        return hr;
    }
 
    static LSTATUS WINAPI DetourRegQueryValueExW(HKEY h, LPCWSTR name, LPDWORD r, LPDWORD t, LPBYTE data, LPDWORD cb) {
        if (name) {
            if (wcsstr(name, L"MachineGuid")) {
                if (data && cb) {
                    std::wstring fakeGuid = GenerateRandomGUID();
                    wcscpy_s((wchar_t*)data, *cb / 2, fakeGuid.c_str());
                    return ERROR_SUCCESS;
                }
            }
            if (wcsstr(name, L"ProductId")) {
                if (data && cb) {
                    std::wstring fakePID = L"00330-80000-00000-AA" + Utils::s2ws(GenerateRandomSerial(3));
                    wcscpy_s((wchar_t*)data, *cb / 2, fakePID.c_str());
                    return ERROR_SUCCESS;
                }
            }
        }
        return fpRegQueryValueExW(h, name, r, t, data, cb);
    }
 
    static ULONG WINAPI DetourGetAdaptersAddresses(ULONG f, ULONG fl, PVOID r, PIP_ADAPTER_ADDRESSES p, PULONG l) {
        ULONG ret = fpGetAdaptersAddresses(f, fl, r, p, l);
        if (ret == ERROR_SUCCESS && p) {
            for (auto c = p; c; c = c->Next) {
                if (c->PhysicalAddressLength == 6) memcpy(c->PhysicalAddress, m_fakeMAC.data(), 6);
            }
        }
        return ret;
    }
 
    static BOOL WINAPI DetourGetVolumeInformationW(LPCWSTR r, LPWSTR n, DWORD ns, LPDWORD s, LPDWORD ml, LPDWORD f, LPWSTR sn, DWORD ss) {
        BOOL res = fpGetVolumeInformationW(r, n, ns, s, ml, f, sn, ss);
        if (res && s) {
            *s = m_fakeVolumeSerial;
        }
        return res;
    }
 
    static BOOL WINAPI DetourGetVolumeInformationByHandleW(HANDLE h, LPWSTR n, DWORD ns, LPDWORD s, LPDWORD ml, LPDWORD f, LPWSTR sn, DWORD ss) {
        BOOL res = fpGetVolumeInformationByHandleW(h, n, ns, s, ml, f, sn, ss);
        if (res && s) *s = m_fakeVolumeSerial;
        return res;
    }
 
    static BOOL WINAPI DetourGetComputerNameW(LPWSTR n, LPDWORD s) {
        wcsncpy_s(n, *s, m_fakeComputerName.c_str(), _TRUNCATE);
        *s = (DWORD)m_fakeComputerName.length();
        return TRUE;
    }
 
    static DWORD WINAPI DetourGetAdaptersInfo(PIP_ADAPTER_INFO p, PULONG l) {
        DWORD res = fpGetAdaptersInfo(p, l);
        if (res == ERROR_SUCCESS && p) {
            for (auto c = p; c; c = c->Next) memcpy(c->Address, m_fakeMAC.data(), 6);
        }
        return res;
    }
 
    static BOOL WINAPI DetourEnumDisplayDevicesW(LPCWSTR lp, DWORD i, PDISPLAY_DEVICEW d, DWORD f) {
        BOOL res = fpEnumDisplayDevicesW(lp, i, d, f);
        if (res && d && i == 0) {
            wcsncpy_s(d->DeviceString, 128, m_fakeGPUName.c_str(), _TRUNCATE);
        }
        return res;
    }
 
    static BOOL WINAPI DetourSetupDiGetDeviceRegistryPropertyW(HDEVINFO h, PSP_DEVINFO_DATA d, DWORD p, PDWORD r, PBYTE b, DWORD s, PDWORD req) {
        BOOL res = fpSetupDiGetDeviceRegistryPropertyW(h, d, p, r, b, s, req);
        if (res && b && (p == SPDRP_DEVICEDESC || p == SPDRP_FRIENDLYNAME)) {
            if (wcsstr((wchar_t*)b, L"NVIDIA") || wcsstr((wchar_t*)b, L"Intel"))
                wcsncpy_s((wchar_t*)b, s / sizeof(wchar_t), m_fakeGPUName.c_str(), _TRUNCATE);
        }
        return res;
    }
 
    static void ApplyWMIHooks() {
        IWbemLocator* pLoc = nullptr;
        if (SUCCEEDED(CoCreateInstance(CLSID_WbemLocator, 0, CLSCTX_INPROC_SERVER, IID_IWbemLocator, (LPVOID*)&pLoc))) {
            IWbemServices* pSvc = nullptr;
            if (SUCCEEDED(pLoc->ConnectServer(_bstr_t(L"ROOT\\CIMV2"), NULL, NULL, 0, NULL, 0, 0, &pSvc))) {
                IEnumWbemClassObject* pEnumerator = nullptr;
                if (SUCCEEDED(pSvc->ExecQuery(_bstr_t("WQL"), _bstr_t("SELECT * FROM Win32_DiskDrive"),
                    WBEM_FLAG_FORWARD_ONLY, NULL, &pEnumerator))) {
                    void** vtable = *(void***)pEnumerator;
                    MH_CreateHook(vtable[IEnumWbemClassObject_Next_Index], &DetourEnumWbemClassObjectNext, (LPVOID*)&fpEnumWbemClassObjectNext);
                    MH_EnableHook(vtable[IEnumWbemClassObject_Next_Index]);
                    pEnumerator->Release();
                }
                pSvc->Release();
            }
            pLoc->Release();
        }
    }
 
    static bool CreateHook(LPCWSTR modName, LPCSTR procName, LPVOID detour, LPVOID* orig) {
        HMODULE hMod = GetModuleHandleW(modName);
        if (!hMod) hMod = GetModuleHandleW(L"kernelbase.dll");
        if (!hMod) hMod = GetModuleHandleW(L"ntdll.dll");
 
        if (hMod) {
            LPVOID pFunc = (LPVOID)GetProcAddress(hMod, procName);
            if (pFunc) {
                MH_STATUS status = MH_CreateHook(pFunc, detour, orig);
                if (status == MH_OK) return true;
            }
        }
 
        return MH_CreateHookApi(modName, procName, detour, orig) == MH_OK;
    }
 
    static std::string GenerateRandomSerial(int len) {
        const char charset[] = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        std::string res;
        for (int i = 0; i < len; i++) res += charset[std::rand() % (sizeof(charset) - 1)];
        return res;
    }
 
    static std::vector<BYTE> GenerateRandomMAC() {
        std::vector<BYTE> mac(6);
        for (int i = 0; i < 6; i++) mac[i] = (BYTE)(std::rand() % 256);
        mac[0] &= 0xFE; return mac;
    }
 
    static std::wstring GenerateRandomGUID() {
        wchar_t b[40];
        swprintf(b, 40, L"{%08X-%04X-%04X-%04X-%012llX}",
            rand(), rand() % 0xFFFF, rand() % 0xFFFF, rand() % 0xFFFF, (unsigned long long)rand() << 32 | rand());
        return b;
    }
};