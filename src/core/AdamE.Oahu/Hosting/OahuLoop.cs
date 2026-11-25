using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace AdamE.Oahu.Hosting;

/// <summary>
/// The loop of one thread: it holds the queue of work that sources hand it, and runs that work in delivery turns on
/// its thread, never during the call that queued it. In an app the thread is the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>One loop per thread.</b> A loop belongs to the thread it is created on, and a thread has at most one loop at a
/// time. A host creates the loop on the thread its <see cref="IOahuDispatcher"/> serves, before anything on that thread
/// needs one, and keeps it for as long as the thread runs Oahu code: in an app, for the life of the app. Creating a
/// second loop on a thread whose loop is not disposed throws. Disposing a loop frees its thread for another, which is
/// how a test creates and discards many loops on one thread. There is no loop shared by the process: a source uses
/// the loop of the thread it is first subscribed on.
/// </para>
/// <para>
/// <b>Delivery turns.</b> A delivery turn is one drain of the loop's queue: it runs the queued entries in the order
/// they were queued, until the queue is empty, so an entry queued during a turn runs in that same turn. When the queue
/// goes from empty to holding work, the loop posts a turn to its thread through
/// <see cref="IOahuDispatcher.TryPost"/>; at most one posted turn is pending at a time, and it runs in a later pass of
/// the thread's run loop. Code on the loop's thread inside Oahu can also run a turn at once, without waiting for the
/// posted one, which then finds the queue empty and does nothing. Only one turn runs at a time: asking for a turn
/// while one is running only lets the running one finish the queue.
/// </para>
/// <para>
/// <b>Stopping.</b> A dispatcher that refuses a delivery turn, by returning <see langword="false"/> from
/// <see cref="IOahuDispatcher.TryPost"/> or throwing from it, has stopped taking work, as a UI thread does while an
/// app shuts down. The loop then stops: it drops its queue and takes no more work, as if it were disposed. That is not
/// a fault, and nothing is reported. A stopped loop stays its thread's loop until it is disposed.
/// </para>
/// <para>
/// <b>Faults.</b> A fault is a broken rule of the seam, and it ends the app. An entry that throws is a fault, and the
/// turn goes on with the next entry. Asking for a turn off the loop's thread is a fault that also throws an
/// <see cref="OahuFaultException"/> to the caller, and the sources keep the same rule for their own use. A dispatcher
/// that runs a delivery turn before <see cref="IOahuDispatcher.TryPost"/> returns, or off its thread, is a fault too.
/// The loop reports each fault once to its <see cref="IOahuFaultSink"/>, on the thread where it was raised, and then
/// posts the end of the app to its thread: an action that throws an <see cref="OahuFaultException"/> whose
/// <see cref="Exception.InnerException"/> is the fault, with that exception's stack trace untouched. If the sink
/// throws, the inner exception is an <see cref="AggregateException"/> of the fault and what the sink threw, and the
/// end of the app is posted all the same. A running turn does not catch the end of the app as a new fault: if an
/// entry pumps a nested run loop, such as a modal dialog, and the end of the app runs and throws there, it goes on out
/// of the turn.
/// </para>
/// <para>
/// If the end of the app cannot be posted, because the dispatcher refuses it, throws, or runs it inside
/// <see cref="IOahuDispatcher.TryPost"/>, the loop ends the process from a new foreground thread: it raises
/// <see cref="AppDomain.UnhandledException"/> with the end of the app, through
/// <see cref="ExceptionHandling.RaiseAppDomainUnhandledExceptionEvent(object)"/>, so that crash reporters see Oahu's
/// message, and then calls <see cref="Environment.FailFast(string, Exception)"/>, even if raising the event threw. An
/// app's own <see cref="ExceptionHandling.SetUnhandledExceptionHandler"/> cannot keep the process running there. If
/// that thread cannot be started, the loop calls <see cref="Environment.FailFast(string, Exception)"/> at once. So a
/// fault on a loop that has stopped, as it does while an app shuts down, still ends the process.
/// </para>
/// <para>
/// Once a fault has been raised on a loop, the loop is faulted: the turn running then finishes the entries already
/// queued, and afterwards the loop takes no more work and runs no more turns, as if it were stopped. So an app whose
/// platform marks the end of the app as handled does not go on delivering through a loop that broke a rule.
/// </para>
/// <para>
/// <b>Threads.</b> The constructor is called on the loop's thread, and <see cref="Dispose"/> on any thread. Everything
/// else the loop does runs on its thread too, except checking the thread and reporting a fault, which can happen on
/// any thread: there the loop calls only the dispatcher's <see cref="IOahuDispatcher.IsOnThread"/> and
/// <see cref="IOahuDispatcher.TryPost"/> and the sink's <see cref="IOahuFaultSink.ReportFault"/>, starts a thread to
/// end the process if the end of the app cannot be posted, and takes no lock of its own.
/// </para>
/// <para>
/// The loop installs no <see cref="SynchronizationContext"/>. Where code after an <see langword="await"/> resumes is
/// decided by the context the thread has, which the host must provide, as <see cref="IOahuDispatcher"/> says.
/// </para>
/// </remarks>
public sealed class OahuLoop : IDisposable
{
    private const string DrainedOffThreadMessage =
        "An Oahu loop was asked to run a delivery turn off its thread. Only code on the loop's thread can run one.";

    private const string PostedTurnOffThreadMessage =
        "The dispatcher ran an Oahu delivery turn off the loop's thread. IOahuDispatcher.TryPost must run each " +
        "action on the dispatcher's thread.";

    private const string PostedTurnRanInsideTryPostMessage =
        "The dispatcher ran an Oahu delivery turn before IOahuDispatcher.TryPost returned. TryPost must always queue " +
        "the action for a later pass of the run loop, even when it is called on the dispatcher's thread. The usual " +
        "cause is a platform API that runs the action at once when called on the UI thread, such as MAUI's " +
        "MainThread.BeginInvokeOnMainThread or Android's Activity.RunOnUiThread.";

    private const string LastResortThreadName = "Oahu fault";

    // The bits of _state. Any bit set means the loop takes no more work and runs no more turns.
    private const int Stopped = 1;
    private const int Faulted = 2;
    private const int Disposed = 4;

    // The values of the entries in _reported: a fault, or an end of the app this loop made, which counts as reported
    // too. Static objects, so that a reported exception does not keep a loop alive.
    private static readonly object s_reportedMark = new();
    private static readonly object s_endOfAppMark = new();

    // The loop of the calling thread, if it has one: the only state the loop keeps outside its instances. It may be a
    // loop disposed from another thread, which ForCurrentThread and the constructor treat as none.
    [ThreadStatic]
    private static OahuLoop? t_current;

    private readonly IOahuDispatcher _dispatcher;
    private readonly IOahuFaultSink _faultSink;

    // The faults already reported, so that a fault is reported once however many times it is raised. Weak, so that a
    // reported exception is not kept alive; safe to use from any thread.
    private readonly ConditionalWeakTable<Exception, object> _reported = new();

    // The entries waiting for a turn. Its array is reused, so queuing allocates nothing once it has grown. The loop's
    // thread only.
    private readonly Queue<IOahuLoopEntry> _queue = new();

    // Cached, so that posting a turn allocates no delegate.
    private readonly Action _postedTurn;

    // Stopped, Faulted and Disposed. Read and written on any thread, with Volatile and Interlocked.
    private int _state;

    // Read and written on the loop's thread only.
    private bool _turnPosted;
    private bool _turnRunning;
    private bool _posting;

    /// <summary>Creates the loop of the calling thread.</summary>
    /// <remarks>
    /// <para>
    /// Call it on the thread <paramref name="dispatcher"/> serves. The loop belongs to that thread, and is the
    /// thread's loop until it is disposed. A loop on this thread that was disposed, even from another thread, no longer
    /// counts.
    /// </para>
    /// <para>
    /// The loop calls <paramref name="dispatcher"/> and <paramref name="faultSink"/> as their documentation says, and
    /// keeps them for its lifetime. One object may implement both.
    /// </para>
    /// </remarks>
    /// <param name="dispatcher">The thread the loop serves, and where it posts its turns and the end of the app.</param>
    /// <param name="faultSink">Where the loop reports faults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/> or <paramref name="faultSink"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The calling thread already has a loop that is not disposed, or it is not the thread
    /// <paramref name="dispatcher"/> serves.
    /// </exception>
    public OahuLoop(IOahuDispatcher dispatcher, IOahuFaultSink faultSink)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(faultSink);

        if (ForCurrentThread is not null)
        {
            throw new InvalidOperationException(
                "This thread already has an Oahu loop. A thread has at most one loop: dispose it before creating " +
                "another.");
        }

        if (!dispatcher.IsOnThread)
        {
            throw new InvalidOperationException(
                "An Oahu loop must be created on the thread its dispatcher serves, and the dispatcher's IsOnThread is " +
                "false on this thread.");
        }

        _dispatcher = dispatcher;
        _faultSink = faultSink;
        _postedTurn = RunPostedTurn;
        t_current = this;
    }

    /// <summary>
    /// The loop of the calling thread, or <see langword="null"/> when the thread has none. A loop that was disposed,
    /// even from another thread, is none, and its registration is cleared. A stopped or faulted loop that is not
    /// disposed is still the thread's loop, and drops what it is given.
    /// </summary>
    internal static OahuLoop? ForCurrentThread
    {
        get
        {
            var loop = t_current;
            if (loop is null || !loop.Is(Disposed))
                return loop;

            loop.Unregister();
            return null;
        }
    }

    /// <summary>Whether the caller is on the loop's thread. Any thread.</summary>
    internal bool IsOnThread => _dispatcher.IsOnThread;

    /// <summary>Whether a delivery turn of this loop is running now. The loop's thread only.</summary>
    internal bool IsTurnRunning => _turnRunning;

    /// <summary>Whether the loop stopped because its dispatcher refused a delivery turn. Any thread.</summary>
    internal bool IsStopped => Is(Stopped);

    /// <summary>Whether a fault has been raised on the loop. Any thread.</summary>
    internal bool IsFaulted => Is(Faulted);

    /// <summary>
    /// How the loop ends the process from a new thread when the end of the app cannot be posted. The default starts
    /// the thread that <see cref="CreateLastResortThread"/> makes. Only the core's tests replace it, to see that it was
    /// called.
    /// </summary>
    internal Action<OahuFaultException> EndOnNewThread { get; set; } = EndOnNewThreadByDefault;

    /// <summary>
    /// How the loop ends the process when it cannot start the thread for <see cref="EndOnNewThread"/>, or when
    /// reporting a fault went wrong in a way it does not expect: <see cref="Environment.FailFast(string, Exception)"/>.
    /// Only the core's tests replace it, to see that it was called.
    /// </summary>
    internal Action<string, Exception> FailFast { get; set; } = Environment.FailFast;

    /// <summary>
    /// Ends the loop, and frees its thread for another loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It may be called on any thread. The work still queued is dropped and never runs. Afterwards a turn posted before
    /// the loop was disposed does nothing when it runs, the loop drops any work it is given, and the thread has no loop
    /// until another is created on it. A turn running when the loop is disposed stops after the entry it is running. A
    /// fault raised afterwards, such as one from another thread, is still reported and still ends the app.
    /// </para>
    /// <para>
    /// Called on another thread, it cannot touch the loop's thread: the queue is dropped when the turn running then
    /// stops, or else the next time code on the loop's thread queues work on the loop, runs a turn, runs a turn posted
    /// earlier, or looks for its loop; and the thread's registration is cleared the next time code on that thread
    /// looks for its loop or creates one.
    /// </para>
    /// <para>Disposing a loop again does nothing.</para>
    /// </remarks>
    public void Dispose()
    {
        Interlocked.Or(ref _state, Disposed);
        if (ReferenceEquals(t_current, this))
            Unregister();
    }

    /// <summary>
    /// Queues an entry for a delivery turn, and posts a turn if none is pending or running. The loop's thread only.
    /// </summary>
    /// <remarks>
    /// The entry runs in a later turn, never during this call. Queuing an entry twice runs it twice. On a loop that is
    /// disposed, stopped or faulted, the entry is dropped and nothing is thrown. A dispatcher that refuses the turn
    /// stops the loop, which drops the entry and the rest of its queue.
    /// </remarks>
    internal void Enqueue(IOahuLoopEntry entry)
    {
        Debug.Assert(_dispatcher.IsOnThread, "Callers check the thread before they change anything.");

        // Dropped, not thrown: the caller may be a view model that outlived its screen, or a continuation that runs
        // while the app shuts down, and no observer can be reached. The queue of a faulted loop is left alone, because
        // a turn running on it still finishes what is queued; a disposed loop's queue goes, as Dispose says.
        if (!TakesWork)
        {
            if (Is(Disposed))
                _queue.Clear();
            return;
        }

        _queue.Enqueue(entry);
        if (_turnPosted || _turnRunning)
            return;

        // Set before posting, so that a dispatcher that runs the turn inside TryPost still finds it set. _posting is
        // how the posted turn knows it was run inside TryPost.
        _turnPosted = true;
        _posting = true;
        bool accepted;
        try
        {
            accepted = _dispatcher.TryPost(_postedTurn);
        }
        catch (Exception)
        {
            accepted = false;
        }
        finally
        {
            _posting = false;
        }

        if (!accepted)
        {
            _turnPosted = false;
            Interlocked.Or(ref _state, Stopped);
        }

        // A refusal stops the loop, and a turn run inside TryPost faults it: either way the queue goes. No turn is
        // running here, since the loop posts only between turns.
        if (!TakesWork)
            _queue.Clear();
    }

    /// <summary>
    /// Runs a delivery turn now, on the loop's thread, without waiting for the posted one, which then finds the queue
    /// empty and does nothing.
    /// </summary>
    /// <remarks>
    /// Inside a running turn it does nothing: the running turn goes on until the queue is empty. On a loop that is
    /// disposed, stopped or faulted it runs nothing.
    /// </remarks>
    /// <exception cref="OahuFaultException">The caller is not on the loop's thread. The fault is reported too.</exception>
    internal void Drain()
    {
        CheckThread(DrainedOffThreadMessage);
        if (_turnRunning)
            return;
        if (!TakesWork)
        {
            _queue.Clear();
            return;
        }

        RunTurn();
    }

    /// <summary>
    /// Raises a fault unless the caller is on the loop's thread. Any thread.
    /// </summary>
    /// <remarks>
    /// It reports one <see cref="OahuFaultException"/> with <paramref name="message"/>, given the stack trace of this
    /// call and never thrown, and throws a second one with the same message to the caller. The second is marked as
    /// reported, so a caller that lets it go, or raises it again, does not report the fault twice.
    /// </remarks>
    /// <exception cref="OahuFaultException">The caller is not on the loop's thread.</exception>
    internal void CheckThread(string message)
    {
        if (_dispatcher.IsOnThread)
            return;

        var reported = new OahuFaultException(message);
        ExceptionDispatchInfo.SetCurrentStackTrace(reported);
        RaiseFault(reported);

        var thrown = new OahuFaultException(message);
        _reported.TryAdd(thrown, s_reportedMark);
        throw thrown;
    }

    /// <summary>
    /// Raises a fault: faults the loop, reports the fault to the sink, unless it was reported already, then posts the
    /// end of the app, or ends the process from a new thread if it cannot be posted. Any thread; it takes no lock of
    /// its own. Nothing escapes it but the <see cref="ArgumentNullException"/> for a null fault.
    /// </summary>
    internal void RaiseFault(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (!_reported.TryAdd(fault, s_reportedMark))
            return;

        Interlocked.Or(ref _state, Faulted);
        try
        {
            Report(fault);
        }
        catch (Exception unexpected)
        {
            // Report catches what the sink and the dispatcher throw, so only something like running out of memory
            // gets here. The fault must still end the app, and the caller must not be thrown something it did not do.
            FailFast(
                "An Oahu fault could not be reported; the inner exceptions are the fault and what went wrong.",
                new AggregateException(fault, unexpected));
        }
    }

    /// <summary>
    /// Makes the thread that the default <see cref="EndOnNewThread"/> starts: a foreground thread, so that the process
    /// cannot exit before it runs, that calls <see cref="EndTheProcess"/>.
    /// </summary>
    internal static Thread CreateLastResortThread(OahuFaultException end) =>
        new(() => EndTheProcess(end, ExceptionHandling.RaiseAppDomainUnhandledExceptionEvent, Environment.FailFast))
        {
            IsBackground = false,
            Name = LastResortThreadName,
        };

    /// <summary>
    /// The body of the last resort thread: raises <see cref="AppDomain.UnhandledException"/> with
    /// <paramref name="end"/>, so that crash reporters see it, then fails fast with it, even if raising the event
    /// threw. The thread passes <see cref="ExceptionHandling.RaiseAppDomainUnhandledExceptionEvent(object)"/> and
    /// <see cref="Environment.FailFast(string, Exception)"/>; only the core's tests pass anything else.
    /// </summary>
    internal static void EndTheProcess(
        OahuFaultException end, Action<object> raiseUnhandledException, Action<string, Exception> failFast)
    {
        try
        {
            raiseUnhandledException(end);
        }
        catch (Exception)
        {
            // The process ends below all the same.
        }

        failFast(end.Message, end);
    }

    /// <summary>The default <see cref="EndOnNewThread"/>: starts the thread <see cref="CreateLastResortThread"/> makes.</summary>
    internal static void EndOnNewThreadByDefault(OahuFaultException end) => CreateLastResortThread(end).Start();

    private bool TakesWork => Volatile.Read(ref _state) == 0;

    private bool Is(int bit) => (Volatile.Read(ref _state) & bit) != 0;

    // Whether the exception is an end of the app this loop posted, which a turn lets go rather than catch: it is there
    // to end the app, and its fault was reported already.
    private bool IsEndOfApp(Exception exception) =>
        _reported.TryGetValue(exception, out var mark) && ReferenceEquals(mark, s_endOfAppMark);

    // The loop's thread only: clears the thread's registration and drops the queue.
    private void Unregister()
    {
        t_current = null;
        _queue.Clear();
    }

    // Kept out of RaiseFault, so that raising a fault already reported allocates nothing.
    private void Report(Exception fault)
    {
        var message = "An Oahu fault ended the app: " + Describe(fault);
        Exception inner = fault;
        try
        {
            _faultSink.ReportFault(fault);
        }
        catch (Exception sinkFailure)
        {
            message += " The fault sink threw when the fault was reported; the inner exceptions are the fault and " +
                "what the sink threw.";
            inner = new AggregateException(fault, sinkFailure);
        }

        PostEndOfApp(message, inner);
    }

    // The fault's type and message. A fault whose Message throws is described by its type alone.
    private static string Describe(Exception fault)
    {
        var type = fault.GetType().FullName ?? fault.GetType().Name;
        try
        {
            return type + ": " + fault.Message;
        }
        catch (Exception)
        {
            return type;
        }
    }

    private void PostEndOfApp(string message, Exception inner)
    {
        // Made here and marked, so that a turn it is thrown through lets it go instead of reporting it as a new fault.
        var posting = new OahuFaultException(message, inner);
        _reported.TryAdd(posting, s_endOfAppMark);

        Exception? postFailure = null;
        bool posted;
        try
        {
            posted = _dispatcher.TryPost(() => throw posting);
        }
        catch (Exception thrown)
        {
            posted = false;
            postFailure = thrown;
        }

        if (posted)
            return;

        OahuFaultException end;
        if (ReferenceEquals(postFailure, posting))
        {
            end = new OahuFaultException(
                message + " The dispatcher ran the end of the app inside IOahuDispatcher.TryPost, which must queue it " +
                "for a later pass of the run loop, so the loop ended the process from a new thread.",
                inner);
        }
        else
        {
            end = new OahuFaultException(
                message + " The dispatcher did not take the end of the app, so the loop ended the process from a new " +
                "thread.",
                postFailure is null ? inner : new AggregateException(inner, postFailure));
        }

        try
        {
            EndOnNewThread(end);
        }
        catch (Exception startFailure)
        {
            FailFast(
                end.Message + " The thread could not be started; the inner exceptions are the end of the app and " +
                "what starting the thread threw.",
                new AggregateException(end, startFailure));
        }
    }

    private void RunPostedTurn()
    {
        if (!_dispatcher.IsOnThread)
        {
            RaiseTraced(PostedTurnOffThreadMessage);
            return;
        }

        if (_posting)
        {
            // Not run here: that would deliver during the call that queued the entry. The fault leaves the loop
            // faulted, so Enqueue drops the queue once TryPost returns.
            RaiseTraced(PostedTurnRanInsideTryPostMessage);
            return;
        }

        _turnPosted = false;
        if (_turnRunning)
            return;
        if (!TakesWork)
        {
            _queue.Clear();
            return;
        }

        RunTurn();
    }

    // Raises a fault the loop detected itself, with the stack trace of the call that detected it.
    private void RaiseTraced(string message)
    {
        var fault = new OahuFaultException(message);
        ExceptionDispatchInfo.SetCurrentStackTrace(fault);
        RaiseFault(fault);
    }

    private void RunTurn()
    {
        _turnRunning = true;
        try
        {
            // A fault lets the turn finish what is queued; disposal stops it after the entry that is running.
            while (!Is(Disposed) && _queue.TryDequeue(out var entry))
            {
                try
                {
                    entry.Run();
                }
                catch (Exception fault) when (!IsEndOfApp(fault))
                {
                    RaiseFault(fault);
                }
            }
        }
        finally
        {
            // Also when this loop's end of the app goes out of the turn: it was posted after a fault, so the loop is
            // faulted, and the queue goes.
            _turnRunning = false;
            if (!TakesWork)
                _queue.Clear();
        }
    }
}
