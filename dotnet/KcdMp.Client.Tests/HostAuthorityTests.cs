// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using Xunit;
using KcdMp.Wire;
using KCDMP_launcher.Models;

namespace KcdMp.Client.Tests;

public class HostAuthorityTests
{
    [Theory]
    [InlineData("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(true,false) end")]
    [InlineData("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(false,false) end")]
    public void Authority_bootstrap_and_reset_reach_the_mod_at_the_menu(string lua) =>
        Assert.True(GameBridge.IsMenuSafeLua(lua));

    [Theory]
    [InlineData("B decision_gate=armed story_gate=armed role=2", true)]
    [InlineData("B decision_gate=unavailable story_gate=armed", false)]
    [InlineData("B decision_gate=armed story_gate=unavailable", false)]
    [InlineData("B paused=155 running=0", false)]
    [InlineData("B decision_gate=armed_later story_gate=armed", false)]
    public void Both_current_native_gates_are_required_before_load(string status, bool expected) =>
        Assert.Equal(expected, HostAuthorityReadiness.Armed(status));

    [Theory]
    [InlineData("B decision_gate=unavailable story_gate=armed", HostAuthorityFailure.DecisionHook)]
    [InlineData("B decision_gate=armed story_gate=unavailable", HostAuthorityFailure.StoryHook)]
    [InlineData("B decision_gate=unavailable story_gate=unavailable", HostAuthorityFailure.BothHooks)]
    public async Task Missing_hook_reaches_both_launcher_languages_before_native_role_rejection(string status, string reason)
    {
        var native = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int roleCalls = 0;
        var pending = HostAuthorityReadiness.ProbeAndConfigureAsync(() => native.Task, () =>
        {
            roleCalls++;
            return Task.FromResult(false); // op160 would refuse role 2 while a hook is absent
        });
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, roleCalls);
        native.SetResult(status);
        var prepared = await pending;
        Assert.Equal(reason, prepared.FailureReason);
        Assert.Equal(status, prepared.NativeStatus);
        Assert.Equal(0, roleCalls);
        foreach (string language in new[] { "de", "en" })
        {
            var banner = new AgentStatusBanner();
            banner.Apply(null, new JoinStatusData { State = "failed", Message = "The join failed.", Reason = prepared.FailureReason }, true, 1, language);
            Assert.True(banner.AuthorityUnavailable);
            Assert.Equal(HostAuthorityFailure.Message(reason, language), banner.JoinMessage);
            Assert.Contains(language == "de" ? "Spielupdate" : "game update", banner.JoinMessage);
        }
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, HostAuthorityFailure.NotConfirmed)]
    public async Task Armed_probe_configures_role_once_and_preserves_an_unconfirmed_configuration(bool configured, string expected)
    {
        var order = new List<string>();
        var prepared = await HostAuthorityReadiness.ProbeAndConfigureAsync(() =>
        {
            order.Add("probe");
            return Task.FromResult<string?>("B decision_gate=armed story_gate=armed");
        }, () =>
        {
            order.Add("configure");
            return Task.FromResult(configured);
        });
        Assert.Equal(new[] { "probe", "configure" }, order);
        Assert.Equal(expected, prepared.FailureReason);
    }

    [Fact]
    public async Task Unreadable_readiness_does_not_configure_an_unverified_role()
    {
        int roleCalls = 0;
        var prepared = await HostAuthorityReadiness.ProbeAndConfigureAsync(() => Task.FromResult<string?>(null), () =>
        {
            roleCalls++;
            return Task.FromResult(true);
        });
        Assert.Equal(HostAuthorityFailure.NotConfirmed, prepared.FailureReason);
        Assert.Equal(0, roleCalls);
    }

    [Fact]
    public async Task Abandoned_native_probe_cannot_later_configure_a_role()
    {
        const string armed = "B decision_gate=armed story_gate=armed";
        var native = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<Task<HostAuthorityReadiness.Preparation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualFrameTime();
        int roleCalls = 0;
        var frame = ProcessorFrameScope.RunAsync(async token =>
        {
            var preparation = HostAuthorityReadiness.ProbeAndConfigureAsync(() => native.Task,
                () => { roleCalls++; return Task.FromResult(true); }, token);
            entered.SetResult(preparation);
            await preparation;
        }, 50, CancellationToken.None, clock);
        try
        {
            var pending = await entered.Task.WaitAsync(TestWatchdog);
            Assert.False(pending.IsCompleted);
            clock.Expire();
            var result = await frame.WaitAsync(TestWatchdog);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
            native.SetResult(armed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Equal(0, roleCalls);
        }
        finally
        {
            clock.Expire();
            native.TrySetResult(armed);
        }
    }

    [Fact]
    public async Task Abandoned_native_configuration_cannot_report_late_success()
    {
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<Task<HostAuthorityReadiness.Preparation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualFrameTime();
        int confirmed = 0;
        var frame = ProcessorFrameScope.RunAsync(async token =>
        {
            var preparation = HostAuthorityReadiness.ProbeAndConfigureAsync(
                () => Task.FromResult<string?>("B decision_gate=armed story_gate=armed"), () => native.Task, token);
            entered.SetResult(preparation);
            await preparation;
            confirmed++;
        }, 50, CancellationToken.None, clock);
        try
        {
            var pending = await entered.Task.WaitAsync(TestWatchdog);
            Assert.False(pending.IsCompleted);
            clock.Expire();
            var result = await frame.WaitAsync(TestWatchdog);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
            native.SetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Equal(0, confirmed);
        }
        finally
        {
            clock.Expire();
            native.TrySetResult(true);
        }
    }

    private static readonly TimeSpan TestWatchdog = TimeSpan.FromSeconds(5);
}
