using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Dialvert.Core.Jobs;

public enum JobState { Queued, Running, Done, Failed, Canceled }

public enum JobLane { Image, Media, Document }

/// <summary>One unit of work shown in the queue. Work itself is a delegate so engines stay plain methods.</summary>
public sealed class Job : INotifyPropertyChanged
{
    public delegate Task<IReadOnlyList<string>> Work(IProgress<double> progress, CancellationToken ct);

    readonly Work _work;
    readonly CancellationTokenSource _cts = new();
    JobState _state;
    double _progress;
    string? _message;
    IReadOnlyList<string> _outputs = [];

    public Job(string title, JobLane lane, IReadOnlyList<string> inputs, Work work)
    {
        Title = title;
        Lane = lane;
        Inputs = inputs;
        _work = work;
    }

    public string Title { get; }
    public JobLane Lane { get; }
    public IReadOnlyList<string> Inputs { get; }

    public JobState State { get => _state; private set => Set(ref _state, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public IReadOnlyList<string> Outputs { get => _outputs; private set => Set(ref _outputs, value); }

    public void Cancel() => _cts.Cancel();

    public async Task RunAsync()
    {
        if (_cts.IsCancellationRequested) { State = JobState.Canceled; return; }
        State = JobState.Running;
        var progress = new Progress<double>(p => Progress = Math.Clamp(p, 0, 1));
        try
        {
            Outputs = await Task.Run(() => _work(progress, _cts.Token), _cts.Token);
            Progress = 1;
            Message = Summarize();
            State = JobState.Done;
        }
        catch (OperationCanceledException)
        {
            Message = "Canceled";
            State = JobState.Canceled;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            State = JobState.Failed;
        }
    }

    string Summarize()
    {
        if (Outputs.Count == 0) return "Done";
        long before = Inputs.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        long after = Outputs.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        var name = Outputs.Count == 1 ? Path.GetFileName(Outputs[0]) : $"{Outputs.Count} files";
        if (before <= 0) return $"{name} · {CompressSpec.Describe(after)}";
        var delta = (after - before) * 100.0 / before;
        return $"{name} · {CompressSpec.Describe(before)} → {CompressSpec.Describe(after)} ({delta:+0;-0;0} %)";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
