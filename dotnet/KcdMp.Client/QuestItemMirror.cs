// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace KcdMp.Client;

public static class QuestItemMirror
{
    public const int MaxClassCallEncoded = 1800;

    public static async Task<bool> PublishClassesAsync(Func<Task<IReadOnlyList<string>?>> load,
        Func<string, Task> execute, Func<bool> current)
    {
        var classes = await load();
        if (classes is null || classes.Count == 0 || !current()) return false;
        await execute("if KCD2MP_W200QClassReset then KCD2MP_W200QClassReset() end");
        for (int i = 0; i < classes.Count; i += Wo200Rules.QClassChunk)
        {
            if (!current()) return false;
            string call = $"if KCD2MP_W200QClassAdd then KCD2MP_W200QClassAdd(\"{string.Join(' ', classes.Skip(i).Take(Wo200Rules.QClassChunk))}\") end";
            if (Uri.EscapeDataString("#" + call).Length > MaxClassCallEncoded)
                throw new InvalidDataException("Quest-item class call exceeds the console's encoded budget");
            await execute(call);
        }
        return current(); // caller marks initialization complete only after every accepted chunk
    }

    /// <summary>Count accepted per-peer item-count messages, including zero (hand-in), rather than send attempts.</summary>
    public static async Task<int> ForwardAsync(string? text, IReadOnlyList<byte> peers, Func<byte, string, Task<bool>> send)
    {
        int sent = 0;
        foreach (string pair in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = pair.IndexOf(':');
            if (colon <= 0 || !Guid.TryParse(pair.AsSpan(0, colon), out Guid cls) || cls == Guid.Empty
                || !int.TryParse(pair.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                || count > Wo200Rules.QCountMax) continue;
            string payload = Wo200Rules.ItemCountText(count, cls);
            foreach (byte peer in peers) if (await send(peer, payload)) sent++;
        }
        return sent;
    }
}
