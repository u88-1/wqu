namespace GeminiMenu;

/// <summary>
/// A system-wide (per Windows session) limit on how many files are processed at once.
/// Every file runs in its own process/window; extra ones wait here instead of fighting over Gemini's quota.
/// </summary>
public sealed class JobSlot : IDisposable
{
    readonly Semaphore _sem;
    JobSlot(Semaphore sem) => _sem = sem;

    public static async Task<JobSlot> AcquireAsync(int max, Action<string> status, CancellationToken ct)
    {
        max = Math.Clamp(max, 1, 4);
        var sem = new Semaphore(max, max, @"Local\GeminiMenu.Jobs");
        bool announced = false;
        try
        {
            while (!sem.WaitOne(0))
            {
                if (!announced) { status("ממתין בתור: קבצים אחרים מעובדים כעת. העיבוד יתחיל אוטומטית."); announced = true; }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch { sem.Dispose(); throw; }
        return new JobSlot(sem);
    }

    public void Dispose()
    {
        try { _sem.Release(); } catch { }
        _sem.Dispose();
    }
}
