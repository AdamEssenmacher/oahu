# Wire format

Oahu's C# and native code exchange values as bytes in the format this page specifies. Every implementation of the format must read and write exactly these bytes. In C#, `OahuWriter` writes the format and `OahuReader` reads it, both in the `AdamE.Oahu.Wire` namespace.

This page covers the whole format: messages, variable-length integers, counts, booleans, signed integers, doubles, strings, instants and durations, nullable values, lists, records, the no-value type, enums, unions, the batches of a keyed list and the nesting depth of records and unions.

## Messages

A message is the bytes of one value and nothing else. It has no header, tag, length or schema. Both sides are generated from the same contract, so the reader already knows the type of the value it reads.

A contract is a C# interface marked `[OahuContract]` that declares what a screen observes and calls, and with it the types of the values that cross; [Contracts](contracts.md) describes it. A codec is the code, generated from the contract, that writes and reads the values of one type: in C# it calls `OahuWriter` and `OahuReader`, and each native language has its own.

A reader reads the value from the first byte. When the value is read, the message must have ended. Bytes left over make the message malformed. A message can be empty, when its value is no bytes, as the value of the [no-value type](#the-no-value-type) is.

## Variable-length integers

An unsigned 64-bit integer is written as unsigned LEB128:

- The value is split into groups of 7 bits, starting with the lowest.
- Each group is written as one byte, lowest group first. The group is in the byte's low 7 bits.
- The byte's high bit (`0x80`) is set on every byte except the last.

A writer always writes the shortest form: it stops after the highest group that is not zero. Zero is one byte, `00`. An integer takes 1 to 10 bytes.

| Value | Bytes |
| --- | --- |
| 0 | `00` |
| 127 | `7f` |
| 128 | `80 01` |
| 300 | `ac 02` |
| 16,383 | `ff 7f` |
| 16,384 | `80 80 01` |
| 4,294,967,295 | `ff ff ff ff 0f` |
| 18,446,744,073,709,551,615 (the largest) | `ff ff ff ff ff ff ff ff ff 01` |

A reader accepts a longer form than the shortest, up to 10 bytes. For example, `80 00` and `80 80 80 80 80 80 80 80 80 00` are both read as 0. In a 10-byte integer the last byte holds only the value's top bit, so it must be `00` or `01`. A tenth byte with its high bit set makes the integer longer than 10 bytes, whatever follows, including the end of the input: a reader rejects `80 80 80 80 80 80 80 80 80 80` as too long, not as cut short, and does not read an eleventh byte.

## Counts

A count, such as a length or a number of items, is a whole number from 0 to 2,147,483,647, the largest value of a C# `int`. It is written as a variable-length integer. A writer refuses a negative count.

A reader rejects a count larger than 2,147,483,647, even when the integer itself is well formed.

A reader checks that a count is a valid count first, and only then compares it with the bytes that remain. So a length or count above 2,147,483,647 is rejected as a count that is too large, whatever follows.

## Booleans

A boolean is one byte: `00` for false, `01` for true.

A reader rejects any other byte, such as `02` or `ff`.

## Signed integers

An `int` (32 bits) and a `long` (64 bits) are signed. Each is first zigzag-encoded into an unsigned value, which is then written as a variable-length integer.

Zigzag encoding maps values near zero, negative or positive, to small unsigned values, so they take few bytes. Zero maps to 0, then the values alternate: -1, 1, -2, 2 and so on. A value `n` that is zero or more maps to `2n`; a negative value maps to `-2n - 1`. In bit operations, encode a 64-bit `n` as `(n << 1) ^ (n >> 63)`, where `>>` is an arithmetic right shift (use 31 for a 32-bit value). Decode `z` as `(z >>> 1) ^ -(z & 1)`, where `>>>` is a logical right shift.

| Value | Zigzag value | Bytes |
| --- | --- | --- |
| 0 | 0 | `00` |
| -1 | 1 | `01` |
| 1 | 2 | `02` |
| -2 | 3 | `03` |
| 2 | 4 | `04` |
| 63 | 126 | `7e` |
| -64 | 127 | `7f` |
| 64 | 128 | `80 01` |
| -65 | 129 | `81 01` |
| 2,147,483,647 (the largest `int`) | 4,294,967,294 | `fe ff ff ff 0f` |
| -2,147,483,648 (the smallest `int`) | 4,294,967,295 | `ff ff ff ff 0f` |
| 9,223,372,036,854,775,807 (the largest `long`) | 18,446,744,073,709,551,614 | `fe ff ff ff ff ff ff ff ff 01` |
| -9,223,372,036,854,775,808 (the smallest `long`) | 18,446,744,073,709,551,615 | `ff ff ff ff ff ff ff ff ff 01` |

An `int` and a `long` with the same value have the same bytes. An `int` takes 1 to 5 bytes and a `long` 1 to 10.

A reader of an `int` reads the variable-length integer, then checks its value: the zigzag value must be at most 4,294,967,295. A larger value does not fit in 32 bits, and the reader rejects it. Only the value counts, not the number of bytes. So a longer form than the shortest is accepted when its value fits: `ff ff ff ff 8f 00` is read as -2,147,483,648. `80 80 80 80 10`, whose value is 4,294,967,296, is rejected, as is any longer form of a value that large.

## Doubles

A double is eight bytes: its IEEE 754 binary64 bit pattern, lowest byte first. The byte order is the same on every machine.

| Value | Bytes |
| --- | --- |
| 0 | `00 00 00 00 00 00 00 00` |
| -0 | `00 00 00 00 00 00 00 80` |
| 1.5 | `00 00 00 00 00 00 f8 3f` |
| -2.5 | `00 00 00 00 00 00 04 c0` |
| The largest finite double | `ff ff ff ff ff ff ef 7f` |
| The smallest positive double | `01 00 00 00 00 00 00 00` |
| Positive infinity | `00 00 00 00 00 00 f0 7f` |
| Negative infinity | `00 00 00 00 00 00 f0 ff` |
| A quiet NaN | `00 00 00 00 00 00 f8 7f` |

Every bit pattern is a valid double. A writer writes the bits of the value it is given, and a reader produces the value with the bits it read; neither changes a bit. 0 and -0, and positive and negative infinity, are ordinary values, and their bytes are exact. A reader rejects a double only when fewer than eight bytes remain.

A NaN is written and read with its bits unchanged too. But a language runtime may quieten a signalling NaN before the writer ever sees it. So the format does not promise that a NaN's payload survives from one side of an app to the other, and nothing should depend on it.

## Strings

A string is its length as a count, then that many bytes of UTF-8. The length is the number of UTF-8 bytes, not the number of characters. There is no terminator and no byte-order mark.

A reader reads the length as a count first, and only then compares it with the bytes that remain, as [Counts](#counts) says. So a length above 2,147,483,647 is rejected as a count that is too large, whatever follows.

| Value | Bytes |
| --- | --- |
| The empty string | `00` |
| `Milk` | `04 4d 69 6c 6b` |
| `é` (U+00E9) | `02 c3 a9` |
| `€` (U+20AC) | `03 e2 82 ac` |
| `😀` (U+1F600) | `04 f0 9f 98 80` |
| U+10FFFF, the highest code point | `04 f4 8f bf bf` |
| `a`, U+0000, `b` | `03 61 00 62` |
| U+FEFF | `03 ef bb bf` |
| 128 `a` characters | `80 01`, then 128 bytes of `61` |
| 64 `é` characters | `80 01`, then 64 pairs of `c3 a9` |

U+0000 and U+FEFF are ordinary characters. A reader keeps a U+FEFF at the start of a string; it is not taken as a byte-order mark.

The bytes of a string are always well-formed UTF-8 as the Unicode standard defines it.

A writer writes every string, even one that is not well-formed UTF-16. It writes each lone surrogate as U+FFFD, the bytes `ef bf bd`. A lone surrogate is a high surrogate that no low surrogate follows, or a low surrogate that no high surrogate comes before; a low surrogate followed by a high one is two lone surrogates, written as two U+FFFD. The length counts the three bytes of each U+FFFD. The reason: a string cut in the middle of a character should show one replacement character on the screen, not end the app. The string the reader produces then differs from the writer's string in that character: it has U+FFFD where the writer's string had the lone surrogate.

| Value | Bytes |
| --- | --- |
| A lone high surrogate, U+D800 | `03 ef bf bd` |
| A lone low surrogate, U+DC00 | `03 ef bf bd` |
| U+DC00, then U+D800 | `06 ef bf bd ef bf bd` |
| `a`, U+D800, `b` | `05 61 ef bf bd 62` |

A reader is strict. It rejects a string whose bytes are not well-formed UTF-8 and never replaces anything with U+FFFD. A U+FFFD it reads is the three bytes `ef bf bd` in the input, like any other character.

In C#, `OahuWriter.WriteString` throws `ArgumentNullException` for `null`. A string's UTF-8 bytes, its length and the bytes written before it must fit in the writer's buffer, which holds at most `Array.MaxLength` bytes; beyond that, the writer throws `InvalidOperationException` and writes nothing. That limit is below the largest count, so a string the writer writes always has a valid length.

Notes for implementers. On the JVM, the default conversions do not follow these rules: `String.getBytes(UTF_8)` writes `?` for a lone surrogate, and `String(bytes, UTF_8)` silently replaces ill-formed bytes with U+FFFD. So a Kotlin codec must encode with a `CharsetEncoder` set to replace malformed input with the bytes `ef bf bd`, and decode with a `CharsetDecoder` set to report errors. A Swift string cannot hold a lone surrogate, so a Swift writer has nothing to replace. A Swift reader must not decode through a NUL-terminated C string, because U+0000 is an ordinary character.

## Instants and durations

An instant is a point in time. It is written as a `long`: the number of milliseconds since the Unix epoch, 1970-01-01T00:00:00Z. A negative value is before the epoch.

A writer rounds a fraction of a millisecond down, towards the earlier time. This holds before 1970 too: 0.9999 ms after the epoch is written as 0, and 0.5 ms before it, 1969-12-31T23:59:59.9995Z, is written as -1, not 0.

An instant has no offset or time zone. Two values for the same instant with different offsets, such as 15:00 at +00:00 and 05:00 the next day at +14:00, are written the same. A reader produces the instant in UTC.

The valid instants are those from 0001-01-01T00:00:00Z to 9999-12-31T23:59:59.999Z: from -62,135,596,800,000 to 253,402,300,799,999 milliseconds. A reader rejects a value outside this range, even when the `long` itself is well formed.

| Instant | Milliseconds | Bytes |
| --- | --- | --- |
| 1970-01-01T00:00:00Z, the epoch | 0 | `00` |
| 1970-01-01T00:00:00.001Z | 1 | `02` |
| 1969-12-31T23:59:59.999Z | -1 | `01` |
| 2025-11-02T15:00:00.123Z | 1,762,095,600,123 | `f6 c7 a8 d1 c8 66` |
| 0001-01-01T00:00:00Z, the earliest | -62,135,596,800,000 | `ff df e6 a2 e2 a0 1c` |
| 9999-12-31T23:59:59.999Z, the latest | 253,402,300,799,999 | `fe ef fe a1 fa 9d 73` |

A duration is a length of time. It is written as a `long`: a number of ticks of 100 nanoseconds. A negative value is a negative duration. Every `long` is a valid duration.

| Duration | Ticks | Bytes |
| --- | --- | --- |
| Zero | 0 | `00` |
| One tick | 1 | `02` |
| Minus one tick | -1 | `01` |
| One millisecond | 10,000 | `a0 9c 01` |
| One day | 864,000,000,000 | `80 80 ce a6 a5 32` |
| `TimeSpan.MaxValue` | 9,223,372,036,854,775,807 | `fe ff ff ff ff ff ff ff ff 01` |
| `TimeSpan.MinValue` | -9,223,372,036,854,775,808 | `ff ff ff ff ff ff ff ff ff 01` |

In C#, `OahuWriter.WriteInstant` takes a `DateTimeOffset`, and `OahuReader.ReadInstant` returns one with an offset of zero. Every `DateTimeOffset` is in the valid range, so the writer never writes an instant that a reader rejects. `OahuWriter.WriteDuration` and `OahuReader.ReadDuration` use a `TimeSpan`, whose ticks are exactly the wire value, so every `TimeSpan` round-trips exactly.

The format fixes only the bytes and the valid range. A writer must never write an instant outside the range. What a native writer does with a value its own type can hold but the format cannot, such as a `Date` outside 0001–9999 or an infinite or oversized Kotlin `Duration`, is specified with that codec.

Notes for implementers. A Swift `Date` is a `Double` of seconds since 2001-01-01T00:00:00Z. Across the valid range, neighbouring `Date` values are at most 2^-15 seconds apart, so every millisecond has its own nearest `Date`. To write a `Date`, take the nearest millisecond. If that millisecond converts back to the same `Date`, write it. Otherwise, write the millisecond rounded down. To read, convert the millisecond to the nearest `Date`.

A Kotlin `kotlin.time.Duration` is exact to the nanosecond up to 4,611,686,018,426,999,999 nanoseconds, about ±146 years. Beyond that it holds whole milliseconds, cut towards zero, so up to 9,999 ticks are lost. `TimeSpan.MaxValue` and `TimeSpan.MinValue` arrive 5,807 and 5,808 ticks closer to zero. Two more traps: multiplying ticks by 100 to get nanoseconds overflows a `Long` beyond 92,233,720,368,547,758 ticks, about ±292 years, and `inWholeNanoseconds` saturates beyond about ±292 years. A Kotlin codec that must keep every tick keeps the count as a `Long`.

## Nullable values

A nullable value is one presence byte, then the value when it is present. The presence byte is `00` when the value is absent and `01` when it is present. An absent value has no more bytes. A present value is written as a value of its type would be without the presence byte.

| Value | Bytes |
| --- | --- |
| An absent `int` | `00` |
| The `int` 5 | `01 0a` |
| An absent string | `00` |
| The string `x` | `01 01 78` |
| The empty string | `01 00` |
| The double 1.5 | `01 00 00 00 00 00 00 f8 3f` |

The type of a nullable value is never itself nullable, so a nullable value always has exactly one presence byte.

A reader rejects any presence byte other than `00` and `01`, such as `02` or `ff`.

In C#, `OahuWriter.WritePresence` writes the presence byte and `OahuReader.ReadPresence` reads it. After `true`, the caller writes or reads the value itself.

## Lists

A list is the number of its items as a count, then each item in order. Each item is written as a value of the item type would be. There is nothing between the items and nothing after the last one.

| Value | Bytes |
| --- | --- |
| An empty list | `00` |
| The `int`s 1 and 2 | `02 02 04` |
| The strings `a` and `bc` | `02 01 61 02 62 63` |
| The booleans false, true, false | `03 00 01 00` |
| One double, 1.5 | `01 00 00 00 00 00 00 f8 3f` |
| 128 booleans | `80 01`, then 128 bytes of `00` or `01` |

Every item of a list takes at least one byte. The [contract rules](contracts.md#rules-for-values) forbid an item type whose values are no bytes: a record that is no bytes, as [Records](#records) defines it, and the [no-value type](#the-no-value-type). So a list's count is never larger than the number of bytes left after it, and a reader rejects a count that is. A reader must check this before it makes room for the items. It reads the count as a count first, and only then compares it with the bytes that remain, as [Counts](#counts) says. So a count above 2,147,483,647 is rejected as a count that is too large, whatever follows. The check guarantees that one list's array of items has no more items than there are bytes left after its count, so a message of a few bytes cannot make a reader allocate a list of two billion items. It does not by itself bound the total a reader allocates, because lists nest inside each other and inside records. The [nesting depth](#nesting-depth) bounds that total. Let L be the largest number of lists that can enclose a value with no record or union between them; the type fixes it, such as 1 for a record with a list of itself and 2 for a list of lists. A byte of the input lies inside at most 64 records and unions, so it lies inside at most 65 × L lists: up to L outside the outermost record, and up to L inside each of the 64. The items of one list start at different bytes, so a byte is the first byte of at most one item of each list around it. So a reader creates at most 65 × L list items for each byte of input, whatever the input. What each list item allocates is fixed by its type, so the total a reader allocates is linear in the size of the input. A [keyed-list batch](#keyed-list-batches)'s array of operations likewise has at most one entry for each byte left after its count, so a batch keeps the total linear too.

Lists nest. An item can be any value: a list, a nullable value, a string and so on. An inner list is its own count and items, and an absent item is its presence byte alone.

| Value | Bytes |
| --- | --- |
| A list of three lists of `int`s: empty, then 1, then 2 and 3 | `03 00 01 02 02 04 06` |
| A list of nullable strings: absent, `x`, the empty string, absent | `04 00 01 01 78 01 00 00` |

In C#, a list is an `OahuList<T>`, the list type of a contract value, in the `AdamE.Oahu` namespace, as [Contracts](contracts.md#lists) says. `OahuWriter.WriteList` takes one and `OahuReader.ReadList` returns one. Both take a delegate that writes or reads one item. Pass a method group of a static method or a `static` lambda, so the delegate is created once. `ReadList` returns a list over an array of exactly the count's length, which the list owns, so the array is not copied and nothing else can reach it. It allocates nothing else, apart from what the delegate allocates for the items, and an empty list allocates nothing. `WriteList` allocates nothing, and the list is immutable, so it cannot change while it is written. After the items, it throws `InvalidOperationException` if they wrote fewer bytes than there are items; it does not check each item on its own. It also throws `InvalidOperationException` if the delegate reset the writer. If the delegate throws, or `WriteList` does, the exception propagates. The writer's contents are then unspecified, so reset it before writing another message.

## Records

This page writes a record in a short notation: `record Item(string Name, int Quantity)` stands for the C# positional record `public sealed record Item(string Name, int Quantity);`, or a positional `readonly record struct` with the same parameters, which has the same bytes. The [contract rules](contracts.md#rules-for-values) say which records, enums and unions a contract may declare. This page fixes only their bytes. Where it says the contract rules forbid something, the format gives it no bytes.

A record's fields are the parameters of its own primary constructor, in the order they are declared. Nothing else is a field:

- A property declared in the record's body is not a field and is never written. The contract rules forbid a record whose state is not all in its fields, so such a property can only be computed from them.
- A parameter of a base record's primary constructor is not a field of a derived record. A derived record's fields are its own parameters. When it passes one of them on to its base, as `Header` passes `Key` in [Unions](#unions), the value is written once, as the derived record's field.

A record is its fields in order, each written as a value of its type would be. There is nothing before the first field, between two fields or after the last one. Field names, numbers and types are never written, so a reader must know the record's fields and their order.

For these types:

```text
record Item(string Name, int Quantity, bool Done, double? Weight);
record Empty();
record Tagged(Empty Marker, int Value);
record Pair(Empty First, Empty Second);
```

| Value | Bytes |
| --- | --- |
| `Item("Milk", 2, true, null)` | `04 4d 69 6c 6b 04 01 00` |
| `Item("", -1, false, 1.5)` | `00 01 00 01 00 00 00 00 00 00 f8 3f` |
| `Empty()` | No bytes |
| `Pair(Empty(), Empty())` | No bytes |
| `Tagged(Empty(), -1)` | `01` |
| A present `Empty` as an `Empty?` | `01` |
| A list of `Pair?`: present, absent | `02 01 00` |

A field that is a record is that record's fields, in its place.

A record is no bytes when all its fields are no bytes: a record with no fields, such as `Empty`, or one whose fields are all records that are no bytes, such as `Pair`. Apart from the value of the [no-value type](#the-no-value-type), which is never part of another value, every other value is at least one byte: a boolean, a number, an instant, a duration, a string, an enum, a list, a union and a nullable value each are, and so is a record with at least one field that is. A cycle made only of record fields has no value that ends: a record that holds its own type as a field, or holds it through other records' fields, as in `record A(B Next)` with `record B(A Next)`, with no list, nullable value or union on the way. No value of such a type can be built, and the contract rules forbid the cycle, so this rule never has to decide one. An implementation that works out whether a record is no bytes can rely on that.

So any type can be the item type of a list except a record that is no bytes and the no-value type, and every list item is at least one byte, as [Lists](#lists) requires. A record that is no bytes can be a field, as in `Tagged`. A nullable value of it is one byte, its presence byte, so it can be a list item, as in the list of `Pair?`.

## The no-value type

The no-value type has exactly one value, which carries nothing. A contract uses it for events that have nothing to say but that they happened, such as a stream of moments when a document was saved. In C# it is `OahuUnit`, in the `AdamE.Oahu` namespace, and its value is `OahuUnit.Value`.

Its value is no bytes. So an events message of the no-value type is an empty message, and each empty message is one occurrence. A reader of it reads nothing. The message must then have ended, as for any message: a byte left over makes it malformed.

| Value | Bytes |
| --- | --- |
| The value | No bytes |

The [contract rules](contracts.md#rules-for-values) allow the no-value type only as the whole type of an events stream. It is never the type of a state, a list's items, a nullable value, a record's field, a union case's field or a method's argument, so the format defines no encoding for it in any of those places. As a list's item type it would break the rule of [Lists](#lists) that every item is at least one byte. As a nullable value it would be its presence byte alone, but the contract rules forbid that too. A record with no fields, which [Records](#records) describes, is no bytes too, but it is a record: the contract rules allow it wherever a record is allowed, except as a list's item type.

The no-value type is neither a record nor a union, so it adds no nesting depth. Its codec writes nothing and reads nothing, so `OahuWriter` and `OahuReader` have no method for it.

## Enums

An enum is the index of its value, written as a variable-length integer. An enum's declared values are its named members. The index is a value's position among them, in the order they are declared, counting from 0, whatever their numbers. An enum's numeric values and the names of its values are never written.

An enum has at least one declared value, and no two of its declared values have the same number. The contract rules forbid an enum with no members, an enum with an alias (a member with the same number as another, such as `B = A`), and a `[Flags]` enum. So each declared value has exactly one index.

For `enum Priority { High = 10, Low = 5, Normal = 7 }`:

| Value | Bytes |
| --- | --- |
| `High`, index 0 | `00` |
| `Low`, index 1 | `01` |
| `Normal`, index 2 | `02` |
| An absent `Priority?` | `00` |
| `Normal` as a `Priority?` | `01 02` |
| A list of `Normal`, `High`, `Low`, `Normal` | `04 02 00 01 02` |
| `High`, read from a longer form of index 0 | `80 00`, which a reader accepts; a writer writes `00` |

An index of 128 or more takes two bytes or more: the value at index 128 of a larger enum is `80 01`.

A value that is not exactly one declared value has no index: a number that no member has, such as `(Priority)8`, or a combination of members, such as `Priority.High | Priority.Low`, which is 15. A writer refuses it and writes nothing. In C#, an enum's codec throws `InvalidOperationException` for it, before it writes anything.

A reader rejects an index that is not below the number of the enum's declared values. A writer never writes one. A reader accepts an index in a longer form than the shortest, as it does any variable-length integer.

## Unions

A union is a value of one of a fixed set of records, its cases. This page writes `union Screen = Loading() | Failed(string Message) | Ready(Point Origin, OahuList<int> Values)` for this C# shape, an abstract record with its cases nested in it:

```csharp
public abstract record Screen
{
    public sealed record Loading() : Screen;
    public sealed record Failed(string Message) : Screen;
    public sealed record Ready(Point Origin, OahuList<int> Values) : Screen;
}
```

A union's cases are the records nested in its abstract record that derive from it, in the order they are declared there. A union has at least one case. A case's fields are the parameters of its own primary constructor, as for any record.

A union is the index of its case, written as a variable-length integer, then that case's fields. The index is the case's position among the union's cases, counting from 0. The case's fields are written as a record's are: in order, with nothing before, between or after them. A case with no fields is its index alone. A union with one case still writes the index, which is always 0. Case names are never written.

For these types:

```text
record Point(int X, int Y);
union Screen = Loading() | Failed(string Message) | Ready(Point Origin, OahuList<int> Values);
union Wrapper = Only(int Value);
```

| Value | Bytes |
| --- | --- |
| `Loading()` | `00` |
| `Failed("x")` | `01 01 78` |
| `Ready(Point(1, -1), [1, 3])` | `02 02 01 02 02 06` |
| `Ready(Point(0, 0), [])` | `02 00 00 00` |
| A list of `Screen`s: `Loading()`, `Loading()` | `02 00 00` |
| An absent `Screen?` | `00` |
| `Loading()` as a `Screen?` | `01 00` |
| `Only(1)`, a `Wrapper` | `00 02` |

The abstract record of a union can have parameters of its own, such as `Key` here:

```csharp
public abstract record Row(string Key)
{
    public sealed record Header(string Key, string Title) : Row(Key);
    public sealed record Line(string Key, string Text) : Row(Key);
}
```

The union's own parameters are not written. Each case's fields are its own parameters, so `Header`'s fields are `Key` and `Title`, and its `Key` is written once, as its first field: `Header("h", "Today")` is `00 01 68 05 54 6f 64 61 79`.

A case is written only as part of its union, with its index. The contract rules forbid a case type on its own, as the type of a field, a list's items, a nullable value, a stream or a method's argument; the union goes there, or a separate record with the same fields. So the format gives a case type on its own no bytes.

A reader rejects an index that is not below the number of the union's cases. A writer never writes one. A reader accepts an index in a longer form than the shortest: `80 00` is read as index 0, `Loading()`.

Adding, removing or reordering a record's fields, an enum's values or a union's cases changes what existing bytes mean. That is why both sides of an app are generated together from one contract, and the contract hash checks that they were. The contract hash is a 64-bit hash of the contract's signature, which both sides carry and compare when a screen connects; [Architecture](architecture.md#the-wire-format-and-the-fixed-interface) describes it.

In C#, `OahuWriter.WriteIndex` writes the index of an enum's value or a union's case, and `OahuReader.ReadIndex` reads it. Both take the number of the type's values or cases. `WriteIndex` throws `ArgumentOutOfRangeException` for an index that is negative or not below that number, and both throw it for a number below 1, since a type with no values or cases has no value to write. `ReadIndex` throws `OahuFormatException` for an index that is not below the number, so the codec that maps the index to its value or case never sees one out of range. An enum's codec calls only these. A union's codec calls `EnterNested`, then the index method, then reads or writes the case's fields, then calls `ExitNested`, as [Nesting depth](#nesting-depth) says.

## Keyed-list batches

A keyed list's message is a batch: a list of operations, `ListOp<T>`, as [Keyed lists](contracts.md#keyed-lists) describes. Each operation is one of five cases, and its rows are of the list's row type, a record or a union with a `Key` field.

A batch is the number of its operations as a count, then each operation in order. An operation is its tag, then its payload. The tag says which operation it is, and is written as a variable-length integer, as an enum's index is: it is the operation's position in this table, counting from 0. The payload is the operation's indexes and row, in the order of the table, with nothing before, between or after them. Each index is written as a count. A row is written as a value of its type would be. There is nothing between the operations and nothing after the last one. Case names are never written.

| Operation | Tag | Payload |
| --- | --- | --- |
| `Insert(Index, Item)` | 0 | `Index`, then `Item` |
| `Remove(Index)` | 1 | `Index` |
| `Move(From, To)` | 2 | `From`, then `To` |
| `Replace(Index, Item)` | 3 | `Index`, then `Item` |
| `Clear()` | 4 | Nothing |

A tag is from 0 to 4, so a writer writes it as one byte, `00` to `04`. A reader accepts a tag in a longer form than the shortest, as it accepts an enum's index: `80 00` is read as tag 0, an insert, and `84 00` as tag 4, a clear.

For these types, the first in the notation of [Records](#records) and the second as in [Unions](#unions):

```text
record TaskRow(string Key, string Text, bool Done);
union Row = Header(string Key, string Title) | Line(string Key, string Text);
```

Both are rows, as [Rows and keys](contracts.md#rows-and-keys) says: `TaskRow` and each case of `Row` implement `IOahuKeyed` through their `Key`. `TaskRow("a", "Milk", false)` is `01 61 04 4d 69 6c 6b 00`, and `TaskRow("b", "Eggs", true)` is `01 62 04 45 67 67 73 01`.

| Value | Bytes |
| --- | --- |
| A batch of `Insert(0, TaskRow("a", "Milk", false))` | `01 00 00 01 61 04 4d 69 6c 6b 00` |
| A batch of `Remove(2)` | `01 01 02` |
| A batch of `Move(0, 2)` | `01 02 00 02` |
| A batch of `Replace(1, TaskRow("b", "Eggs", true))` | `01 03 01 01 62 04 45 67 67 73 01` |
| A batch of `Clear()` | `01 04` |
| `Clear()`, `Insert(0, TaskRow("a", "Milk", false))`, `Insert(1, TaskRow("b", "Eggs", true))`, `Move(1, 0)`, `Replace(1, TaskRow("a", "Milk", true))`, `Remove(0)` | `06 04 00 00 01 61 04 4d 69 6c 6b 00 00 01 01 62 04 45 67 67 73 01 02 01 00 03 01 01 61 04 4d 69 6c 6b 01 01 00` |
| A batch of `Row`s: `Insert(0, Header("h", "Today"))` | `01 00 00 00 01 68 05 54 6f 64 61 79` |
| A batch of `Row`s: `Replace(0, Line("l", ""))` | `01 03 00 01 01 6c 00` |

An index of 128 or more takes two bytes or more: a batch of `Remove(128)` is `01 01 80 01`.

When an observer subscribes, a keyed list replays its rows as one batch: a `Clear()`, then an `Insert` of each row, in order, at indexes 0, 1, 2 and on. For an empty list, the replay is a `Clear()` alone.

| Value | Bytes |
| --- | --- |
| The replay of a list of `TaskRow("a", "Milk", false)` and `TaskRow("b", "Eggs", true)` | `03 04 00 00 01 61 04 4d 69 6c 6b 00 00 01 01 62 04 45 67 67 73 01` |
| The replay of an empty list | `01 04` |

A batch has at least one operation. A delivered batch is never empty, and an empty list is replayed as a `Clear()`, so a batch with no operations is never sent, and the format gives it no bytes. A writer refuses a batch with no operations. A reader rejects a batch whose count is 0. So every message of a keyed list is at least two bytes.

Each index is written as a count, so a writer refuses a negative index, as it refuses any negative count. A reader reads an index as a count: it rejects one larger than 2,147,483,647, and accepts a longer form than the shortest.

Every operation takes at least one byte, its tag. So a batch's count is never larger than the number of bytes left after it, and a reader rejects a count that is, as it does a list's count in [Lists](#lists). It reads the count as a count first, and only then compares it with the bytes that remain, as [Counts](#counts) says, and it checks the count before it makes room for the operations. So a count above 2,147,483,647 is rejected as a count that is too large, whatever follows, and a message of a few bytes cannot make a reader allocate room for two billion operations.

A row is never no bytes: its `Key` is a string, and a string is at least one byte. So a list of rows is a list that [Lists](#lists) allows, and an insert or a replace is at least three bytes. The count check does not rely on it, because the tag alone is a byte.

A reader checks only the bytes. It does not check that an index is in range for the list, that a move's indexes are, that an insert adds a key the list does not have, or that a replace keeps the key of the row it replaces. Those depend on the list the batch applies to, so whoever applies the batch checks them there, and refuses the whole batch if one fails, as [Batches](contracts.md#batches) says. A batch whose bytes are well formed is read, whatever its indexes and keys.

A batch and its operations are not records or unions of a contract, so they add no nesting depth, as [Nesting depth](#nesting-depth) says. A row is a record or a union, so a row of a batch is at depth 1, as a record that is a message's value is.

In C#, `OahuWriter.WriteBatch` and `OahuReader.ReadBatch` take a delegate that writes or reads one row, and the row type is an `IOahuKeyed`. Pass a method group of a static method or a `static` lambda, so the delegate is created once. `WriteBatch` reads the batch's `Count` once and allocates nothing. It throws `ArgumentNullException` for a null batch or delegate and `ArgumentException` for an empty batch, before it writes anything. It reads each operation once and checks it just before writing it: it throws `ArgumentException` for a null operation or the null row of an insert or a replace, and `ArgumentOutOfRangeException` for a negative index. After each row, it throws `InvalidOperationException` if the delegate reset the writer or left the nesting depth different from what it was before that row. If an operation fails its check, the delegate throws, or a row fails these checks, the exception propagates. The operations before it have then been written, and the writer's contents are unspecified, so reset it before writing another message, as for `WriteList`. `ReadBatch` returns an array of exactly the count's length. Apart from what the delegate allocates for the rows, that array and one `ListOp<T>` for each operation are all it allocates. It throws `InvalidOperationException` when a row leaves the nesting depth different from what it was before that row.

## Nesting depth

A record or union type can contain itself, through a list, a nullable value or a union case, such as `record Node(string Name, OahuList<Node> Children)`. For such a type, the input decides how deep a value nests, so the format limits it.

A value's nesting depth is the number of record and union values that enclose it, counting itself if it is one. Records and unions count because they are the only kinds of value that can contain a value of their own type. Every record and union value counts, including a value of a record type that never contains itself. Lists, nullable values, enums, strings and primitive values do not add depth, and nor do a keyed list's batches and their operations.

A union value and the case it holds are one level, not two. In a contract a union's cases are records; the union value counts once, the record of its case is not counted again, and the case's fields are at the union's depth. A case is never a value on its own, as [Unions](#unions) says, so a case record is always counted as part of its union.

The largest nesting depth is 64. A value at depth 65 is not valid in the format. A writer refuses to write it, and a reader rejects it as malformed. Every implementation accepts depth 64 and rejects depth 65.

For these types, in the notation of [Records](#records) and [Unions](#unions):

```text
record Point(double X, double Y);
record Node(string Name, Point Origin, OahuList<Node> Children);
record Folder(OahuList<Node>? Nodes);
union Shape = Circle(Point Center, double Radius) | Group(OahuList<Shape> Shapes);
```

| Value | Depth |
| --- | --- |
| A message whose value is a primitive, such as an `int`, or a list of primitives | 0 |
| A `Node` that is the message's value | 1 |
| A field that is a primitive, string, enum, list or nullable value, such as that `Node`'s `Name` or `Children` | The depth of the record that holds it: 1 |
| That `Node`'s `Origin`, a `Point` | 2 |
| A `Node` in its `Children` | 2 |
| A `Node` in the `Children` of that one | 3 |
| The `Origin` of a `Node` at depth 64 | 65, which is not valid, although `Point` never contains itself |
| Each `Node` of a message whose value is a list of `Node`s | 1 |
| A `Node` in the `Nodes` of a `Folder`: a record inside a list inside a nullable value inside a record | The `Folder`'s depth plus 1 |
| A `Shape` that is the message's value | 1 |
| The case a `Shape` holds, and that case's fields, such as a `Group`'s `Shapes` list | The `Shape`'s depth |
| A `Shape` in the `Shapes` of a `Group`: a `Group` inside a `Group` | 2 |
| A `Circle`'s `Center`: a record that is a field of a union's case | The `Shape`'s depth plus 1 |

The limit exists for two reasons. Without it, a reader of a deeply nested value runs out of stack. On most platforms that ends the app with no error to catch, and the depth at which it happens differs with each platform's stack size. With the limit, every platform fails on the same values, with a message. The limit also bounds what a reader allocates, as [Lists](#lists) describes.

In C#, `OahuFormat.MaxNestingDepth` is the limit. The codec of a record or a union calls `EnterNested` on the writer or the reader before anything of the value, so before a record's first field or a union's case index, and `ExitNested` after its last field. A record with no fields calls them too, and so does a record type that never contains itself. A union's codec calls `EnterNested` once, writes or reads the case index and that case's fields, then calls `ExitNested`; it does not call them again for the case, including a case with no fields. Nothing else counts depth: `WriteList`, `ReadList`, `WriteBatch`, `ReadBatch` and the presence methods do not.

- `OahuWriter.EnterNested` throws `InvalidOperationException` for a value at depth 65, before anything of the value is written. Reset the writer before writing another message.
- `OahuReader.EnterNested` throws `OahuFormatException` for a value at depth 65. Its offset is where that value starts, and the reader stays where it was.
- `ExitNested` throws `InvalidOperationException` on both when no value is entered. Encode a whole message for sending with `OahuWriter.WriteMessage(value, codec)`. It resets the writer, runs the codec and returns bytes only after the codec and the completion checks succeed. If either throws, or `WriteMessage` itself throws for a null codec or a call from inside a codec, list or batch, `GetCompletedSpan` refuses the partial message until reset. Any write method that throws faults the writer the same way, so a caller that encodes manually and catches the exception also gets the refusal, even when the codec exited its nesting in a `finally`. `GetCompletedSpan` checks that every value entered on the writer has been exited and that no `WriteList` or `WriteBatch` call is still running, so calling it from an item or row writer throws, and so does calling it after any write threw, until `Reset`. `OahuReader.EnsureEnd()` checks the reader's own depth. Both throw `InvalidOperationException` if a value is still entered. `OahuWriter.WrittenSpan` can be used to inspect partial bytes.
- `WriteList` and `ReadList` throw `InvalidOperationException` when an item leaves the depth different from what it was before that item, and `WriteBatch` and `ReadBatch` when a row does. Each unbalanced depth is a fault in a codec, not in the input.
- These checks cover list items, the rows of a batch and the end of a message, and nothing else. On the writer, the end of a message is checked by `GetCompletedSpan`, including when called by `WriteMessage`; reading `WrittenSpan` checks nothing. A codec that enters without exiting for values that are not list items, such as a record's fields, is not caught where it happens. The depth it leaves behind builds up, and the fault can then be reported as a value nested too deep.
- `OahuWriter.Reset` returns the writer's depth to zero.

## Malformed input

A reader rejects input that is not in the format. It never guesses at a value or skips bytes. Each of these makes a message malformed:

- The input ends inside a variable-length integer.
- A variable-length integer is longer than 10 bytes. A tenth byte with its high bit set makes it longer than 10 bytes, whatever follows, including the end of the input.
- The last byte of a 10-byte integer is greater than `01`, so the value does not fit in 64 bits.
- A count is larger than 2,147,483,647.
- A boolean's byte is neither `00` nor `01`.
- An `int` or a `long` is malformed in any of the ways a variable-length integer is, above.
- An `int`'s zigzag value is larger than 4,294,967,295, so the value does not fit in 32 bits.
- The input ends before a boolean, or inside a double.
- An instant or a duration is malformed in any of the ways a `long` is, above.
- An instant is earlier than -62,135,596,800,000 or later than 253,402,300,799,999 milliseconds from the Unix epoch.
- A string's length is malformed in any of the ways a count is, above.
- A string's length is larger than the number of bytes left after it. A length larger than 2,147,483,647 is rejected as a count that is too large instead, whatever follows.
- A string's bytes are not well-formed UTF-8. This includes an overlong form, such as `c0 af` for `/`; an encoded surrogate, U+D800 to U+DFFF, such as `ed a0 80`; a code point above U+10FFFF, such as `f4 90 80 80`; a sequence cut short, such as `e2 82` at the end of the string or `c3 28`; a continuation byte (`80` to `bf`) that no lead byte comes before; and the bytes `c0`, `c1` and `f5` to `ff`, which never appear in UTF-8.
- A presence byte is neither `00` nor `01`, or the input ends before it.
- A list's count is malformed in any of the ways a count is, above.
- A list's count is larger than the number of bytes left after it. A count larger than 2,147,483,647 is rejected as a count that is too large instead, whatever follows.
- An item of a list, a present value, a field of a record or a field of a union's case is malformed in any of the ways a value of its type is.
- An enum's or a union's index is malformed in any of the ways a variable-length integer is, above.
- An enum's or a union's index is not below the number of the type's values or cases.
- A batch's count is malformed in any of the ways a count is, above.
- A batch's count is 0.
- A batch's count is larger than the number of bytes left after it. A count larger than 2,147,483,647 is rejected as a count that is too large instead, whatever follows.
- An operation's tag is malformed in any of the ways a variable-length integer is, above.
- An operation's tag is larger than 4.
- An operation's index is malformed in any of the ways a count is, above.
- The row of an insert or a replace is malformed in any of the ways a value of its type is.
- A record or union value is at nesting depth 65.
- Bytes are left over after the message's value.

In C#, `OahuReader` throws `OahuFormatException` for each of these. Its message says what was wrong and at which byte offset, counted from 0 at the start of the input; its `Offset` property holds the same offset. For a malformed value, the offset is where that value starts; for a string, that is its length, for a list, its count, and for a batch, its count. For an operation's tag that is too large, it is where the tag starts. When a string's bytes are not well-formed, the message also names the offset of the first sequence that is wrong. For bytes left over, it is the first byte not read. A single read call that throws leaves the reader where it was. The exceptions are `ReadList` when an item is malformed, and `ReadBatch` when an operation is: the items or operations before it have been read. When `ReadBatch` rejects a tag, the reader is at the start of that tag. A value made of several parts is read with several calls, so a failure can come after earlier parts have been read; a reader that has thrown is not used again, and the message is rejected as a whole.

## Test vectors

The format's test vectors are in [`tests/conformance/wire-format/vectors.json`](../tests/conformance/wire-format/vectors.json). For many values, the file gives the exact bytes; for many byte sequences, it says that a reader must reject them, and why. Every implementation of the format must pass every vector. The file's [README](../tests/conformance/wire-format/README.md) describes it and what an implementation does with each kind of vector.
