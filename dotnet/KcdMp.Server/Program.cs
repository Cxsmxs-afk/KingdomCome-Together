// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// Portions from the original project, marczukmichal/kcd2-multiplayer; its author keeps their copyright (AUTHORS).
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Server.Features.Faults;
using KcdMp.Server.Features.Interactions;
using KcdMp.Server.Features.ServerInformation;
using KcdMp.Server.Features.Steam;
using KcdMp.Server.Features.Tcp;
using Serilog;

namespace KcdMp.Server;

public class Program
{
	/// <summary>
	/// Maps the pre-refactor CLI flags onto configuration keys, so
	/// "--port 7778" still works now that settings live in appsettings.json.
	/// </summary>
	private static readonly Dictionary<string, string> SwitchMappings = new()
	{
		["--port"] = "Tcp:Port",
		["-p"]     = "Tcp:Port",
		["--echo"] = "Echo",
		// WO-127: the launcher's "Also allow Steam" (Host window).
		["--steam"]      = "Steam:Enabled",
		["--steam-app"]  = "Steam:AppId",
		["--steam-game"] = "Steam:GameExe",
	};

	/// <summary>
	/// The server's entry point.
	/// </summary>
	static async Task Main(string[] args)
	{
		await using var app = CreateApp(args);
		await app.RunAsync();
	}

	/// <summary>
	/// Builds the relay exactly as <see cref="Main"/> runs it -- same services,
	/// same configuration sources -- without starting it. WO-101: this is what
	/// the loopback round-trip gate (dotnet/KcdMp.Relay.Tests) hosts
	/// in-process, so the test exercises the REAL ClientSession /
	/// TcpBroadcastService code path over a real socket, not a copy of it.
	/// With port zero, TcpSocketService reports the actual OS-assigned endpoint.
	/// </summary>
	public static WebApplication CreateApp(string[] args) => CreateApp(args, null);

	public static WebApplication CreateApp(string[] args, TimeProvider? idleTimeProvider)
	{
		// WO-206 B: the test bench's fault mode (Features/Faults/RelayFaultPlan.cs).
		// Both the environment variable and the option, or nothing. The option
		// leaves the argument list here, before any configuration source sees
		// it, and the fault layer reads no configuration -- so no settings file
		// can turn it on.
		string? faultPlanPath = RelayFaultSwitch.Resolve(ref args,
			Environment.GetEnvironmentVariable(RelayFaultSwitch.EnvVar), out string? faultNote);

		args = NormaliseArgs(args);

		var builder = WebApplication.CreateBuilder(args);

		// CreateBuilder already added a command-line source, but without the
		// aliases above; re-adding it last lets "--port" win over appsettings.
		builder.Configuration.AddCommandLine(args, SwitchMappings);

		string? loopbackUrl = null;
		if (builder.Configuration.GetValue<bool>("Tcp:LoopbackOnly"))
		{
			if (builder.Configuration["Tcp:BindAddress"] != "127.0.0.1" ||
				!Uri.TryCreate(builder.Configuration["urls"], UriKind.Absolute, out var local) ||
				local.Scheme != "http" || local.Host != "127.0.0.1" || local.Port is <= 0 or > 65535)
				throw new InvalidOperationException("Tcp:LoopbackOnly requires 127.0.0.1 for TCP and an explicit loopback HTTP URL.");
			loopbackUrl = $"http://127.0.0.1:{local.Port}";
			builder.WebHost.ConfigureKestrel(options =>
			{
				// Use the explicitly selected bench listener, including when
				// deployment configuration names another Kestrel endpoint.
				options.Configure(new ConfigurationBuilder().Build());
				options.Listen(System.Net.IPAddress.Loopback, local.Port);
			});
		}

		// Logging config
		builder.Services.AddSerilog(options =>
		{
			options.ReadFrom.Configuration(builder.Configuration);
		});

		// Add controllers for HTTP API's
		builder.Services.AddControllers();

		// Add TCP Socket as background service
		builder.Services.AddHostedService<TcpSocketService>();

		// Announces this relay to a master server. Does nothing unless
		// MasterServer:Url is set.
		builder.Services.AddHostedService<MasterAnnounceService>();

		builder.Services.AddSingleton<ClientHandler>();
		builder.Services.AddSingleton<TcpBroadcastService>();
		builder.Services.AddSingleton<SessionManager>();
		builder.Services.AddSingleton<ClientSessionRunner>();   // WO-127: one session lifecycle for TCP and Steam
		if (idleTimeProvider is not null) builder.Services.AddSingleton(idleTimeProvider);

		// WO-127: Steam P2P beside the TCP listener. Does nothing unless Steam:Enabled.
		builder.Services.AddSingleton<SteamRelayStatus>();
		builder.Services.AddHostedService<SteamRelayService>();
		builder.Services.AddHostedService<LinkReportService>();   // WO-213 A: each client's link, every 10 s (Steam's numbers every 2 s)

		// WO-206 B: registered only with the switch on; a bad plan stops the
		// relay here -- a faulted test run without its faults is no run at all.
		if (faultPlanPath is not null)
		{
			builder.Services.AddSingleton(RelayFaultPlan.Load(faultPlanPath));
			builder.Services.AddSingleton<RelayFaults>();
			builder.Services.AddHostedService<RelayFaultsService>();
		}

		var app = builder.Build();
		if (loopbackUrl is not null)
			app.Lifetime.ApplicationStarted.Register(() =>
			{
				if (app.Urls.Count == 1 && app.Urls.Contains(loopbackUrl))
					app.Services.GetRequiredService<Serilog.ILogger>().Information("HTTP loopback ready on {Url}.", loopbackUrl);
			});

		if (faultNote is not null)
			app.Services.GetRequiredService<Serilog.ILogger>().Warning("{Line:l}", faultNote);

		// Map controller routs
		app.MapControllers();

		return app;
	}

	/// <summary>
	/// The configuration command-line parser requires every switch to carry a
	/// value and throws on a bare one. The old CLI used a bare "--echo", so
	/// expand it to "--echo true" rather than crash on startup.
	/// </summary>
	private static string[] NormaliseArgs(string[] args)
	{
		var result = new List<string>(args.Length + 1);

		for (int i = 0; i < args.Length; i++)
		{
			result.Add(args[i]);

			if (!args[i].Equals("--echo", StringComparison.OrdinalIgnoreCase))
				continue;

			// A bare "--echo" is one with no value after it, or one immediately
			// followed by another switch.
			bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith('-');
			if (!hasValue)
				result.Add("true");
		}

		return [.. result];
	}
}
