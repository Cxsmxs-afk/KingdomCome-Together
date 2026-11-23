// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using KcdMp.Wire;
using KCDMP_launcher.Models;

namespace KcdMp.Client.Tests;

public class AuthorityFailureUiTests
{
    [Theory]
    [InlineData("B decision_gate=unavailable story_gate=armed decision_reason=prologue_changed", HostAuthorityFailure.DecisionHook)]
    [InlineData("B decision_gate=armed story_gate=unavailable", HostAuthorityFailure.StoryHook)]
    [InlineData("B decision_gate=unavailable story_gate=unavailable", HostAuthorityFailure.BothHooks)]
    [InlineData("B paused=155 running=0", HostAuthorityFailure.NotConfirmed)]
    [InlineData("", HostAuthorityFailure.NotConfirmed)]
    [InlineData("B decision_gate=armed story_gate=armed", "")]
    public void Native_hook_readiness_keeps_the_underlying_failure(string status, string reason) =>
        Assert.Equal(reason, HostAuthorityFailure.FromStatus(status));

    [Theory]
    [InlineData("de", "Beitritt gestoppt", "Spielupdate", "NPC-KI")]
    [InlineData("de-DE", "Beitritt gestoppt", "Spielupdate", "NPC-KI")]
    [InlineData("en", "Joining stopped", "game update", "NPC AI")]
    [InlineData("fr", "Joining stopped", "game update", "NPC AI")]
    public void Launcher_uses_failure_reason_instead_of_generic_join_failed(string language, string start, string patch, string system)
    {
        string reason = HostAuthorityFailure.JoinReason(HostAuthorityFailure.DecisionHook);
        // This is the existing join-status JSON contract, as consumed by the launcher.
        var status = JsonSerializer.Deserialize<JoinStatusData>(JsonSerializer.Serialize(new
        {
            State = "failed", Message = "The join failed.", Reason = reason, CanFresh = true
        }))!;
        var banner = new AgentStatusBanner();
        banner.Apply(new ConnectionStatusData { State = "connected" }, status, true, 1, language);
        Assert.True(banner.AuthorityUnavailable);
        Assert.Equal(reason, banner.JoinReason);
        Assert.Contains(start, banner.JoinMessage);
        Assert.Contains(patch, banner.JoinMessage);
        Assert.Contains(system, banner.JoinMessage);
        Assert.Contains("Report a bug", banner.JoinMessage);
        Assert.False(banner.ShowFreshJoin); // a new character cannot fix a missing hook
        Assert.False(banner.ShowChoiceButtons);
        // A later successful join clears the error and its modal condition.
        banner.Apply(null, new JoinStatusData { State = "in", Message = "In the world." }, true, 2, language);
        Assert.False(banner.AuthorityUnavailable);
        Assert.Equal("In the world.", banner.JoinMessage);
    }

    [Theory]
    [InlineData("de", "nicht bestätigt", "erneut Beitreten")]
    [InlineData("en", "not confirmed", "Choose Join / Rejoin")]
    public void Missing_Dll_confirmation_does_not_invent_a_game_patch_failure(string language, string wording, string action)
    {
        string message = HostAuthorityFailure.Message(HostAuthorityFailure.NotConfirmed, language)!;
        Assert.Contains(wording, message);
        Assert.Contains(action, message);
        Assert.DoesNotContain("game update", message);
        Assert.DoesNotContain("Spielupdate", message);
    }

    [Fact]
    public void Post_load_leave_keeps_the_hook_failure_and_its_actionable_German_message()
    {
        var banner = new AgentStatusBanner();
        banner.Apply(null, new JoinStatusData
        {
            State = "left", Message = "Your host's world clock could not be verified.",
            Reason = HostAuthorityFailure.JoinReason(HostAuthorityFailure.BothHooks)
        }, true, 1, "de");
        Assert.True(banner.AuthorityUnavailable);
        Assert.Contains("NPC-KI und Story", banner.JoinMessage);
        Assert.DoesNotContain("clock", banner.JoinMessage);
        Assert.Equal("MOD-UPDATE ERFORDERLICH", HostAuthorityFailure.Title(banner.JoinReason, "de"));
        Assert.Equal("MULTIPLAYER NOT READY", HostAuthorityFailure.Title(HostAuthorityFailure.NotConfirmed, "en"));
    }

    [Fact]
    public void Unknown_join_errors_keep_the_agents_own_message()
    {
        var banner = new AgentStatusBanner();
        banner.Apply(null, new JoinStatusData { State = "failed", Message = "Disk full", Reason = "io" }, true, 1, "de");
        Assert.Equal("Disk full", banner.JoinMessage);
        Assert.False(banner.AuthorityUnavailable);
        Assert.Equal("frozen", HostAuthorityFailure.JoinReason("load-frozen"));
    }
}
