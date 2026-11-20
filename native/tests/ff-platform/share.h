// The fixture process owns its temporary native log.
#pragma once
#include <cstdio>
constexpr int _SH_DENYWR = 0;
inline FILE* _fsopen(const char* path, const char* mode, int) { return std::fopen(path, mode); }
