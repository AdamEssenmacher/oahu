# Contracts

A contract is what an app author writes to connect a view model to its native screen. This page describes the vocabulary a contract is written in and the rules a contract follows. The [wire format](wire-format.md) gives the bytes of the values a contract carries, and the [architecture](architecture.md) describes how the rest of Oahu uses a contract.

> **Status:** the vocabulary is arriving in parts. What exists now, in the `AdamE.Oahu` namespace: `[OahuContract]`, and the two stream interfaces `IOahuState<T>` and `IOahuEvents<T>`. Later pull requests add editable values (`IOahuEditable<T>`), keyed lists (`IOahuKeyedList<T>`, with their rows and list operations), methods and their rules, the no-value type `OahuUnit`, and structural equality for contract values. Nothing implements the stream interfaces yet, nothing checks a contract and nothing is generated from one: the sources, the analyzer and the generators arrive in later milestones of the [roadmap](roadmap.md).

## What a contract is

A contract is a C# interface marked `[OahuContract]`. It is the C# side of one native screen: it declares what the screen can observe and what it can ask for, and nothing else. A view model, an ordinary C# class, implements it. Oahu generates the code on both sides of the seam from the contract, so the screen and the view model never meet directly.

A contract has **streams**, which carry values from the view model to the screen, and **members**, which the screen calls. A stream is a get-only property whose type is one of the stream interfaces:

```csharp
IOahuState<string> FileName { get; }
```

The vocabulary is in the `AdamE.Oahu` namespace, the one an app author imports. The `AdamE.Oahu.Wire` namespace is for generated code.

## State and events

The vocabulary has two kinds of stream so far. The rules below are requirements on every implementation of the stream interfaces. Oahu's own sources keep them, and an implementation of your own, such as an adapter or a hand-written state, must keep them too, because the exporter and other observers rely on them.

**State** (`IOahuState<T>`) is a value that always has a current one: a title, a count, the status of a download. Use it for anything the screen draws from.

- `Current` is the value now. When the view model changes it, `Current` changes at once.
- A new observer receives the current value at once, during `Subscribe`. Then, in later delivery turns, it receives the value current at that time, when it differs from the last one that observer received. Contract values compare by structure.
- Observers hear of a change in a later delivery turn, not while the code that changed it is running. So several changes before a turn can arrive as one value, and a change and its reversal as none.
- A state never completes and never fails.

**Events** (`IOahuEvents<T>`) are occurrences with no current value: a message to show once, a sound to play. Use them for what happens, not for what is.

- Nothing is replayed: a new observer receives only occurrences published after it subscribed.
- Each occurrence is delivered once to each observer that was subscribed when it was published and is still subscribed when it is delivered.
- Events are never merged: two occurrences are two deliveries, even when their values are equal.
- Like a state, events reach observers in a later delivery turn, and they never complete or fail.

Both kinds of stream follow these rules too:

- **Disposal.** Disposing a subscription stops all delivery to it at once, even later in a delivery turn that is already running, and even when the observer disposes it during its own callback. Disposing it again does nothing.
- **One thread.** A stream is used only on the UI thread: `Current` is read there, observers subscribe and dispose their subscriptions there, and values are delivered there. Using a stream on another thread is a mistake. How Oahu reports it is specified with the sources; the [architecture](architecture.md#invariants) already says that a change made off the UI thread is a fault.
- **Observer exceptions.** An exception thrown by an observer is a fault, as the [architecture](architecture.md#other-operations) says. The details arrive with the sources.

The [architecture](architecture.md#the-path-of-one-change) describes delivery turns and the loop that runs them.

## An example

A screen that shows a download:

```csharp
[OahuContract]
public interface IDownloadViewModel
{
    IOahuState<string> FileName { get; }
    IOahuState<DownloadStatus> Status { get; }
    IOahuEvents<string> Messages { get; }
}

public abstract record DownloadStatus
{
    public sealed record Waiting() : DownloadStatus;
    public sealed record Running(double Fraction) : DownloadStatus;
    public sealed record Failed(string Reason) : DownloadStatus;
    public sealed record Done(TimeSpan Took) : DownloadStatus;
}
```

`DownloadStatus` is a union: its value is one of four cases, each a record.

The screen sees the contract through a generated native model. The model has the file name and the status from the moment the screen subscribes, before it is first drawn, because a state's current value arrives during the subscribe call. The screen draws from them: a progress bar while the status is `Running`, the reason when it is `Failed`. When the view model changes the status, the model changes in a later delivery turn, and the screen redraws. If the view model sets the same status again, nothing reaches the screen.

The messages are different. The screen receives each one as it is published and shows it, for example as a banner that goes away. A message published before the screen subscribed never reaches it, and the same message published twice is shown twice.

## Value types

A stream carries values of these types. Each links to the section of the wire format that gives its bytes.

| Type | In C# |
| --- | --- |
| [Boolean](wire-format.md#booleans) | `bool` |
| [Signed integers](wire-format.md#signed-integers) | `int` and `long` |
| [Double](wire-format.md#doubles) | `double` |
| [String](wire-format.md#strings) | `string` |
| [Instant](wire-format.md#instants-and-durations) | `DateTimeOffset`, to the millisecond, without its offset |
| [Duration](wire-format.md#instants-and-durations) | `TimeSpan` |
| [Enum](wire-format.md#enums) | An enum |
| [Record](wire-format.md#records) | A positional `sealed record` or `readonly record struct` |
| [Union](wire-format.md#unions) | An abstract record with its cases nested in it |
| [Nullable value](wire-format.md#nullable-values) | `T?` of any of these except a nullable value: a nullable value's type is never itself nullable |
| [List](wire-format.md#lists) | `IReadOnlyList<T>` of any of these |

No other numeric type can cross. Values are immutable. Records and unions nest at most 64 deep, as [Nesting depth](wire-format.md#nesting-depth) says.

## Rules for values

The wire format relies on these rules and gives no bytes to a type that breaks one. The analyzer, which comes in a later milestone, will enforce them. Nothing enforces them yet.

- **Records.** All of a record's state is in the parameters of its primary constructor; a property declared in its body can only be computed from them.
- **Unions.** A union is an abstract record with at least one case. Every record nested in it that derives from it is a case and is sealed, and nothing else derives from it.
- **Cases.** A case record is never used on its own, as the type of a field, a list's items, a nullable value, a stream or a method's argument; use the union there, or a separate record with the same fields.
- **Absence.** `?` is the only way a value can be absent. A value whose type has no `?` is never null: a string, a record or a list has no presence byte.
- **Cycles.** No record contains its own type through record fields alone, directly or through other records, with no list, nullable value or union on the way.
- **Enums.** An enum has at least one member, no two members with the same number, and no `[Flags]` attribute.
- **List items.** A list's item type is never a record that is no bytes, which is a record whose fields are all records that are no bytes, such as a record with no fields; a nullable value of such a record is allowed.

## Order

A contract's streams are numbered in the order they are declared, and its members are numbered separately, in the order they are declared. How a contract split across partial declarations, or one that inherits from another interface, is ordered is defined with the contract model, in a later milestone. The wire format writes record fields, enum values and union cases by their position too. So order matters: reordering changes what the bytes mean. Both sides of an app must be built from the same contract.
