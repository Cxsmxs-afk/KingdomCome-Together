// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using Xunit;

namespace KcdMp.Client.Tests;

public sealed class T3GameplayReadyHistoryTests
{
    private const string Startup = "Log Started at Wed Nov 18 03:00:00 2026";
    private static bool Read(string text) => GameplayReadyHistory.Read(new StringReader(text));

    [Fact]
    public void Attach_requires_a_real_gameplay_edge_in_the_identified_log_process()
    {
        Assert.False(Read(""));
        Assert.False(Read("Gameplay started\n"));
        Assert.False(Read(Startup + "\n"));
        Assert.True(Read(Startup + "\nGameplay started\n"));
        Assert.True(Read(Startup + "\nGameplay started\nordinary later engine log\n"));
    }

    [Theory]
    [InlineData("Loading saved game 'playline0/quicksave.whs'")]
    [InlineData("[CryAction] LoadGame: 'playline0/quicksave.whs'")]
    [InlineData("PlayVideoOnly 'main_menu'")]
    [InlineData("PlayVideoOnly 'main_menu_intro'")]
    [InlineData("CSystem::Quit invoked from quit command")]
    [InlineData("Exiting to main menu because save game loading failed")]
    public void A_later_load_menu_or_quit_revokes_old_gameplay_readiness(string edge)
    {
        string prefix = Startup + "\nGameplay started\n";
        Assert.False(Read(prefix + edge + "\n"));
        Assert.False(Read(prefix + edge + "\nordinary append while waiting\n"));
        // No amount of append/attach makes an old Gameplay edge current again.
        Assert.False(Read(prefix + edge + "\n[KCD2-MP] Gameplay started\n"));
    }

    [Theory]
    [InlineData("Loading saved game 'another.whs'")]
    [InlineData("[CryAction] LoadGame: 'another.whs'")]
    [InlineData("PlayVideoOnly 'main_menu'")]
    public void Only_a_new_real_gameplay_after_the_transition_restores_readiness(string edge)
    {
        Assert.True(Read(Startup + "\nGameplay started\n" + edge + "\nGameplay started\n"));
    }

    [Theory]
    [InlineData("BackupNameAttachment=2026-11-18")]
    [InlineData("Connecting messages from trace server...")]
    public void A_startup_append_fences_the_old_process_until_its_own_header_and_gameplay(string marker)
    {
        string previous = Startup + "\nGameplay started\n";
        Assert.False(Read(previous + marker + "\n"));
        Assert.False(Read(previous + marker + "\nGameplay started\n"));
        Assert.False(Read(previous + marker + "\n" + Startup + "\n"));
        Assert.True(Read(previous + marker + "\n" + Startup + "\nGameplay started\n"));
    }

    [Fact]
    public void A_restarted_log_process_never_inherits_the_previous_gameplay()
    {
        string previous = Startup + "\nGameplay started\n";
        Assert.False(Read(previous + "Log Started at Wed Nov 18 04:00:00 2026\n"));
        Assert.False(Read(previous + "Log Started at Wed Nov 18 04:00:00 2026\nordinary startup append\n"));
        Assert.True(Read(previous + "Log Started at Wed Nov 18 04:00:00 2026\nGameplay started\n"));
    }

    [Theory]
    [InlineData("[KCD2-MP] Gameplay started")]
    [InlineData("[KCD2-MP-EVT] v1 12 Gameplay started")]
    [InlineData("[KCD2-MP] engine quote: 'Gameplay started'")]
    [InlineData("not Gameplay started")]
    [InlineData("Gameplay started later")]
    [InlineData(" Gameplay started")]
    public void A_foreign_or_quoted_gameplay_line_never_establishes_readiness(string line)
    {
        Assert.False(Read(Startup + "\n" + line + "\n"));
    }

    [Theory]
    [InlineData("[KCD2-MP] Log Started at quoted date")]
    [InlineData("[KCD2-MP] BackupNameAttachment=quoted")]
    [InlineData("[KCD2-MP] Connecting messages from trace server...")]
    [InlineData("[KCD2-MP] Loading saved game 'quoted.whs'")]
    [InlineData("[KCD2-MP] CSystem::Quit invoked")]
    public void A_mod_quote_cannot_replace_the_actual_engine_process_or_load_state(string line)
    {
        Assert.False(Read(line + "\nGameplay started\n"));
        Assert.True(Read(Startup + "\nGameplay started\n" + line + "\n"));
    }

    [Fact]
    public void CrLf_and_missing_final_newline_preserve_the_same_history_contract()
    {
        Assert.True(Read(Startup + "\r\nGameplay started"));
        Assert.False(Read(Startup + "\r\nGameplay started\r\n[ CryAction ] no relevant edge\r\nLoading saved game 'x.whs'"));
        Assert.True(Read("BackupNameAttachment=a\r\nConnecting messages from trace server...\r\n" + Startup + "\r\nGameplay started"));
    }
}
