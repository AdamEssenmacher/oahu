namespace AdamE.Oahu;

/// <summary>
/// The exception for a fault: a broken rule of the seam between C# and native code, which ends the app.
/// </summary>
/// <remarks>
/// <para>
/// It is used in three ways. A rule Oahu checks as it is broken, such as using a source on a thread other than its
/// loop's, is reported as one, with a message that says what was wrong and the stack trace of the call that broke the
/// rule. The code that broke the rule is thrown a second one with the same message, so the app ends even if that code
/// catches it; that second one counts as already reported, so letting it go or throwing it again reports nothing
/// more. And after any fault, the end of the app throws one on the loop's thread, in a later pass of the run loop,
/// with the fault as its <see cref="Exception.InnerException"/>.
/// </para>
/// <para>
/// The inner exception is the exception that was reported, which Oahu never throws again, so its stack trace is the
/// one from where the fault was raised. If the fault sink threw when the fault was reported, the inner exception is an
/// <see cref="AggregateException"/> of the fault and what the sink threw.
/// </para>
/// </remarks>
public sealed class OahuFaultException : InvalidOperationException
{
    internal OahuFaultException(string message)
        : base(message)
    {
    }

    internal OahuFaultException(string message, Exception fault)
        : base(message, fault)
    {
    }
}
