// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

public partial class GameBridge
{
    /// <summary>&lt;Saved Games&gt;\KingdomCome2 (the game's user folder), whether or not it exists.</summary>
    internal static string Kcd2UserFolder()
    {
        string? root = null;
        try
        {
            if (SHGetKnownFolderPath(FolderIdSavedGames, 0, IntPtr.Zero, out var p) == 0)
            {
                root = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
            }
        }
        catch (Exception caughtFailure) { Faults.ReportThrottled("prologue-saves-folder", caughtFailure); }
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        return Path.Combine(root, "KingdomCome2");
    }

}
