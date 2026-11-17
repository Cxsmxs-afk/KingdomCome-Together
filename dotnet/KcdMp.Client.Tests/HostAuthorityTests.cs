using Xunit;

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
}
