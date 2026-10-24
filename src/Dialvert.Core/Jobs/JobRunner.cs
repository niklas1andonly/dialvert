namespace Dialvert.Core.Jobs;

/// <summary>
/// Runs jobs with per-lane concurrency: images in parallel across cores,
/// media (FFmpeg already multithreads, NVENC has session limits) and documents mostly serial.
/// </summary>
public sealed class JobRunner
{
    readonly Dictionary<JobLane, SemaphoreSlim> _lanes = new()
    {
        [JobLane.Image] = new(Math.Max(1, Environment.ProcessorCount / 2)),
        [JobLane.Media] = new(2),
        // LibreOffice serializes itself; PDFium holds a global lock. Two lanes keep Pandoc/CSV work moving meanwhile.
        [JobLane.Document] = new(2),
    };

    public event Action<Job>? Enqueued;
    public event Action<Job>? Finished;

    public void Enqueue(Job job)
    {
        Enqueued?.Invoke(job);
        _ = RunAsync(job);
    }

    async Task RunAsync(Job job)
    {
        var lane = _lanes[job.Lane];
        await lane.WaitAsync();
        try { await job.RunAsync(); }
        finally
        {
            lane.Release();
            Finished?.Invoke(job);
        }
    }
}
