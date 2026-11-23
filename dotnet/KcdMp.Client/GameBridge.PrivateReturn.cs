// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

public partial class GameBridge
{
    private static readonly TimeSpan ReturnCheckDelay = TimeSpan.FromMinutes(5);
    private PrivateReturnCheck? _privateReturnCheck;

    private sealed class PrivateReturnCheck(JoinEngineReceipt receipt, string? ownSave, WorldLoadSnapshot load)
    {
        public JoinEngineReceipt Receipt { get; } = receipt;
        public string? OwnSave { get; } = ownSave;
        public WorldLoadSnapshot Load { get; } = load;
        public bool DeadlineChecked;
        public bool Checking;
        public volatile bool RetryOffered;
    }

    // Called at the HTTP handoff while _joinGate is held. The sender continues
    // to own the physical lease until its receipt settles.
    private void AttachPrivateReturn(JoinEngineReceipt receipt, string? ownSave) =>
        Volatile.Write(ref _privateReturnCheck, new(receipt, ownSave, _worldLoad.Current));

    private void DetachPrivateReturn(JoinEngineReceipt? receipt)
    {
        lock (_joinGate)
            if (_privateReturnCheck is { } check && ReferenceEquals(check.Receipt, receipt))
                Volatile.Write(ref _privateReturnCheck, null);
    }

    // This also runs after relay teardown. The relay tick can trigger the same
    // check earlier if the leave already spent part of its deadline preparing.
    private async Task<bool> WaitForPrivateReturnAsync(JoinEngineReceipt receipt)
    {
        try { return await receipt.Terminal.Task.WaitAsync(ReturnCheckDelay, _joinEngineLifetime); }
        catch (TimeoutException)
        {
            if (Volatile.Read(ref _privateReturnCheck) is { } check && ReferenceEquals(check.Receipt, receipt))
                await CheckPrivateReturnAsync(check);
            return await receipt.Terminal.Task.WaitAsync(_joinEngineLifetime);
        }
    }

    private async Task CheckPrivateReturnAsync(PrivateReturnCheck check, bool playerRequested = false)
    {
        lock (_joinGate)
        {
            if (!ReferenceEquals(_privateReturnCheck, check) || check.Receipt.Terminal.Task.IsCompleted ||
                check.Checking || (playerRequested ? !check.RetryOffered : check.DeadlineChecked)) return;
            check.DeadlineChecked = true;
            check.Checking = true;
        }

        string where = "unknown";
        string? last = null;
        try
        {
            // This round trip uses the same live place observation as W160,
            // with a token so an old unsolicited "world" cannot answer it.
            where = await Wo154WhereNowAsync();
            if (where == "world" && check.OwnSave is not null && check.Receipt.ObservedExactRead)
                last = await AskModAsync("KCD2MP_Wo125LastLoaded", 4000);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-H21-HOME place-check unavailable transport={ex.GetType().Name}");
        }

        lock (_joinGate)
        {
            check.Checking = false;
            if (!ReferenceEquals(_privateReturnCheck, check) || check.Receipt.Terminal.Task.IsCompleted) return;
            var load = _worldLoad.Current;
            bool sameLoad = !LoadSequenceEnabled || (load.Generation == check.Load.Generation &&
                load.OwnIntentRevision == check.Load.OwnIntentRevision);
            if (OwnLoadExpected && sameLoad && check.OwnSave is { } own &&
                string.Equals(last, "last=" + own, StringComparison.OrdinalIgnoreCase) &&
                check.Receipt.TryConfirmObservedPrivateRead())
            {
                Console.WriteLine("MP-H21-HOME place-check result=private-world");
                return;
            }
            check.RetryOffered = true;
            SetJoinUi("return-needed", "Your return is still unconfirmed. Choose Leave in the co-op menu to check it again.");
            Console.WriteLine($"MP-H21-HOME place-check result=pending where={where} player_retry={On(playerRequested)}");
        }
    }
}
