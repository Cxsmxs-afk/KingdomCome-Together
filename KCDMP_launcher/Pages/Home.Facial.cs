// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KcdMp.Setup;
using Serilog;

namespace KCDMP_launcher.Pages;

public partial class Home
{
    private static async Task<bool> PrepareFacialFallbackAsync(string gameRoot)
    {
        try
        {
            // Assets are consumed at game startup. Never rewrite an override while any game is alive.
            bool running = false;
            foreach (var process in Process.GetProcessesByName("KingdomCome"))
                using (process) running |= !process.HasExited;
            var r = await Task.Run(() => FacialFallback.Prepare(gameRoot, Path.Combine(gameRoot, "Mods", "kdcmp"), running));
            // One bounded summary per launch, regardless of the number of repeated engine warnings.
            if (r.Ok) Log.Information("MP-FIX FACE facial fallback: {Action}, {Databases} databases, {References} invalid clips ({Detail})", r.Action, r.Databases, r.References, r.Detail);
            else Log.Warning("MP-FIX FACE facial fallback: {Action} ({Detail})", r.Action, r.Detail);
            return !r.BlocksLaunch;
        }
        catch (Exception ex)
        {
            Log.Warning("facial fallback: preparation failed ({Reason}); this launch has no certified new facial fallback", ex.GetType().Name);
            return !File.Exists(Path.Combine(gameRoot, "Mods", "kdcmp", "Data", FacialFallback.PakName));
        }
    }
}
