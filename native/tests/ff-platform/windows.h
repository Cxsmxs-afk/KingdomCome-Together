// Linux compatibility for the engine-free Friendly Fire fixture only.
#pragma once
#include <cstdint>
#include <cstddef>
#include <ctime>
#include <strings.h>

#define __fastcall
using DWORD = unsigned long;
using HMODULE = void*;
using LPCSTR = const char*;
struct LARGE_INTEGER { long long QuadPart; };
struct FILETIME { DWORD dwLowDateTime, dwHighDateTime; };
struct SYSTEMTIME { int wHour, wMinute, wSecond, wMilliseconds; };
constexpr unsigned MAX_PATH = 260, PAGE_READWRITE = 4;
constexpr unsigned GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 4;
constexpr unsigned GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 2;

uint64_t GetTickCount64();
inline void QueryPerformanceCounter(LARGE_INTEGER* out) { out->QuadPart = GetTickCount64(); }
inline void QueryPerformanceFrequency(LARGE_INTEGER* out) { out->QuadPart = 1000; }
inline void GetLocalTime(SYSTEMTIME* out) {
    const auto now = std::time(nullptr);
    std::tm t{};
    localtime_r(&now, &t);
    *out = {t.tm_hour, t.tm_min, t.tm_sec, 0};
}
inline HMODULE GetModuleHandleA(LPCSTR) { return nullptr; }
inline bool VirtualProtect(void*, std::size_t, DWORD, DWORD*) { return false; }
inline void InterlockedExchangePointer(void**, void*) {}
inline bool GetModuleHandleExA(unsigned, LPCSTR, HMODULE*) { return false; }
inline DWORD GetModuleFileNameA(HMODULE, char*, DWORD) { return 0; }
inline DWORD GetCurrentDirectoryA(DWORD, char*) { return 0; }
inline bool GetProcessTimes(void*, FILETIME*, FILETIME*, FILETIME*, FILETIME*) { return false; }
inline void* GetCurrentProcess() { return nullptr; }
inline DWORD GetCurrentProcessId() { return 1; }
inline DWORD GetCurrentThreadId() { return 1; }
inline int _stricmp(const char* a, const char* b) { return strcasecmp(a, b); }
inline int _dupenv_s(char** out, std::size_t* len, const char*) { *out = nullptr; *len = 0; return 0; }
