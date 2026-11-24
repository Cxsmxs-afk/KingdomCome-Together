// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

public partial class GameBridge
{
    private static readonly TimeSpan ReturnCheckDelay = TimeSpan.FromMinutes(5);
    private PrivateReturnCheck? _privateReturnCheck;
    private Task _privateReturnMenuUpdate = Task.CompletedTask;

    private sealed class PrivateReturnCheck(JoinEngineReceipt receipt, WorldLoadSnapshot load)
    {
        public JoinEngineReceipt Receipt { get; } = receipt;
        public WorldLoadSnapshot Load { get; } = load;
        public bool DeadlineChecked;
        public bool Checking;
        public volatile bool RetryOffered;
    }

    // Called at the HTTP handoff while _joinGate is held. The sender continues
    // to own the physical lease until its receipt settles.
    private void AttachPrivateReturn(JoinEngineReceipt receipt)
    {
        receipt.PublishesPrivateReturn = true;
        Volatile.Write(ref _privateReturnCheck, new(receipt, _worldLoad.Current));
    }

    private void DetachPrivateReturn(JoinEngineReceipt? receipt)
    {
        lock (_joinGate)
            if (_privateReturnCheck is { } check && ReferenceEquals(check.Receipt, receipt))
            {
                Volatile.Write(ref _privateReturnCheck, null);
                if (check.RetryOffered) QueuePrivateReturnMenu(pending: false);
            }
    }

    // The local retry flag is independent of the relay menu state. Keep
    // the offer and its clear in command order if the engine answers late.
    private void QueuePrivateReturnMenu(bool pending) =>
        _privateReturnMenuUpdate = PublishPrivateReturnMenuAsync(_privateReturnMenuUpdate, pending);

    private async Task PublishPrivateReturnMenuAsync(Task prior, bool pending)
    {
        await prior;
        try
        {
            string value = pending ? "true" : "false";
            await GameCalls.ExecuteNowAsync($"if KCD2MP_W154MenuReturnRetry then KCD2MP_W154MenuReturnRetry({value}) end",
                _joinEngineLifetime);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-H21-HOME retry-menu unavailable transport={ex.GetType().Name}");
        }
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
        bool ownSave = check.Receipt.Destination == LauncherPrivateReturn.OwnSave;
        long readSequence = ownSave ? check.Receipt.FindProbedRead() : 0;
        try
        {
            // This round trip uses the same live place observation as W160,
            // with a token so an old unsolicited "world" cannot answer it.
            where = await Wo154WhereNowAsync(requireActiveLog: true);
            if (where == "world" && readSequence != 0)
                last = await AskModAsync("KCD2MP_Wo125LastLoaded", 4000, "true", _joinEngineLifetime, requireActiveLog: true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-H21-HOME place-check unavailable transport={ex.GetType().Name}");
        }

        lock (_joinGate)
        {
            check.Checking = false;
            if (!ReferenceEquals(_privateReturnCheck, check) || check.Receipt.Terminal.Task.IsCompleted) return;
            if (OwnLoadExpected && where == "menu" && check.Receipt.Destination == LauncherPrivateReturn.Menu &&
                _worldLoad.TryConfirmPrivateReturn(check.Load, check.Receipt.TryConfirmMenuReturn))
            {
                Console.WriteLine("MP-H21-HOME place-check result=menu-exit");
                return;
            }
            if (ownSave && OwnLoadExpected && last is not null && last.StartsWith("path=", StringComparison.Ordinal) &&
                ConfirmPrivateRead(last[5..]))
            {
                Console.WriteLine("MP-H21-HOME place-check result=private-world");
                return;
            }
            if (!check.RetryOffered)
            {
                check.RetryOffered = true;
                QueuePrivateReturnMenu(pending: true);
            }
            SetJoinUi("return-needed", "Your return is still unconfirmed. Choose Leave in the co-op menu to check it again.");
            Console.WriteLine($"MP-H21-HOME place-check result=pending where={where} player_retry={On(playerRequested)}");
        }

        bool ConfirmPrivateRead(string path) => LoadSequenceEnabled
            ? _worldLoad.TryConfirmPrivateReturn(check.Load, () => check.Receipt.TryConfirmObservedPrivateRead(readSequence, path))
            : check.Receipt.TryConfirmObservedPrivateRead(readSequence, path);
    }
}
