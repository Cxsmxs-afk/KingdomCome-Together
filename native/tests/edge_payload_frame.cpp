// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// The production hook expects WHGame to import this method from Framework.
namespace wh::framework {
#ifdef KCDMP_EDGE_FRAME_PROVIDER
class C_ModulesManager {
public:
    __declspec(dllexport) void Update(float dt);
};
void C_ModulesManager::Update(float) {}
#else
class C_ModulesManager {
public:
    __declspec(dllimport) void Update(float dt);
};
#endif
}

#ifndef KCDMP_EDGE_FRAME_PROVIDER
extern "C" __declspec(dllexport) void PulseKctSmokeFrame() {
    static wh::framework::C_ModulesManager manager;
    manager.Update(1.0f / 60.0f);
}
#endif
