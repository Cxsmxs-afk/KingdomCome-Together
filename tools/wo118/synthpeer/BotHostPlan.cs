// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Net;
using KcdMp.Client;
using KcdMp.Wire;

sealed record BotHostPlan(IReadOnlyList<string[]> Setup, IReadOnlyList<BotHostPlan.Step> Timed)
{
    public sealed record Step(double AtSeconds, string[] Fields, int Line);

    public static BotHostPlan Parse(IEnumerable<string> lines)
    {
        var setup = new List<string[]>();
        var timed = new List<Step>();
        int number = 0;
        foreach (var line in lines)
        {
            number++;
            if (number > 4096 || line.Length > 8192)
                throw new FormatException("Die Bot-Host-Planzeile ist zu groß.");
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0].StartsWith('#')) continue;

            var fields = words[1..];
            if (words[0] == "setup" && IsSetup(fields))
            {
                setup.Add(fields);
                continue;
            }
            if (words is ["at", _, _, ..] && Finite(words[1], out double seconds)
                && seconds >= 0 && seconds <= 3600 && IsTimed(words[2..]))
            {
                timed.Add(new(seconds, words[2..], number));
                continue;
            }
            throw new FormatException($"Ungültiger Bot-Host-Plan in Zeile {number}.");
        }
        if (timed.Count == 0) throw new FormatException("Der Bot-Host-Plan braucht einen zeitgesteuerten Schritt.");
        return new(setup, timed.OrderBy(step => step.AtSeconds).ToArray());
    }

    private static bool IsSetup(string[] fields) => fields switch
    {
        ["witem" or "witemtaken", var cls, var x, var y, var z] =>
            Wo134Rules.TryClass(cls, out _) && Coord(x) && Coord(y) && Coord(z),
        ["bodyset", var name, var items] =>
            Wo134Rules.BodyName.IsMatch(name) && Wo134Rules.ParseItems(items) is not null,
        _ => false
    };

    private static bool IsTimed(string[] fields) => fields switch
    {
        ["pos", var x, var y, var z] => Coord(x) && Coord(y) && Coord(z),
        ["walk", var vx, var vy, var duration, .. var rest] => Speed(vx) && Speed(vy)
            && Finite(duration, out var seconds) && seconds is > 0 and <= 300
            && (rest.Length == 0 || rest.Length <= 2 && Speed(rest[0])
                && (rest.Length == 1 || int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ms) && ms is >= 20 and <= 1000)),
        ["clock", var value] => BotHostCalendar.TryParseWorldTime(value, out _),
        ["hstate", _, .. var rest] => fields.Skip(1).All(IsBodyState) && rest.Length < 4,
        _ => false
    };

    private static bool IsBodyState(string field) => field.Split('=') is ["crouch" or "torch" or "combat" or "downed", "0" or "1"];
    private static bool Speed(string value) => Finite(value, out var speed) && Math.Abs(speed) <= 20;
    private static bool Coord(string value) => Wo134Rules.TryCoord(value, out _);
    internal static bool Finite(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}

sealed record BotHostOptions(string Save, string Host, int Port, string Name, double Duration,
    float[] Position, long WorldTimeMs, BotHostPlan Plan, string WireVersion, string? LoadWeather, bool BaselineEvents, bool WorldRecovery)
{
    public const string Flag = "--test-bot";
    public const string EnvironmentName = "KCDMP_TEST_BOT";
    public const string LoadFlag = "--test-load-sequence";
    public const string LoadEnvironmentName = "KCDMP_TEST_LOAD_SEQUENCE";
    public const string LoadWeatherOption = "--weather-during-load";
    public const string BaselineFlag = "--test-baseline-events";
    public const string RecoveryFlag = "--test-world-recovery";

    public static BotHostOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool flag = false, loadFlag = false, baselineFlag = false, recoveryFlag = false;
        for (int i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == Flag && !flag) { flag = true; continue; }
            if (key == LoadFlag && !loadFlag) { loadFlag = true; continue; }
            if (key == BaselineFlag && !baselineFlag) { baselineFlag = true; continue; }
            if (key == RecoveryFlag && !recoveryFlag) { recoveryFlag = true; continue; }
            if (key is not ("--join-host125" or "--host" or "--port" or "--duration" or "--name" or "--host-pos" or "--world-time" or "--bot-plan" or "--wire-version" or LoadWeatherOption)
                || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(key, args[++i]))
                throw new FormatException("Ungültige Bot-Host-Argumente.");
        }
        if (baselineFlag && Environment.GetEnvironmentVariable("KCDMP_TEST_BASELINE_EVENTS") != "1")
            throw new FormatException("Der Ereignistest braucht KCDMP_TEST_BASELINE_EVENTS=1 und --test-baseline-events.");
        if (recoveryFlag && Environment.GetEnvironmentVariable("KCDMP_TEST_WORLD_RECOVERY") != "1")
            throw new FormatException("Der Wiederverbindungstest braucht KCDMP_TEST_WORLD_RECOVERY=1 und --test-world-recovery.");
        string? loadWeather = values.GetValueOrDefault(LoadWeatherOption);
        if (loadFlag != (loadWeather is not null) || loadFlag && Environment.GetEnvironmentVariable(LoadEnvironmentName) != "1")
            throw new FormatException("Der Wettertest braucht KCDMP_TEST_LOAD_SEQUENCE=1, --test-load-sequence und --weather-during-load.");
        if (loadWeather is not null && (loadWeather.Length is < 1 or > Protocol.MaxWeatherNameLen ||
            loadWeather.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new FormatException("Das Wetterprofil braucht 1 bis 48 ASCII-Buchstaben, Ziffern oder Unterstriche.");
        string host = values.GetValueOrDefault("--host", "127.0.0.1");
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = IPAddress.Loopback.ToString();
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new FormatException("Der Bot-Host braucht einen lokalen Relay-Endpunkt.");
        if (!int.TryParse(values.GetValueOrDefault("--port", "7778"), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
            throw new FormatException("Der Bot-Host braucht einen gültigen Relay-Port.");
        if (!BotHostPlan.Finite(values.GetValueOrDefault("--duration", "3600"), out var duration) || duration is <= 0 or > 7200)
            throw new FormatException("Die Bot-Host-Laufzeit muss zwischen 0 und 7200 Sekunden liegen.");
        if (!values.TryGetValue("--world-time", out var calendar))
            throw new FormatException("Der Bot-Host braucht --world-time in Weltsekunden.");
        long worldTime = BotHostCalendar.ParseWorldTime(calendar);
        string wireVersion = values.GetValueOrDefault("--wire-version", ReleaseVersionInfo.Wire);
        var (version, build) = BuildId.Split(wireVersion);
        if (version != BuildId.Split(ReleaseVersionInfo.Wire).Version || wireVersion.Any(char.IsControl)
            || BuildId.Wire(version, build) != wireVersion)
            throw new FormatException("Der Bot-Host braucht die passende Relay-Version mit optionaler Build-ID.");

        var xy = values.GetValueOrDefault("--host-pos", "0,0,0").Split(',');
        if (xy.Length != 3 || xy.Any(value => !Wo134Rules.TryCoord(value, out _)))
            throw new FormatException("Die Bot-Host-Position braucht drei endliche Koordinaten.");
        float[] position = xy.Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        string name = values.GetValueOrDefault("--name", "t4-bot-host");
        if (name.Length is < 1 or > 48 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new FormatException("Der Bot-Host-Name braucht 1 bis 48 Buchstaben, Ziffern, Bindestriche oder Unterstriche.");

        if (!values.TryGetValue("--join-host125", out var save) || !Path.GetExtension(save).Equals(".whs", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Der Bot-Host braucht einen externen Test-Save als .whs.");
        save = Path.GetFullPath(save);
        var file = new FileInfo(save);
        if (!file.Exists || file.Length is <= 0 or > Protocol.WorldMaxBytes)
            throw new FormatException("Der Test-Save fehlt, ist leer oder überschreitet die Relay-Grenze.");
        if (IsInCheckout(save, AppContext.BaseDirectory))
            throw new FormatException("Der Test-Save muss außerhalb des Checkouts liegen.");

        var plan = values.TryGetValue("--bot-plan", out var planFile)
            ? BotHostPlan.Parse(ReadPlan(planFile))
            : BotHostPlan.Parse(["at 0 walk 1.4 0 4 0 100", "at 4 walk 0 1.4 4 0 100"]);
        return new(save, host, port, name, duration, position, worldTime, plan, wireVersion, loadWeather, baselineFlag, recoveryFlag);
    }

    private static IEnumerable<string> ReadPlan(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 64 * 1024) throw new FormatException("Der Bot-Host-Plan fehlt oder überschreitet 64 KiB.");
        return File.ReadLines(path);
    }

    private static bool IsInCheckout(string path, string baseDirectory)
    {
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git")))
                continue;
            var rel = Path.GetRelativePath(directory.FullName, path);
            return !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        return false;
    }
}
