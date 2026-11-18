// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

/// <summary>Attach may use a real Gameplay edge from this log process, never a role or a timeout.</summary>
public static class GameplayReadyHistory
{
    public static bool Read(TextReader reader)
    {
        bool process = false, ready = false;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith("Log Started at ", StringComparison.Ordinal)) { process = true; ready = false; }
            else if (line.StartsWith("BackupNameAttachment=", StringComparison.Ordinal) || line == "Connecting messages from trace server...")
                { process = false; ready = false; }
            else if (process && line == "Gameplay started") ready = true;
            else if (line.StartsWith("Loading saved game '", StringComparison.Ordinal) || line.StartsWith("[CryAction] LoadGame: '", StringComparison.Ordinal) ||
                line.StartsWith("PlayVideoOnly 'main_menu", StringComparison.Ordinal) || line.StartsWith("CSystem::Quit invoked", StringComparison.Ordinal) ||
                line.StartsWith("Exiting to main menu because save game loading failed", StringComparison.Ordinal)) ready = false;
        }
        return process && ready;
    }
    public static bool Scan(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return Read(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
