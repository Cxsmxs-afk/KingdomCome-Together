# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
# WO-129: engine-free native unit tests (no game). Built by Build-Native.ps1,
# run by tools\Build-Installer.ps1 after the native build.
add_executable(KCDMP_NativeTests wo129_gait_tests.cpp walk_holder_tests.cpp pose_ring_tests.cpp wo114_wake_tests.cpp wo133_port_gate_tests.cpp wo136_rules_tests.cpp wo137_rules_tests.cpp wo138_rules_tests.cpp wo139_rules_tests.cpp wo140_rules_tests.cpp wo141_rules_tests.cpp wo143_rules_tests.cpp wo148_x64_tests.cpp wo151_tests.cpp wo155_rules_tests.cpp wo163_tests.cpp wo165_tests.cpp wo166_tests.cpp wo167_tests.cpp wo170_tests.cpp wo172_tests.cpp wo171_tests.cpp wo173_tests.cpp wo174_tests.cpp wo176_tests.cpp wo177_tests.cpp wo178_tests.cpp wo187_tests.cpp wo190_tests.cpp wo194_tests.cpp wo195_tests.cpp wo197_tests.cpp wo200_tests.cpp wo201_tests.cpp wo202_tests.cpp wo203_tests.cpp wo203_gate_tests.cpp wo203_conv_tests.cpp wo203_defence_tests.cpp wo203_stream_tests.cpp wo203_appear_tests.cpp wo203_quest_tests.cpp wo203_join_tests.cpp wo203_cam_tests.cpp wo204_tests.cpp wo206_tests.cpp wo208_tests.cpp wo209_tests.cpp wo210_tests.cpp wo211_tests.cpp wo213_tests.cpp wo214_tests.cpp wo215_tests.cpp wo216_tests.cpp p4_rules_review_tests.cpp p5_corpse_tests.cpp p5_marker_tests.cpp p6_rules_tests.cpp p6_missile_flow_tests.cpp p6_capture_flow_tests.cpp p6_render_fence_tests.cpp ../KCDMP/p6_missile.cpp ../KCDMP/wo201_ranged.cpp t3_native_stats_tests.cpp ../KCDMP/p5_marker.cpp p4_flow_tests.cpp ../KCDMP/p4.cpp wo217_tests.cpp wo218_tests.cpp ../KCDMP/fault_guard.cpp)
target_include_directories(KCDMP_NativeTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../KCDMP)
target_sources(KCDMP_NativeTests PRIVATE rp_measure_tests.cpp holder_contract_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE edge_lanes_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE rp_timeline_tests.cpp rp_timeline_action_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE rp_controller_tests.cpp motion_remote_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE rp_combat_tests.cpp rp_body_action_tests.cpp load_sequence_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE edge_console_tests.cpp visual_damage_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE t3_recovery_stats_tests.cpp)
target_sources(KCDMP_NativeTests PRIVATE h6_combat_action_tests.cpp)
target_compile_definitions(KCDMP_NativeTests PRIVATE KCDMP_P4_TEST KCDMP_P5_TEST KCDMP_P6_TEST KCDMP_P6_CAPTURE_TEST)
