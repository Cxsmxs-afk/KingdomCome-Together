// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Net;
using System.Text.Json;
using KCDMP_launcher.Models;
using MoonSharp.Interpreter;
using Choice = KCDMP_launcher.Models.MenuTakeoverRule.MenuChoice;

namespace KcdMp.Client.Tests;

public sealed class H22LauncherSessionRoleTests
{
    [Fact]
    public async Task Direct_diagnostic_allows_host_load_and_remote_join_with_the_same_launcher_settings()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var ownRelay = await ReplayRelay.StartAsync(deadline.Token);
        await using var remoteRelay = await ReplayRelay.StartAsync(deadline.Token);
        await using var remoteHost = await ReplayPeer.ConnectAsync(remoteRelay.Port, "remote-host", deadline.Token);
        var settings = new AppSettings { HostPort = ownRelay.Port, HostAllowSteam = false, ConnectGate = true, MenuTakeover = true };
        string sameSettings = JsonSerializer.Serialize(settings);
        var local = new ServerInfo { Name = "Local", Ip = IPAddress.Loopback.ToString(), Port = settings.HostPort };
        var remote = new ServerInfo { Name = "Remote", Ip = IPAddress.Loopback.ToString(), Port = remoteRelay.Port };
        var roles = new LauncherSessionRole();

        // This is the agent's actual --test-connection handshake through the shipped TCP relay.
        var diagnostic = await ProbeAsync(remote, deadline.Token);
        Assert.True(diagnostic.Reachable, diagnostic.Detail);
        Assert.Equal("direct", diagnostic.Via);
        Assert.True(diagnostic.VersionMatch);
        Assert.True(diagnostic.HostConnected);
        Assert.Equal(1, diagnostic.Players);
        roles.RememberJoinTarget(remote);

        var host = Assert.IsType<SessionLaunchPlan>(roles.Select(SessionRole.Host, local));
        Assert.Equal(SessionRole.Host, host.Role);
        Assert.Equal(ownRelay.Port, host.Server.Port);
        Assert.Equal(" --hosting", host.HostClaimArgument);
        Assert.Contains("role=\"host\"", Model(host));

        // Model -> real Lua menu and log event -> real launcher follower and dispatcher.
        // The final callbacks capture what the game console and Join path would receive.
        var folder = Directory.CreateTempSubdirectory("kcdmp-h22-");
        try
        {
            var load = new List<string>();
            var join = new List<Choice>();
            Task IssueLoad(Choice choice)
            {
                load.Add(MenuTakeoverRule.LoadCall(choice.Playline, choice.Name));
                return Task.CompletedTask;
            }
            Task RequestJoin(Choice choice)
            {
                join.Add(choice);
                return Task.CompletedTask;
            }

            string hostLog = Path.Combine(folder.FullName, "host-kcd.log");
            var hostFollower = MenuLog(hostLog, out var hostChoices);
            var hostMenu = new MenuLua(hostLog);
            hostMenu.Render(Model(host));
            hostMenu.Click("MP_StartGame");
            hostMenu.Click("MP_Load_1");
            hostFollower.Poll(1);
            Assert.Equal(GameStage.Menu, hostFollower.State.Stage);
            Assert.Equal(new GateView(false, ConnectGateRule.LoadSaveFirst), HostGate(hostFollower, 1));
            Assert.True(await LauncherSessionRole.DispatchMenuActionAsync(host.Role, hostFollower.State.Stage,
                Assert.Single(hostChoices), IssueLoad, RequestJoin));
            Assert.Contains("KCD2MP_Wo124LoadGame(2,\"autosave118\",\"w159\")", Assert.Single(load));
            Assert.Empty(join);

            File.AppendAllText(hostLog, "Loading saved game 'autosave118'\n");
            hostFollower.Poll(2);
            Assert.Equal(new GateView(false, ConnectGateRule.WorldLoading), HostGate(hostFollower, 2));
            Assert.False(await LauncherSessionRole.DispatchMenuActionAsync(host.Role, hostFollower.State.Stage,
                Assert.Single(hostChoices), IssueLoad, RequestJoin));
            File.AppendAllText(hostLog, "Gameplay started\n");
            hostFollower.Poll(3);
            double settled = 3 + ConnectGateRule.SettleS;
            Assert.False(HostGate(hostFollower, settled - 0.5).Open);
            Assert.True(HostGate(hostFollower, settled).Open);
            Assert.True(MenuTakeoverRule.HostAutoConnect(host.Hosting, load.Count == 1, hostFollower.State.Stage,
                hostFollower.State.WorldSinceS, settled, waitingForConnect: true, gateOpen: HostGate(hostFollower, settled).Open));

            // In a fresh menu, the opposite button requests the tested address while the local relay stays open.
            string joinLog = Path.Combine(folder.FullName, "join-kcd.log");
            var joinFollower = MenuLog(joinLog, out var joinChoices);
            var joinMenu = new MenuLua(joinLog);
            joinMenu.Render(Model(host));
            joinMenu.Click("MP_JoinGame");
            joinFollower.Poll(1);
            var roleRequest = Assert.Single(joinChoices);
            Assert.Equal(("role", "join"), (roleRequest.Kind, roleRequest.Name));
            var joining = Assert.IsType<SessionLaunchPlan>(roles.Select(SessionRole.Join));
            Assert.Equal(SessionRole.Join, joining.Role);
            Assert.Equal(remoteRelay.Port, joining.Server.Port);
            Assert.NotEqual(host.Server.Port, joining.Server.Port);
            Assert.Empty(joining.HostClaimArgument);
            Assert.True((await ProbeAsync(local, deadline.Token)).Reachable);
            Assert.True(ConnectGateRule.ForJoiner(ConnectGateRule.FromTest(diagnostic.Reachable,
                diagnostic.Kind.ToString(), diagnostic.HostConnected)).Open);

            joinMenu.Render(Model(joining, roleRequest.Seq));
            joinMenu.Click("MP_JoinBring");
            joinFollower.Poll(2);
            var joinChoice = Assert.Single(joinChoices.Where(choice => choice.Kind == "join"));
            Assert.True(joinChoice.Seq > roleRequest.Seq);
            Assert.False(await LauncherSessionRole.DispatchMenuActionAsync(joining.Role, joinFollower.State.Stage,
                Assert.Single(hostChoices), IssueLoad, RequestJoin));
            Assert.True(await LauncherSessionRole.DispatchMenuActionAsync(joining.Role, joinFollower.State.Stage,
                joinChoice, IssueLoad, RequestJoin));
            Assert.Equal("bring", Assert.Single(join).Join);
            Assert.Single(load);

            // Repeat in the reverse direction in a fresh menu and keep the same settings.
            string reverseLog = Path.Combine(folder.FullName, "reverse-kcd.log");
            var reverseFollower = MenuLog(reverseLog, out var reverseChoices);
            var reverseMenu = new MenuLua(reverseLog);
            reverseMenu.Render(Model(joining));
            reverseMenu.Click("MP_StartGame");
            reverseFollower.Poll(1);
            var hostRequest = Assert.Single(reverseChoices);
            Assert.Equal(("role", "host"), (hostRequest.Kind, hostRequest.Name));
            var reverseHost = Assert.IsType<SessionLaunchPlan>(roles.Select(SessionRole.Host, local));
            Assert.Equal(ownRelay.Port, reverseHost.Server.Port);
            Assert.Equal(" --hosting", reverseHost.HostClaimArgument);
            Assert.Equal(new GateView(false, ConnectGateRule.LoadSaveFirst), HostGate(reverseFollower, 1));

            reverseMenu.Render(Model(reverseHost, hostRequest.Seq));
            reverseMenu.Click("MP_Load_1");
            reverseFollower.Poll(2);
            var reverseLoad = Assert.Single(reverseChoices.Where(choice => choice.Kind == "load"));
            Assert.True(reverseLoad.Seq > hostRequest.Seq);
            Assert.True(await LauncherSessionRole.DispatchMenuActionAsync(reverseHost.Role, reverseFollower.State.Stage,
                reverseLoad, IssueLoad, RequestJoin));
            Assert.Equal(2, load.Count);
            Assert.Equal(load[0], load[1]);
            Assert.Single(join);
            Assert.Equal(sameSettings, JsonSerializer.Serialize(settings));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(GameStage.Menu, true, true, false, false, true)]
    [InlineData(GameStage.Menu, false, true, false, false, false)]
    [InlineData(GameStage.Menu, true, false, false, false, false)]
    [InlineData(GameStage.Menu, true, true, true, false, false)]
    [InlineData(GameStage.Menu, true, true, false, true, false)]
    [InlineData(GameStage.Loading, true, true, false, false, false)]
    [InlineData(GameStage.World, true, true, false, false, false)]
    public void A_role_change_requires_the_current_idle_main_menu(GameStage stage, bool waiting, bool current,
        bool agentActive, bool menuActionStarted, bool allowed) =>
        Assert.Equal(allowed, LauncherSessionRole.MenuSwitchAllowed(stage, waiting, current, agentActive, menuActionStarted));

    [Fact]
    public void The_session_log_ignores_recent_prior_actions_and_detects_a_fast_return_to_menu()
    {
        var folder = Directory.CreateTempSubdirectory("kcdmp-h22-log-");
        try
        {
            string path = Path.Combine(folder.FullName, "kcd.log");
            DateTime started = new(2026, 2, 18, 12, 0, 3, DateTimeKind.Local);
            var choices = new List<Choice>();
            var follower = MenuFollower(path, started, choices);
            File.WriteAllText(path, MenuLogText(started.AddSeconds(-2), "load 2 autosave118"));
            follower.Poll(1);
            Assert.False(follower.LatestPollRead);
            Assert.Equal(GameStage.Starting, follower.State.Stage);
            Assert.Empty(choices);

            File.WriteAllText(path, MenuLogText(started, "load 2 autosave119"));
            follower.Poll(2);
            Assert.True(follower.LatestPollRead);
            Assert.Equal(GameStage.Menu, follower.State.Stage);
            Assert.Equal("autosave119", Assert.Single(choices).Name);
            long initialMenu = follower.State.MenuRevision;

            File.AppendAllText(path, "Loading saved game 'autosave119'\nExiting to main menu because save game loading failed\n");
            follower.Poll(3);
            Assert.True(follower.LatestPollRead);
            Assert.Equal(GameStage.Menu, follower.State.Stage);
            Assert.True(follower.State.MenuRevision > initialMenu);
            Assert.Single(choices);
        }
        finally { folder.Delete(recursive: true); }
    }

    private static Task<ConnectionTest.Result> ProbeAsync(ServerInfo target, CancellationToken ct) =>
        ConnectionTest.RunAsync(new ClientConfig { ServerHost = target.Ip, ServerPort = target.Port }, ct);

    private static string Model(SessionLaunchPlan session, long roleAck = 0) =>
        MenuTakeoverRule.ModelCall(session.Hosting, [new(2, "autosave118", "Playline 2")],
            "", null, "", "idle", true, "", roleAck: roleAck);

    private static KcdLogFollower MenuLog(string path, out List<Choice> choices)
    {
        DateTime started = DateTime.Now;
        File.WriteAllText(path, MenuLogStart(started));
        choices = new List<Choice>();
        return MenuFollower(path, started, choices);
    }

    private static string MenuLogText(DateTime started, string action) =>
        MenuLogStart(started) + $"[KCD2-MP-EVT] v1 3 w159 {action}\n";

    private static string MenuLogStart(DateTime started)
    {
        string header = started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"Log Started at {header}\nPlayVideoOnly 'main_menu.bk2'\n";
    }

    private static KcdLogFollower MenuFollower(string path, DateTime started, List<Choice> choices) =>
        new(path, started, KcdLogFollower.SessionStartSlack)
        {
            OnLine = line => { if (MenuTakeoverRule.Parse(line) is { } choice) choices.Add(choice); },
        };

    private static GateView HostGate(KcdLogFollower follower, double nowS) =>
        ConnectGateRule.ForHost(follower.State.Stage, follower.State.WorldSinceS, follower.Reading, nowS, nowS);

    internal sealed class MenuLua
    {
        private readonly Script _lua = new(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling);

        public MenuLua(string logPath)
        {
            var system = new Table(_lua);
            system["LogAlways"] = DynValue.NewCallback((_, args) =>
            {
                File.AppendAllText(logPath, args[0].String + "\n");
                return DynValue.Nil;
            });
            _lua.Globals["System"] = system;
            _lua.DoString("""
                KCD2MP = {}
                os = { clock = function() return 0 end }
                Game = { IsLoadingEngineSaveGame = function() return false end }
                local buttons = {}
                UIAction = {
                    CallFunction = function(element, instance, command, id, container, text, tip, disabled)
                        if command == "ClearAll" then buttons = {} end
                        if command == "AddBasicButton" then buttons[id] = not disabled end
                    end,
                    RegisterElementListener = function(listener, element, instance, event, method)
                        if event ~= "OnButton" then return end
                        click = function(id)
                            assert(buttons[id], "Menu did not enable " .. id)
                            listener[method](listener, element, instance, event, { [0] = id })
                        end
                    end
                }
                """);

            string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lua", "kdcmp.lua"));
            // Keep the real local logger in the same Lua chunk as the menu that uses it.
            _lua.DoString(
                SourceBetween(source, "-- ===== Debug Logger =====", "-- WO-106 Phase 5:") + "\n" +
                SourceBetween(source, "-- ===== Outbound Events (WO-2) =====", "-- ===== Interaction Prompt UI (WO-2) =====") + "\n" +
                SourceBetween(source, "-- ===== WO-159: Start Game / Join Game on the main menu", "-- ----- the prologue's rendered cutscenes"));
        }

        public void Render(string model)
        {
            _lua.DoString(model);
            _lua.DoString(MenuTakeoverRule.TickCall);
        }

        public void Click(string id) => _lua.Call(_lua.Globals.Get("click"), DynValue.NewString(id));

        private static string SourceBetween(string source, string start, string end)
        {
            int first = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(first >= 0, $"Lua source marker missing: {start}");
            int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
            Assert.True(last > first, $"Lua source marker missing after {start}: {end}");
            return source[first..last];
        }
    }
}
