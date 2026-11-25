# The loop

The loop is what makes a view model's changes reach its observers later, in order, on the UI thread. This page describes what the loop does and the rules it keeps. It is written for app authors first; the last section is for hosts.

> **Status:** the loop exists: `OahuLoop`, the host interfaces `IOahuDispatcher` and `IOahuFaultSink`, and the exception for a fault, `OahuFaultException`. Nothing uses the loop yet. The sources (`StateSource<T>`, `EventSource<T>`, `EditableSource<T>` and `KeyedListSource<T>`), which queue their deliveries on it, arrive next in milestone 3 of the [roadmap](roadmap.md), with the test loop, which runs a loop in a plain unit test. The exporter, which runs a delivery turn itself before a native call returns, is milestone 4.

## What the loop is

When a view model changes a source, the source's value changes at once, and its observers hear of the change later. The loop is what runs "later". A source that has observers hands the loop an entry, and the loop runs that entry in a **delivery turn**, after the code that made the change has returned. No observer runs while the change is still on the stack.

The [architecture](architecture.md#the-path-of-one-change) shows where the loop sits on the path from a view model to its screen.

## Delivery turns

A delivery turn is one drain of the loop's queue. It runs the entries in the queue until the queue is empty:

- **First in, first out.** Entries run in the order they were queued.
- **Until empty.** An entry queued during a turn, such as one queued by an observer that changes something, runs in that same turn, after the entries already queued. When a turn ends, every change it caused has been delivered. So an entry that always queues another makes a turn that never ends, and keeps the thread busy forever: the run loop never gets another pass. An observer that changes what it observes on every delivery does this.
- **One at a time.** Only one turn runs on a thread. Asking for a turn while one is running does nothing more than let the running one finish the queue. This holds when an observer pumps a nested run loop, such as a modal dialog: a turn that run loop would start finds a turn already running, and does nothing.

What each entry delivers is the business of the source that queued it: a state delivers its current value, a keyed list its operations, events each occurrence. The loop runs the entries in order.

## When a turn runs

A turn runs in one of two ways.

- **Posted.** When the queue goes from empty to holding work, the loop posts a turn to its thread. The turn runs in a later pass of that thread's run loop. If the thread refuses the turn, the loop stops, as [For hosts](#for-hosts) describes. At most one posted turn is pending at a time, so many changes before the next pass are one turn.
- **Drained.** Code inside Oahu on the loop's thread can run a turn at once, without waiting for the posted one. The exporter will do this before a native call returns, so that a screen's own action reaches it before the call ends. A posted turn that then finds the queue empty does nothing.

## One loop per thread

A loop belongs to one thread, and a thread has at most one loop. In an app, the loop's thread is the UI thread, and the host for the platform creates the loop; the hosts arrive in later milestones. There is no loop shared by the whole process, and an app never passes a loop to a source: when the sources arrive, a source uses the loop of the thread it is first subscribed on.

Creating a second loop on a thread whose loop is not disposed throws. Disposing a loop frees its thread for another one, so a test can create a fresh loop and discard it.

A loop can be disposed on any thread, such as the thread an `async` test finishes on. Disposing drops the work still queued, and a turn that is running stops after the entry it is running. A loop disposed on another thread is no longer its own thread's loop: a source on that thread does not find it, and a new loop can be created there.

A disposed loop drops any work it is given, without throwing. So a view model that outlives its window, or a continuation that runs while the app shuts down, can still set a source; no observer hears of it, because none can be reached. A loop that has [faulted](#after-a-fault), or [stopped](#for-hosts) because its thread refused work, drops work in the same way.

## Faults

A **fault** is a broken rule of the seam, as the [architecture](architecture.md#other-operations) says. A fault is never swallowed: it ends the app. The loop raises these faults:

- **An entry that throws.** This is how an exception thrown by an observer becomes a fault. The turn goes on with the next entry, so one broken observer does not stop the others from hearing about their changes.
- **Running a turn off the loop's thread.** Running a delivery turn on another thread is a fault, and the code that tried receives an `OahuFaultException` as well. The sources, when they arrive, apply the same rule to changing a source, subscribing and disposing a subscription.
- **A dispatcher that breaks its rules.** A dispatcher that runs a delivery turn off its thread, or before `TryPost` returns, raises a fault, as [For hosts](#for-hosts) describes.

Each fault is reported once, to the host, on the thread where it was raised. The loop then posts the end of the app to its thread, for the next pass of the run loop. The end of the app throws an `OahuFaultException`. Its message names the fault, and its `InnerException` is the fault itself, with the stack trace from where the fault was raised. So a crash report shows an `OahuFaultException` first, and the cause inside it.

A running turn does not catch the end of the app. If an observer pumps a nested run loop, such as a modal dialog, and the end of the app runs there, it goes on out of the turn and is not reported as a second fault.

When the loop checks a rule, such as the thread, it reports one `OahuFaultException`, with the stack trace of the call that broke the rule, and throws another with the same message to that call. Code that catches the one thrown to it does not stop the app ending: the fault was reported and the end of the app posted before it was thrown. If that code throws it again, or lets it go from an entry, it is not reported a second time.

### After a fault

Once a fault has been raised, the loop is **faulted**. The turn running then finishes the entries already queued, and after it the loop takes no more work and runs no more turns, as if it were stopped. Some platforms let an app mark an unhandled exception as handled and go on running, so an app may go on running after the end of the app is thrown; a faulted loop delivers nothing more to it.

### When the end of the app cannot be posted

The loop ends the app with an ordinary managed exception, so that the platform's handlers for unhandled exceptions, and the crash reporters that use them, see Oahu's message. It does not use `Environment.FailFast` for that, because a fail-fast skips those handlers. That ordinary way is still subject to how the platform treats an unhandled exception on the loop's thread. When it fails, the loop does this:

- **The host's fault sink throws.** The end of the app is posted all the same. Its `InnerException` is then an `AggregateException` of the fault and what the sink threw.
- **The dispatcher does not take the end of the app**, because `TryPost` returns false, throws, or runs it before it returns. The loop ends the process from a new foreground thread that it starts for the purpose. There it raises `AppDomain.UnhandledException` with the end of the app, through `ExceptionHandling.RaiseAppDomainUnhandledExceptionEvent`, so that crash reporters see Oahu's message, and then calls `Environment.FailFast` with it, even if raising the event threw. If `TryPost` threw, the `InnerException` is an `AggregateException` of the fault and what `TryPost` threw. If `TryPost` ran the end of the app, the message says so.
- **That thread cannot be started.** Then the loop calls [`Environment.FailFast`](https://learn.microsoft.com/dotnet/api/system.environment.failfast) at once, passing the end of the app and what starting the thread threw.

On CoreCLR on .NET 11, the new thread was checked in a console app whose `Main` returned at the same moment: the process ended in each case, `AppDomain.UnhandledException` handlers ran, and an app's `ExceptionHandling.SetUnhandledExceptionHandler` that marks exceptions handled did not keep the process running. It has not been checked on the other runtimes.

## Where code after an await resumes

The loop installs no `SynchronizationContext`. Where the code after an `await` in a view model resumes is decided by the context the UI thread has, which the host must make sure of, as [For hosts](#for-hosts) says. On .NET for iOS and Android, the platform's context resumes it on the UI thread. Code that leaves the UI thread, such as code after `ConfigureAwait(false)`, must return to it before it changes a source.

The test loop, which arrives next, installs a context of its own while it runs work, so that a view model's `await` resumes on the test loop's thread.

## For hosts

A host is the Oahu package for one platform. It creates the loop of its UI thread with `new OahuLoop(dispatcher, faultSink)`, on that thread, and gives it two things from the [`AdamE.Oahu.Hosting`](../src/core/AdamE.Oahu/Hosting) namespace:

| Interface | Does | Threads |
| --- | --- | --- |
| `IOahuDispatcher` | `IsOnThread` says whether the caller is on the UI thread. `TryPost` queues an action to run on it in a later pass of its run loop, in the order posted, and says whether it took it. | Both members are called on any thread, so both must be thread-safe. |
| `IOahuFaultSink` | `ReportFault` passes a fault on, for example to the app's crash reporter. It must not end the app itself and must not throw. | Called on the thread where the fault was raised, possibly several at once, so it must be thread-safe. |

`IsOnThread` must be true exactly on the thread the loop was created on, and false on every other.

The host must make sure the UI thread has a `SynchronizationContext` that posts back to the UI thread, because the loop installs none. An app needs it: the code after an `await` in a view model's method must resume on the loop's thread, because changing a source on another thread is a fault.

`TryPost` returns true when the action will run later on the UI thread, exactly once. A `TryPost` that returns true must run the action. The loop posts no other turn while one is pending, so a posted turn that never runs makes the loop stop delivering, and nothing reports it. It returns false when the dispatcher has stopped taking work, and then the action never runs. Platform APIs that post to the UI thread often say whether they took the work, as Android's `Handler.post` and WinUI's `DispatcherQueue.TryEnqueue` do: pass that answer on. An exception thrown by `TryPost` counts as false.

`TryPost` must never run the action before it returns, even when it is called on the UI thread. It always queues. Some platform APIs run the action at once when they are called on the UI thread, such as MAUI's `MainThread.BeginInvokeOnMainThread` and Android's `Activity.RunOnUiThread`, so they cannot be used for `TryPost` as they are. The loop detects a delivery turn that runs inside `TryPost`: it does not run the turn there, because that would deliver during the call that made the change, and it raises a fault whose message names the usual cause. The fault leaves the loop faulted, so the queued work never runs.

An action the dispatcher runs may throw: the end of the app does. The dispatcher must not catch that exception and carry on. It must let the exception end the process as an unhandled exception. Where posted actions run under native frames that a managed exception must not cross, the host catches the exception at that boundary and ends the process there itself. Either way, the loop has already reported the fault to the sink.

A dispatcher that refuses a delivery turn stops the loop. A stopped loop drops its queue and takes no more work, as a disposed one does. That is not a fault, and nothing is reported, because a UI thread that stops taking work is usually an app shutting down. A stopped loop stays its thread's loop until it is disposed. Dispose the loop before the dispatcher stops taking work; a dispatcher that refuses work stops the loop. A fault raised after the loop stopped is still reported, and the loop then ends the process from a new thread, as [above](#when-the-end-of-the-app-cannot-be-posted). So a fault during shutdown still ends the process: while an app shuts down, nothing may change a source off the UI thread.

A host keeps its loop for as long as the UI thread runs Oahu code. It may dispose the loop on any thread. A disposed loop drops the work still queued, does nothing when a turn it posted earlier runs, and drops any more work it is given. A fault raised after that still ends the app.
