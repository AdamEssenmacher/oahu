# Contracts

A contract is what an app author writes to connect a view model to its native screen. This page describes the vocabulary a contract is written in and the rules a contract follows. The [wire format](wire-format.md) gives the bytes of the values a contract carries, and the [architecture](architecture.md) describes how the rest of Oahu uses a contract.

> **Status:** the vocabulary is arriving in parts. What exists now, in the `AdamE.Oahu` namespace: `[OahuContract]`, the three stream interfaces `IOahuState<T>`, `IOahuEditable<T>` and `IOahuEvents<T>`, and the no-value type `OahuUnit`. Later pull requests add keyed lists (`IOahuKeyedList<T>`, with their rows and list operations), methods and their rules, and structural equality for contract values. Nothing implements the stream interfaces yet, nothing checks a contract and nothing is generated from one: the sources, the analyzer and the generators arrive in later milestones of the [roadmap](roadmap.md).

## What a contract is

A contract is a C# interface marked `[OahuContract]`. It is the C# side of one native screen: it declares what the screen can observe and what it can ask for, and nothing else. A view model, an ordinary C# class, implements it. Oahu generates the code on both sides of the seam from the contract, so the screen and the view model never meet directly.

A contract has **streams**, which carry values from the view model to the screen, and **members**, which the screen calls. A stream is a get-only property whose type is one of the stream interfaces:

```csharp
IOahuState<string> FileName { get; }
```

The vocabulary is in the `AdamE.Oahu` namespace, the one an app author imports. The `AdamE.Oahu.Wire` namespace is for generated code.

## State and events

The vocabulary has three kinds of stream so far: state and events, described here, and [editable values](#editable-values), which are states the screen can also change. The rules below are requirements on every implementation of the stream interfaces. Oahu's own sources keep them, and an implementation of your own, such as an adapter or a hand-written state, must keep them too, because the exporter and other observers rely on them.

**State** (`IOahuState<T>`) is a value that always has a current one: a title, a count, the status of a download. Use it for anything the screen draws from.

- `Current` is the value now. When the view model changes it, `Current` changes at once.
- A new observer receives the current value at once, during `Subscribe`. Then, in later delivery turns, it receives the value current at that time, when it differs from the last one that observer received. Contract values compare by structure. For a list, that takes Oahu's own comparison, which arrives with structural equality in a later part of the vocabulary: the default comparer compares two lists by reference, so an implementation that uses it delivers an equal list again.
- Observers hear of a change in a later delivery turn, never during the call that changed it: no observer runs while that call is on the stack. A turn can run before the method that made the change has finished, when that method is waiting at an `await`. So several changes before a turn can arrive as one value, and a change and its reversal as none.
- A state never completes and never fails.

**Events** (`IOahuEvents<T>`) are occurrences with no current value: a message to show once, a sound to play. Use them for what happens, not for what is.

- Nothing is replayed: a new observer receives only occurrences published after it subscribed.
- Each occurrence is delivered once to each observer that was subscribed when it was published and is still subscribed when it is delivered.
- Events are never merged: two occurrences are two deliveries, even when their values are equal.
- Like a state, events reach observers in a later delivery turn, and they never complete or fail.

Some events have nothing to say but that they happened: the document was saved, so play a sound. Their type is the no-value type, `OahuUnit`, whose one value is `OahuUnit.Value`:

```csharp
IOahuEvents<OahuUnit> Saved { get; }
```

Each occurrence is still delivered once, and two are still two deliveries, though every value of `OahuUnit` is equal. It may be used nowhere else, as [No value](#no-value) says.

State and events both follow these rules too:

- **Disposal.** Disposing a subscription stops all delivery to it at once, even later in a delivery turn that is already running, and even when the observer disposes it during its own callback. Disposing it again does nothing.
- **One thread.** A stream is used only on the UI thread: `Current` is read there, observers subscribe and dispose their subscriptions there, and values are delivered there. Changing a state's value or publishing an occurrence on another thread is a fault, as the [architecture](architecture.md#invariants) says. Reading `Current`, subscribing or disposing a subscription on another thread is a mistake too; whether Oahu reports it as a fault is specified with the sources, so an implementation must not rely on it being allowed.
- **Observer exceptions.** An exception thrown by an observer is a fault, as the [architecture](architecture.md#other-operations) says. The details arrive with the sources.

The [architecture](architecture.md#the-path-of-one-change) describes delivery turns and the loop that runs them.

## Editable values

An **editable value** (`IOahuEditable<T>`) is a state that the screen can also change: the text of a field, the position of a slider, a selected option. The view model owns the value. The screen proposes a new one, and the view model decides what the value becomes.

These rules, like those above, are requirements on every implementation:

- **It is a state.** Everything above that applies to a state applies to an editable: `Current` is the value now, a new observer receives it during `Subscribe`, later changes arrive in later delivery turns and only when they differ, disposal stops delivery at once, it never completes or fails, and it is used only on the UI thread. Anything that takes a state can take an editable.
- **An edit is a proposal.** The screen calls `Edit(value)`. The implementation may accept the value, change it (trim text, clamp a number to a range) or refuse it, and it must settle on one of these before `Edit` returns. When `Edit` returns, `Current` is the result.
- **What an edit changes.** If the settled value differs from the value held before the call, the edit is a change to the state and is delivered under the state's rules, merged with any other changes before the next delivery turn. If it is the same, the edit changes nothing, whatever value was proposed. That is what a refusal looks like, and also a change that lands back on the value held before, such as text cut down to a length it already had. "Differs" and "the same" follow the state's rule: contract values compare by structure. How lists and doubles compare, such as 0.0 and -0.0, or NaN, is defined with structural equality, a later part of the vocabulary.
- **Settling is not delivering.** No observer runs while `Edit` is on the stack. Delivering the result is the exporter's work, as below, not the implementation's.
- **Settle now.** The result is what `Current` holds when `Edit` returns, so an edit cannot settle later, after an `await`, a timer or a posted callback. Work that must be asynchronous, such as checking a name with a server, is done another way: settle now on what is known, and report the outcome through another stream, such as a state that holds an error message. A later change the view model makes in its own way is a new change, not the edit's result.
- **One thread.** `Edit` is called on the UI thread. Calling it on another thread is a fault, even if the edit would be refused.
- **Exceptions.** Whether an exception thrown by `Edit` is a failure or a fault is not decided yet: it is specified with the exporter, and an implementation must not rely on either. After `Edit` throws, `Current` must still be a value the implementation could have settled on.
- **Two ways in.** `Edit` is how the screen changes the value. The view model has its own way to set the value, and both go through the same rules: a value equal to the current one changes nothing.

What the native model and the exporter do, which is not the implementation's work: the screen does not take an edited value for itself, and shows only what the view model publishes. The exporter delivers the result of an edit before the outermost native call returns. An edit the screen makes while it is receiving a message is delivered later in the delivery turn already running, as the [architecture](architecture.md#the-path-of-one-change) says. An edit that changes nothing leaves the screen showing the value it already had.

An editable's value type is any type a contract value may have, except `OahuUnit`, as [No value](#no-value) says.

`IOahuEditable<T>` derives from `IOahuState<T>`, but its `T` is not covariant, because the value is proposed as well as read. So an `IOahuEditable<string>` can be used as an `IOahuState<string>` or an `IOahuState<object>`, but not as an `IOahuEditable<object>`.

An editable also gives the contract one member: its edit, which the screen calls. [Order](#order) says how streams and methods are numbered.

A screen that renames a document:

```csharp
[OahuContract]
public interface IRenameViewModel
{
    IOahuEditable<string> Title { get; }
}
```

The screen binds a text field to the title. The model has the title from the moment the screen subscribes, like any state. When the user types, the model does not take the new text for itself: it sends the edit, and shows only what the view model publishes. Suppose the view model trims spaces from the end of a title. The user types "Plan" and the view model accepts it, so the field shows "Plan". The user types a space: the view model settles on "Plan", the value it already had, so the edit changes nothing and the field goes on showing "Plan". What the view model publishes reaches the screen before the outermost native call returns; an edit the screen makes while it is receiving a message is delivered later in the delivery turn already running. The [architecture](architecture.md#other-operations) says this, and also describes how the exporter checks the settlement.

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
| [No value](wire-format.md#the-no-value-type) | `OahuUnit`, only as the whole type of an events stream, as [No value](#no-value) says |

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
- **No value.** `OahuUnit` is only ever the whole type of an events stream, as [No value](#no-value) says.

## No value

`OahuUnit` is the type of a stream that carries no value: `IOahuEvents<OahuUnit>`. That is the only place a contract may use it. It is never:

- the type of any stream but events: a state (`IOahuState<OahuUnit>`), an editable value or a keyed list;
- nullable (`OahuUnit?`), even as an events stream's type: `IOahuEvents<OahuUnit?>` is not allowed;
- a list's item type (`IReadOnlyList<OahuUnit>`), a record's field or a union case's field;
- a method's argument.

Use something else in those places. A method with no argument declares no parameter, not an `OahuUnit` one. A state always has a value, and that value is worth a real type: a `bool`, an enum or a record. `IOahuEvents<OahuUnit?>` would say no more than `IOahuEvents<OahuUnit>`, so write that instead. Where a value with nothing in it is wanted elsewhere, declare a record with no fields, such as `public sealed record Empty();`. It is no bytes too, but it is an ordinary record, allowed wherever a record is except as a list's item type, and its name says what it means. The rule on `OahuUnit` catches accidents; a record with no fields is a deliberate choice.

A list of things that have no content can use neither `OahuUnit` nor a record with no fields as its item type. Carry how many there are as an `int`, use a list of a nullable empty record (`IReadOnlyList<Empty?>`), whose items are each one byte, or give the items a real field.

Like the rules for values, this rule is not enforced yet; the analyzer will enforce it.

`OahuUnit` is not a record value: it has no fields and is not positional, and it adds no nesting depth. On the wire its value is no bytes, so each occurrence of an `IOahuEvents<OahuUnit>` is an empty message, as [The no-value type](wire-format.md#the-no-value-type) says.

## Order

A contract's streams are numbered in the order they are declared, and its methods are numbered separately, in the order they are declared. An editable is a stream and also contributes one member, its edit; how an editable's edit is numbered among the methods is defined with the contract model. How a contract split across partial declarations, or one that inherits from another interface, is ordered is defined with the contract model, in a later milestone. The wire format writes record fields, enum values and union cases by their position too. So order matters: reordering changes what the bytes mean. Both sides of an app must be built from the same contract.
