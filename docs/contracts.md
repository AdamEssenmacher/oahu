# Contracts

A contract is what an app author writes to connect a view model to its native screen. This page describes the vocabulary a contract is written in and the rules a contract follows. The [wire format](wire-format.md) gives the bytes of the values a contract carries, and the [architecture](architecture.md) describes how the rest of Oahu uses a contract.

> **Status:** the vocabulary is arriving in parts. What exists now, in the `AdamE.Oahu` namespace: `[OahuContract]`, the four stream interfaces `IOahuState<T>`, `IOahuEditable<T>`, `IOahuEvents<T>` and `IOahuKeyedList<T>`, the row interface `IOahuKeyed`, the list operations `ListOp<T>`, and the no-value type `OahuUnit`. The wire format gives the bytes of a keyed list's batch, in [Keyed-list batches](wire-format.md#keyed-list-batches). Later pull requests add methods and their rules, and structural equality for contract values. Nothing implements the stream interfaces yet, nothing checks a contract and nothing is generated from one: the sources, the analyzer and the generators arrive in later milestones of the [roadmap](roadmap.md).

## What a contract is

A contract is a C# interface marked `[OahuContract]`. It is the C# side of one native screen: it declares what the screen can observe and what it can ask for, and nothing else. A view model, an ordinary C# class, implements it. Oahu generates the code on both sides of the seam from the contract, so the screen and the view model never meet directly.

A contract has **streams**, which carry values from the view model to the screen, and **members**, which the screen calls. A stream is a get-only property whose type is one of the stream interfaces:

```csharp
IOahuState<string> FileName { get; }
```

The vocabulary is in the `AdamE.Oahu` namespace, the one an app author imports. The `AdamE.Oahu.Wire` namespace is for generated code.

## State and events

The vocabulary has four kinds of stream: state and events, described here, [editable values](#editable-values), which are states the screen can also change, and [keyed lists](#keyed-lists), which send a list's changes row by row. The rules below are requirements on every implementation of the stream interfaces. Oahu's own sources keep them, and an implementation of your own, such as an adapter or a hand-written state, must keep them too, because the exporter and other observers rely on them.

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

An editable also gives the contract one member: its edit, which the screen calls. [Order](#order) says how streams and members are numbered.

A screen that renames a document:

```csharp
[OahuContract]
public interface IRenameViewModel
{
    IOahuEditable<string> Title { get; }
}
```

The screen binds a text field to the title. The model has the title from the moment the screen subscribes, like any state. When the user types, the model does not take the new text for itself: it sends the edit, and shows only what the view model publishes. Suppose the view model trims spaces from the end of a title. The user types "Plan" and the view model accepts it, so the field shows "Plan". The user types a space: the view model settles on "Plan", the value it already had, so the edit changes nothing and the field goes on showing "Plan". What the view model publishes reaches the screen before the outermost native call returns; an edit the screen makes while it is receiving a message is delivered later in the delivery turn already running. The [architecture](architecture.md#other-operations) says this, and also describes how the exporter checks the settlement.

## Keyed lists

A **keyed list** (`IOahuKeyedList<T>`) is a list of rows with stable keys, such as the rows of a table or the messages of a chat. It is sent to the screen as operations on the list, not as the list, so that a native list can animate each change and keep each row's identity: its place on screen, its selection, its scroll position.

Use it instead of a state of a list, `IOahuState<IReadOnlyList<T>>`, when the screen shows the items as rows that the user sees come, go and move. A state of a list replaces the whole list: each change delivers the new list, and the screen has only the new list to tell what changed. A keyed list delivers the changes themselves, and each costs what it changes, not the whole list. A state of a list is still right for a small list that the screen draws as one thing, such as the names in a line of text.

These rules, like those above, are requirements on every implementation.

### Rows and keys

A row is a record or a readonly record struct that implements `IOahuKeyed`, whose one member is the row's key, `string Key`. The key is a parameter of the record's primary constructor, so it is one of the row's fields and is written on the wire like any other:

```csharp
public sealed record TaskRow(string Key, string Text, bool Done) : IOahuKeyed;
```

A row can also be a union, if every case has the positional `Key` and passes it, unchanged, to the union's base record:

```csharp
public abstract record BoardRow(string Key) : IOahuKeyed
{
    public sealed record Header(string Key, string Title) : BoardRow(Key);
    public sealed record Card(string Key, string Text) : BoardRow(Key);
}
```

Each case passes its own `Key` parameter to the union's, so that the interface's `Key` is the case's `Key` field. `Card(string Key, string Text) : BoardRow(Text)` is a mistake that the compiler reports only as warning CS8907, an unread parameter. In a project where warnings are not errors it builds, and then `Key` returns the text.

- **A key is the row's identity.** An operation that names a row with a key names the same row as every earlier one with that key. A row that changes its value and keeps its key is the same row, replaced. A row whose key changes is another row: remove the old one and insert the new one.
- **Keys are unique within a list at every moment**: before and after every operation, including between the operations of one batch. A key can be used again once the row that had it is removed, even later in the same batch. A batch that would give two rows one key is not valid, as [Batches](#batches) says.
- **A key is never null, and is well-formed UTF-16**, with no lone surrogate. The wire format writes a lone surrogate as U+FFFD, as [Strings](wire-format.md#strings) says, so two different keys could otherwise arrive as one.
- **Keys compare ordinally**, by their UTF-16 code units, with no culture and no Unicode normalization: two strings that differ only in normalization are two keys. The native runtimes compare keys the same way, so a native list has exactly the rows and the identities that the C# list has.
- **A row has no field named `Id`.** A native row can be identified by its key under the name `id`, as a Swift `Identifiable` is, and a field named `Id` would take that name too.

A note for native runtimes: compare keys by their code units, not with a string equality that does more. Swift's `String` equality treats canonically equivalent strings as equal, so a Swift runtime cannot compare keys as `String` values.

### Operations

An operation, `ListOp<T>`, is one of five cases. An index refers to the list as it is when that operation applies.

| Operation | Does | Valid when |
| --- | --- | --- |
| `Insert(Index, Item)` | Inserts a row so that it ends up at `Index`; the rows from there on move one place later | `Index` is from 0 to the count, inclusive, and the list has no row with the item's key |
| `Remove(Index)` | Removes the row at `Index`; the rows after it move one place earlier | `Index` is from 0 to the count, exclusive |
| `Move(From, To)` | Removes the row at `From`, then inserts it at `To`, so `To` is the row's index afterwards; it is the same row | both are from 0 to the count, exclusive: `From` in the list before the move, `To` in the list after it |
| `Replace(Index, Item)` | Gives the row at `Index` a new value; it is the same row | `Index` is from 0 to the count, exclusive, and the item has the key of the row it replaces |
| `Clear()` | Removes every row | always |

On [a, b, c, d], `Move(0, 2)` gives [b, c, a, d], and `Move(2, 0)` gives [c, a, b, d].

A note for native runtimes: SwiftUI's `move(fromOffsets:toOffset:)` takes the destination as an offset in the list before the removal, which is `To + 1` when `To` is greater than `From`, and `To` otherwise. For `Move(0, 2)` it is offset 3.

An item is never null. A move to its own index and a replace with an equal value change nothing, and are still operations.

Operations compare with C# record equality: two are equal when they are the same case with equal arguments, and a row compares with its type's `Equals`. The default comparer compares a list field of a record by reference, so two operations whose rows are equal by structure but hold different list instances are not equal, and a replace with such a row is not a replace with an equal value. Oahu's own comparison arrives with structural equality in a later part of the vocabulary.

An operation checks nothing when it is created, not even that an index is not negative: whether an index is in range depends on the list it applies to, so whoever applies the operation checks it there.

The five cases are the only ones. `ListOp<T>` is a `closed` record: no type in another assembly can derive from it, and a `switch` that handles the five is complete, with no discard arm. Its type argument is a row, an `IOahuKeyed`. `ListOp<T>` is shaped like a union, but it is not a contract value: a contract never uses it as the type of a stream, a field or an argument. A keyed list carries it.

### Batches

Each notification of a keyed list is a **batch**: a list of operations, `IReadOnlyList<ListOp<T>>`.

- **In order.** The operations of a batch apply one after another. Each index refers to the list as it is after every operation before it in the batch.
- **All together or not at all.** When the view model applies a batch, `Items`, the rows now, changes at once by the whole batch. A batch with an operation that is not valid where it applies changes nothing: `Items` stays as it was and nothing is delivered. Whether such a batch is thrown to its caller or reported as a fault is specified with the sources. A batch with no operations changes nothing and delivers nothing.
- **Replay.** A new observer receives the current rows during `Subscribe`, before it returns, as one batch: a `Clear`, then an `Insert` of each row of `Items`, in order, at indexes 0, 1, 2 and on. For an empty list it is one `Clear`.
- **Delivery.** Then, in later delivery turns, an observer receives every operation applied since its last delivery, or since its replay, in order, as one batch. No operation is dropped, merged with another or reordered, and none reaches it twice: an operation applied before it subscribed is part of its replay. Several batches applied before a turn arrive as one batch, the operations of the first followed by those of the next. A delivered batch is never empty.
- **More than one in a turn.** A delivery turn runs until its queue is empty, as the [architecture](architecture.md#the-path-of-one-change) says. So when the list changes during a turn, after an observer has received its batch, the observer receives those operations as another batch later in the same turn.
- **A delivered batch is a value.** It never changes afterwards, contains no null operation, and may be one list instance shared by several observers.
- **What the observer holds.** Applying every batch an observer has received, in order, to an empty list gives the rows `Items` had after the last operation that observer received.
- **`Items` is a value.** A list that `Items` returned does not change afterwards; a later change gives a new list.
- **Like the other streams.** Observers hear of a change in a later delivery turn, never during the call that changed the list. Disposing a subscription stops all delivery to it at once. A keyed list never completes and never fails. It is used only on the UI thread: `Items` is read there, observers subscribe and dispose there, and batches are delivered there; changing a keyed list on another thread is a fault. An exception thrown by an observer is a fault. [State and events](#state-and-events) gives these rules in full.

The exporter writes each batch, the replay included, as one message, and the native model applies it all together or not at all, so the screen's list is never left half-changed. The wire format's [Keyed-list batches](wire-format.md#keyed-list-batches) gives a batch's bytes.

`IOahuKeyedList<T>` is invariant: a batch is a list of `ListOp<T>`, which is a record, and a class cannot vary in its type argument. So a keyed list of one row type is never a keyed list of another.

### A keyed list on a screen

A screen that lists tasks:

```csharp
[OahuContract]
public interface ITasksViewModel
{
    IOahuKeyedList<TaskRow> Tasks { get; }
}

public sealed record TaskRow(string Key, string Text, bool Done) : IOahuKeyed;
```

The model has the rows from the moment the screen subscribes: say `t0` "Milk", `t1` "Bread" and `t2` "Eggs", which arrive as a `Clear` and three inserts. Now the view model marks Milk done, moves Eggs to the top and adds "Butter" at the end, in three batches before the next delivery turn:

```csharp
IReadOnlyList<ListOp<TaskRow>> markDone = [new ListOp<TaskRow>.Replace(0, new TaskRow("t0", "Milk", true))];
IReadOnlyList<ListOp<TaskRow>> eggsFirst = [new ListOp<TaskRow>.Move(2, 0)];
IReadOnlyList<ListOp<TaskRow>> addButter = [new ListOp<TaskRow>.Insert(3, new TaskRow("t3", "Butter", false))];
```

`Items` changes at each one. In the next delivery turn the screen receives the three operations as one batch, in that order. The `Move` refers to the list after the `Replace`, and the `Insert` to the list after the `Move`, so the screen ends with Eggs, Milk (done), Bread and Butter. The native list redraws Milk in place, animates Eggs to the top and Butter in at the end, and keeps every other row as it was.

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

A keyed list's rows are records or unions, as [Rows and keys](#rows-and-keys) says, and its batches are written as [Keyed-list batches](wire-format.md#keyed-list-batches) says. No other numeric type can cross. Values are immutable. Records and unions nest at most 64 deep, as [Nesting depth](wire-format.md#nesting-depth) says.

## Rules for values

The wire format relies on these rules and gives no bytes to a type that breaks one. The analyzer, which comes in a later milestone, will enforce them. Nothing enforces them yet.

- **Records.** All of a record's state is in the parameters of its primary constructor; a property declared in its body can only be computed from them.
- **Unions.** A union is an abstract record with at least one case. Every record nested in it that derives from it is a case and is sealed, and nothing else derives from it.
- **Cases.** A case record is never used on its own, as the type of a field, a list's items, a nullable value, a stream or a method's argument; use the union there, or a separate record with the same fields.
- **Absence.** `?` is the only way a value can be absent. A value whose type has no `?` is never null: a string, a record or a list has no presence byte.
- **Cycles.** No record contains its own type through record fields alone, directly or through other records, with no list, nullable value or union on the way.
- **Enums.** An enum has at least one member, no two members with the same number, and no `[Flags]` attribute.
- **List items.** A list's item type is never a record that is no bytes, which is a record whose fields are all records that are no bytes, such as a record with no fields; a nullable value of such a record is allowed.
- **Rows.** A keyed list's row type is a record or a union, never nullable, that implements `IOahuKeyed` through a positional `string Key` parameter: on the record, or on every case of the union, which passes it unchanged to the union's base record. The `Key` that `IOahuKeyed` returns is the property of that parameter, not another implementation, so the key the screen sees is the one C# compares. A row has no field named `Id`, as [Rows and keys](#rows-and-keys) says.
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

A contract's streams are numbered in the order they are declared. Its members are numbered separately. A member is a method or an editable's edit, so an editable is a stream and also contributes one member. Methods keep the order they are declared in. Where each edit falls in the members' numbering is not decided here: it is defined with the contract model, and nothing should assume it until then. How a contract split across partial declarations, or one that inherits from another interface, is ordered is defined with the contract model, in a later milestone. The wire format writes record fields, enum values and union cases by their position too. So order matters: reordering changes what the bytes mean. Both sides of an app must be built from the same contract.
