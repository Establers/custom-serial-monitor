namespace SerialMonitor.WinUI.Services;

public sealed record LogHistoryExportProgress(int WrittenRecords, int TotalRecords, bool IsCompleted = false)
{
    // Buffered writes and the final rename still have to finish at the end of the loop.
    public int Percent => IsCompleted ? 100 : TotalRecords <= 0 ? 0 :
        (int)Math.Clamp((long)WrittenRecords * 100 / TotalRecords, 0, 99);
}
