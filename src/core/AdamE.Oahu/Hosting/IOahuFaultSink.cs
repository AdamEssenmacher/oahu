namespace AdamE.Oahu.Hosting;

/// <summary>Where an <see cref="OahuLoop"/> reports a fault: a broken rule of the seam, which ends the app.</summary>
/// <remarks>
/// <para>
/// A host implements it to pass each fault on, for example to the app's crash reporter or the platform's log. It
/// only reports: the loop itself posts the end of the app to its thread, through its <see cref="IOahuDispatcher"/>,
/// right after the sink returns.
/// </para>
/// <para>
/// From the moment a fault is raised the loop is faulted. The turn running then finishes the entries already queued,
/// and afterwards the loop takes no more work and runs no more turns, even if the platform lets the app carry on after
/// the end of the app.
/// </para>
/// <para>
/// <see cref="ReportFault"/> may be called on any thread, and on several at the same time, so an implementation must
/// be thread-safe.
/// </para>
/// </remarks>
public interface IOahuFaultSink
{
    /// <summary>Reports a fault.</summary>
    /// <remarks>
    /// <para>
    /// The loop calls it once for each fault, on the thread where the fault was raised, which may be a thread other
    /// than the loop's. The app is ending, so the sink records the fault and returns; it must not end the app itself
    /// and must not throw. If it throws, the loop still ends the app: the end of the app it posts then has an
    /// <see cref="AggregateException"/> of the fault and what the sink threw as its inner exception.
    /// </para>
    /// </remarks>
    /// <param name="exception">The fault: an <see cref="OahuFaultException"/> for a rule the loop checks, or the exception
    /// that broke a rule, such as one thrown by an observer.</param>
    void ReportFault(Exception exception);
}
