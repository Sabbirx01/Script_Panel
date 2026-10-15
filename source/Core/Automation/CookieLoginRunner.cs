using System.Windows.Threading;
using AISuitePanel.Models;

namespace AISuitePanel.Automation;

/// <summary>
/// Section 2 - replays stored cookies for the selected accounts and refreshes the
/// session that comes back. Accounts with no usable cookie are marked
/// <see cref="CookieState.Expired"/> instead of being silently skipped.
/// </summary>
public static class CookieLoginRunner
{
    private sealed class Counters
    {
        public int Done;
        public int Ok;
        public int Bad;
    }

    public static async Task<RunSummary> RunAsync(
        ToolInfo tool,
        IReadOnlyList<AccountRecord> accounts,
        ToolSettings settings,
        Dispatcher ui,
        Action<int, int>? onProgress,
        CancellationToken ct)
    {
        var total = accounts.Count;
        if (total == 0) return RunSummary.Empty;

        var threads = Math.Clamp(settings.Threads, 1, 10);
        var gap = TimeSpan.FromSeconds(Math.Clamp(settings.DelaySeconds, 0, 120));
        var counters = new Counters();

        Log.Info(tool.Id, $"Login started - {total} cookie set(s), {threads} thread(s).");

        using var gate = new SemaphoreSlim(threads, threads);

        var workers = new List<Task>(total);
        foreach (var account in accounts)
            workers.Add(Worker(account));

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // Workers log their own cancellation.
        }

        var summary = new RunSummary(total, counters.Ok, counters.Bad);

        Log.Write(summary.Failed == 0 ? LogKind.Success : LogKind.Warn, tool.Id,
            $"Login finished - {summary.Succeeded} session(s) restored, {summary.Failed} failed.");

        return summary;

        // ------------------------------------------------------------- worker

        async Task Worker(AccountRecord account)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                ct.ThrowIfCancellationRequested();
                await Gap().ConfigureAwait(false);

                var stored = CookieVault.Get(tool.Id, account.Id);

                if (stored is null || stored.Cookies.Count == 0)
                {
                    OnUi(() =>
                    {
                        account.CookieState = CookieState.Expired;
                        account.Note = "No stored cookie";
                        AccountStore.Persist(tool.Id);
                    });

                    Interlocked.Increment(ref counters.Bad);
                    Log.Warn(tool.Id, $"#{account.IdLabel} skipped - no stored cookie for {account.EmailUsed}.");
                    return;
                }

                var result = await ToolDrivers.For(tool.Id)
                    .LoginWithCookiesAsync(stored.Email, stored.Cookies, ct)
                    .ConfigureAwait(false);

                if (!result.Ok)
                {
                    OnUi(() =>
                    {
                        account.CookieState = CookieState.LoginFailed;
                        account.Note = result.Message;
                        AccountStore.Persist(tool.Id);
                    });

                    Interlocked.Increment(ref counters.Bad);
                    Log.Error(tool.Id, $"#{account.IdLabel} login failed - {result.Message}");
                    return;
                }

                // Keep whatever the tool handed back - sessions usually rotate.
                stored.Cookies = (result.Cookies ?? stored.Cookies).ToList();
                stored.LastUsedAt = DateTime.Now;
                CookieVault.Save(tool.Id, stored);

                OnUi(() =>
                {
                    account.CookieState = CookieState.Saved;
                    account.Status = AccountStatus.Active;
                    account.LastLoginAt = DateTime.Now;
                    account.Note = "";
                    AccountStore.Persist(tool.Id);
                });

                Interlocked.Increment(ref counters.Ok);
                Log.Success(tool.Id, $"#{account.IdLabel} logged in - session refreshed.");
            }
            catch (OperationCanceledException)
            {
                Log.Warn(tool.Id, $"#{account.IdLabel} cancelled.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref counters.Bad);
                Log.Error(tool.Id, $"#{account.IdLabel} stopped - {ex.Message}");
            }
            finally
            {
                gate.Release();

                var completed = Interlocked.Increment(ref counters.Done);
                OnUi(() => onProgress?.Invoke(completed, total));
            }
        }

        async Task Gap()
        {
            if (gap <= TimeSpan.Zero) return;
            await Task.Delay(gap, ct).ConfigureAwait(false);
        }

        void OnUi(Action action)
        {
            try
            {
                if (ui.CheckAccess()) action();
                else ui.Invoke(action);
            }
            catch (TaskCanceledException) { /* panel closed mid-run */ }
            catch (InvalidOperationException) { /* dispatcher shut down */ }
        }
    }
}
