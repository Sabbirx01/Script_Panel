using System.Windows.Threading;
using AISuitePanel.Models;

namespace AISuitePanel.Automation;

/// <summary>Result of one batch run.</summary>
public sealed record RunSummary(int Total, int Succeeded, int Failed)
{
    public static RunSummary Empty => new(0, 0, 0);
}

/// <summary>
/// Section 1 - creates accounts for one tool: allocate an address, register,
/// wait for the verification mail, store the session.
///
/// Threading: the run is a WAVE - up to <c>atOnce</c> (default
/// <paramref name="settings"/>.Threads) workers are live at the same time and a
/// new one starts as soon as a previous one finishes, until <c>quantity</c>
/// accounts have been attempted. Every <c>DelaySeconds</c> gap is applied
/// between two discrete actions. Collection mutations are marshalled to the UI
/// thread; file writes happen on the worker.
/// </summary>
public static class CreateAccountRunner
{
    private sealed class Counters
    {
        public int Done;
        public int Ok;
        public int Bad;
    }

    public static async Task<RunSummary> RunAsync(
        ToolInfo tool,
        MailSource source,
        int quantity,
        ToolSettings settings,
        Dispatcher ui,
        Action<int, int>? onProgress,
        CancellationToken ct,
        int? atOnce = null)
    {
        // "At once" = how many workers may be live in parallel. Falls back to
        // the tool's Threads setting when the caller does not pass one.
        var threads = Math.Clamp(atOnce ?? settings.Threads, 1, Math.Max(1, quantity));
        var gap = TimeSpan.FromSeconds(Math.Clamp(settings.DelaySeconds, 0, 120));
        var counters = new Counters();

        Log.Info(tool.Id,
            $"Create started - {quantity} account(s) via {SourceLabel(source)}, " +
            $"{threads} at once, {gap.TotalSeconds:0}s delay.");

        using var gate = new SemaphoreSlim(threads, threads);

        var workers = new List<Task>(quantity);
        for (var i = 0; i < quantity; i++)
            workers.Add(Worker(i + 1));

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // Individual workers already logged their own cancellation.
        }

        var summary = new RunSummary(quantity, counters.Ok, counters.Bad);

        if (summary.Total > 0)
        {
            Log.Write(summary.Failed == 0 ? LogKind.Success : LogKind.Warn, tool.Id,
                $"Create finished - {summary.Succeeded} created, {summary.Failed} failed.");
        }

        return summary;

        // ------------------------------------------------------------- worker

        async Task Worker(int serial)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                ct.ThrowIfCancellationRequested();
                await Gap().ConfigureAwait(false);

                var routed = await MailRouter.NextAsync(tool.Id, source, serial).ConfigureAwait(false);
                if (routed is null)
                {
                    Interlocked.Increment(ref counters.Bad);
                    Log.Error(tool.Id, $"#{serial:00} skipped - no address available for {SourceLabel(source)}.");
                    return;
                }

                var record = new AccountRecord
                {
                    EmailUsed = routed.Address,
                    MailSource = source,
                    SourceRef = routed.SourceRef,
                    Status = AccountStatus.Pending,
                    CookieState = CookieState.None,
                    CreatedAt = DateTime.Now
                };

                OnUi(() =>
                {
                    record.Id = AccountStore.NextId(tool.Id);
                    AccountStore.Add(tool.Id, record);
                });

                Log.Info(tool.Id, $"#{record.IdLabel} registering {routed.Address} ...");

                await Gap().ConfigureAwait(false);

                var result = await ToolDrivers.For(tool.Id)
                    .CreateAccountAsync(routed.Address, source, ct)
                    .ConfigureAwait(false);

                if (!result.Ok)
                {
                    OnUi(() =>
                    {
                        record.Status = AccountStatus.Failed;
                        record.Note = result.Message;
                        AccountStore.Persist(tool.Id);
                    });

                    Interlocked.Increment(ref counters.Bad);
                    Log.Error(tool.Id, $"#{record.IdLabel} failed - {result.Message}");
                    return;
                }

                var cookies = result.Cookies ?? Array.Empty<BrowserCookie>();

                CookieVault.Save(tool.Id, new CookieRecord
                {
                    AccountId = record.Id,
                    Email = routed.Address,
                    SavedAt = DateTime.Now,
                    Cookies = cookies.ToList()
                });

                OnUi(() =>
                {
                    record.Status = AccountStatus.Active;
                    record.CookieState = CookieState.Saved;
                    record.Note = "";
                    AccountStore.Persist(tool.Id);
                });

                Interlocked.Increment(ref counters.Ok);
                Log.Success(tool.Id, $"#{record.IdLabel} created - {cookies.Count} cookie(s) saved.");
            }
            catch (OperationCanceledException)
            {
                Log.Warn(tool.Id, $"#{serial:00} cancelled.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref counters.Bad);
                Log.Error(tool.Id, $"#{serial:00} stopped - {ex.Message}");
            }
            finally
            {
                gate.Release();

                var completed = Interlocked.Increment(ref counters.Done);
                OnUi(() => onProgress?.Invoke(completed, quantity));
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

    public static string SourceLabel(MailSource source) => source switch
    {
        MailSource.TempMail     => "Temp Mail",
        MailSource.DottedGmail  => "Dotted Gmail",
        MailSource.CustomDomain => "Custom Domain",
        MailSource.CustomMail   => "Custom Mail",
        MailSource.SemiAuto     => "Semi Automation",
        MailSource.NoProxy      => "No Proxy",
        MailSource.OutlookPool  => "Outlook Pool",
        _ => source.ToString()
    };
}
