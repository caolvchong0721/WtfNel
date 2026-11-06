#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <string>

// 字符串转换工具类：std::string <-> std::wstring（按 UTF-8 编码互转）
class Utils {
public:
    // std::string (UTF-8) -> std::wstring
    static std::wstring s2ws(const std::string& str) {
        if (str.empty()) return std::wstring();
        int len = MultiByteToWideChar(CP_UTF8, 0, str.c_str(), (int)str.size(), nullptr, 0);
        if (len <= 0) return std::wstring();
        std::wstring result(len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, str.c_str(), (int)str.size(), &result[0], len);
        return result;
    }

    // std::wstring -> std::string (UTF-8)
    static std::string ws2s(const std::wstring& wstr) {
        if (wstr.empty()) return std::string();
        int len = WideCharToMultiByte(CP_UTF8, 0, wstr.c_str(), (int)wstr.size(), nullptr, 0, nullptr, nullptr);
        if (len <= 0) return std::string();
        std::string result(len, '\0');
        WideCharToMultiByte(CP_UTF8, 0, wstr.c_str(), (int)wstr.size(), &result[0], len, nullptr, nullptr);
        return result;
    }
};
