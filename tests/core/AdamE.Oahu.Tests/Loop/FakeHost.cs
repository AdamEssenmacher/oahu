using System.Runtime.ExceptionServices;
using AdamE.Oahu.Hosting;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

/// <summary>What a <see cref="FakeDispatcher"/> does with an action posted to it.</summary>
internal enum PostMode
{
    /// <summary>Queues it, and returns true.</summary>
    Queues,

    /// <summary>Returns false, as a dispatcher whose run loop stopped does.</summary>
    Refuses,

    /// <summary>Throws.</summary>
    Throws,

    /// <summary>Runs it at once and returns true, as MAUI's BeginInvokeOnMainThread does on the UI thread.</summary>
    RunsInline,
}

/// <summary>
/// A dispatcher for the thread that creates it: it records each posted action, and runs them only when a test asks.
/// </summary>
internal sealed class FakeDispatcher : IOahuDispatcher
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Lock _gate = new();
    private readonly Queue<Action> _posted = new();

    public PostMode Mode { get; set; }

    /// <summary>How many posts were refused or threw.</summary>
    public int Refusals { get; private set; }

    public bool IsOnThread => Environment.CurrentManagedThreadId == _threadId;

    public int PostedCount
    {
        get
        {
            lock (_gate)
                return _posted.Count;
        }
    }

    public bool TryPost(Action action)
    {
        lock (_gate)
        {
            switch (Mode)
            {
                case PostMode.Refuses:
                    Refusals++;
                    return false;
                case PostMode.Throws:
                    Refusals++;
                    throw new InvalidOperationException("The run loop has stopped.");
                case PostMode.Queues:
                    _posted.Enqueue(action);
                    return true;
            }
        }

        action();
        return true;
    }

    /// <summary>
    /// Runs posted actions in order, including those posted while they run, until none is left. Like a run loop, it
    /// stops at an action that throws and lets the exception go; the actions after it stay posted.
    /// </summary>
    public void RunPosted()
    {
        while (TryTake(out var action))
            action();
    }

    /// <summary>Runs the first posted action, on the calling thread.</summary>
    public void RunNext()
    {
        Assert.True(TryTake(out var action), "Nothing is posted.");
        action();
    }

    private bool TryTake(out Action action)
    {
        lock (_gate)
            return _posted.TryDequeue(out action!);
    }
}

/// <summary>A fault sink that records each fault, with the thread it was reported on and its stack trace then.</summary>
internal sealed class RecordingFaultSink : IOahuFaultSink
{
    private readonly Lock _gate = new();
    private readonly List<(Exception Fault, int ThreadId, string? Trace)> _reports = [];

    /// <summary>When set, <see cref="ReportFault"/> records the fault, then throws.</summary>
    public bool Throws { get; set; }

    public IReadOnlyList<Exception> Faults
    {
        get
        {
            lock (_gate)
                return [.. _reports.Select(report => report.Fault)];
        }
    }

    public int ThreadOf(Exception fault)
    {
        lock (_gate)
            return _reports.Single(report => ReferenceEquals(report.Fault, fault)).ThreadId;
    }

    public string? TraceWhenReported(Exception fault)
    {
        lock (_gate)
            return _reports.Single(report => ReferenceEquals(report.Fault, fault)).Trace;
    }

    public void ReportFault(Exception exception)
    {
        lock (_gate)
            _reports.Add((exception, Environment.CurrentManagedThreadId, exception.StackTrace));
        if (Throws)
            throw new InvalidOperationException("The crash reporter is gone.");
    }
}

/// <summary>
/// A loop for the calling thread over a <see cref="FakeDispatcher"/> and a <see cref="RecordingFaultSink"/>, whose
/// last resorts are recorded instead of ending the process. Create it on the test's thread.
/// </summary>
internal sealed class LoopHarness : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<OahuFaultException> _lastResorts = [];
    private readonly List<(string Message, Exception Exception)> _failFasts = [];

    public LoopHarness()
    {
        Loop = new OahuLoop(Dispatcher, Sink);
        Loop.EndOnNewThread = end =>
        {
            lock (_gate)
                _lastResorts.Add(end);
            if (ThreadStartThrows)
                throw new OutOfMemoryException("No thread for you.");
        };
        Loop.FailFast = (message, exception) =>
        {
            lock (_gate)
                _failFasts.Add((message, exception));
        };
    }

    public FakeDispatcher Dispatcher { get; } = new();

    public RecordingFaultSink Sink { get; } = new();

    public OahuLoop Loop { get; }

    /// <summary>When set, the recorded last resort throws, as starting a thread can.</summary>
    public bool ThreadStartThrows { get; set; }

    /// <summary>Each end of the app the loop would have ended the process with from a new thread.</summary>
    public IReadOnlyList<OahuFaultException> LastResorts
    {
        get
        {
            lock (_gate)
                return [.. _lastResorts];
        }
    }

    public IReadOnlyList<(string Message, Exception Exception)> FailFasts
    {
        get
        {
            lock (_gate)
                return [.. _failFasts];
        }
    }

    public void Dispose() => Loop.Dispose();
}

/// <summary>An entry that logs its name when it runs, then does what the test gives it.</summary>
internal sealed class LoggingEntry(string name, List<string> log, Action? then = null) : IOahuLoopEntry
{
    public void Run()
    {
        log.Add(name);
        then?.Invoke();
    }
}

/// <summary>An entry that counts its runs and allocates nothing.</summary>
internal sealed class CountingEntry : IOahuLoopEntry
{
    public int Runs { get; private set; }

    public void Run() => Runs++;
}

internal static class OtherThread
{
    /// <summary>Runs <paramref name="action"/> on a new thread and waits for it; rethrows what it throws.</summary>
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                thrown = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.Start();
        thread.Join();
        thrown?.Throw();
    }

    /// <summary>Runs <paramref name="action"/> on a new thread, waits for it, and returns what it threw, if anything.</summary>
    public static Exception? Catch(Action action)
    {
        try
        {
            Run(action);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
