# Wire format

Oahu's C# and native code exchange values as bytes in the format this page specifies. Every implementation of the format must read and write exactly these bytes. In C#, `OahuWriter` writes the format and `OahuReader` reads it, both in the `AdamE.Oahu.Wire` namespace.

This page covers the parts of the format that exist so far: messages, variable-length integers, counts, booleans, signed integers, doubles, strings, instants and durations, nullable values and lists.

## Messages

A message is the bytes of one value and nothing else. It has no header, tag, length or schema. Both sides are generated from the same contract, so the reader already knows the type of the value it reads.

A reader reads the value from the first byte. When the value is read, the message must have ended. Bytes left over make the message malformed.

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

A reader accepts a longer form than the shortest, up to 10 bytes. For example, `80 00` and `80 80 80 80 80 80 80 80 80 00` are both read as 0. In a 10-byte integer the last byte holds only the value's top bit, so it must be `00` or `01`.

## Counts

A count, such as a length or a number of items, is a whole number from 0 to 2,147,483,647, the largest value of a C# `int`. It is written as a variable-length integer. A writer refuses a negative count.

A reader rejects a count larger than 2,147,483,647, even when the integer itself is well formed.

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

Every item of a list takes at least one byte. The contract rules forbid an item type whose values can be written as no bytes at all. So a list's count is never larger than the number of bytes left after it, and a reader rejects a count that is. A reader must check this before it makes room for the items. The check guarantees that one list's array of items has no more items than there are bytes left after its count, so a message of a few bytes cannot make a reader allocate a list of two billion items. It does not by itself bound the total a reader allocates: that total is bounded by the nesting depth times the size of the input. For a type of fixed depth, the type sets the depth. For a type that contains itself, such as a record with a list of itself, the input sets the depth, and deep nesting can exhaust a reader's stack. The format does not yet limit nesting depth.

Lists nest. An item can be any value: a list, a nullable value, a string and so on. An inner list is its own count and items, and an absent item is its presence byte alone.

| Value | Bytes |
| --- | --- |
| A list of three lists of `int`s: empty, then 1, then 2 and 3 | `03 00 01 02 02 04 06` |
| A list of nullable strings: absent, `x`, the empty string, absent | `04 00 01 01 78 01 00 00` |

In C#, `OahuWriter.WriteList` and `OahuReader.ReadList` take a delegate that writes or reads one item. Pass a method group of a static method or a `static` lambda, so the delegate is created once. `ReadList` returns an array of exactly the count's length and allocates nothing else, apart from what the delegate allocates for the items; an empty list is the same shared instance every time. `WriteList` reads the list's `Count` once. After the items, it throws `InvalidOperationException` if they wrote fewer bytes than there are items; it does not check each item on its own. It also throws `InvalidOperationException` if the delegate reset the writer. If the delegate throws, or `WriteList` does, the exception propagates. The writer's contents are then unspecified, so reset it before writing another message.

## Malformed input

A reader rejects input that is not in the format. It never guesses at a value or skips bytes. Each of these makes a message malformed:

- The input ends inside a variable-length integer.
- A variable-length integer is longer than 10 bytes.
- The last byte of a 10-byte integer is greater than `01`, so the value does not fit in 64 bits.
- A count is larger than 2,147,483,647.
- A boolean's byte is neither `00` nor `01`.
- An `int` or a `long` is malformed in any of the ways a variable-length integer is, above.
- An `int`'s zigzag value is larger than 4,294,967,295, so the value does not fit in 32 bits.
- The input ends before a boolean, or inside a double.
- An instant or a duration is malformed in any of the ways a `long` is, above.
- An instant is earlier than -62,135,596,800,000 or later than 253,402,300,799,999 milliseconds from the Unix epoch.
- A string's length is larger than the number of bytes left after it.
- A string's bytes are not well-formed UTF-8. This includes an overlong form, such as `c0 af` for `/`; an encoded surrogate, U+D800 to U+DFFF, such as `ed a0 80`; a code point above U+10FFFF, such as `f4 90 80 80`; a sequence cut short, such as `e2 82` at the end of the string or `c3 28`; a continuation byte (`80` to `bf`) that no lead byte comes before; and the bytes `c0`, `c1` and `f5` to `ff`, which never appear in UTF-8.
- A presence byte is neither `00` nor `01`, or the input ends before it.
- A list's count is malformed in any of the ways a count is, above.
- A list's count is larger than the number of bytes left after it.
- An item of a list, or a present value, is malformed in any of the ways a value of its type is.
- Bytes are left over after the message's value.

In C#, `OahuReader` throws `OahuFormatException` for each of these. Its message says what was wrong and at which byte offset, counted from 0 at the start of the input; its `Offset` property holds the same offset. For a malformed value, the offset is where that value starts; for a string, that is its length, and for a list, its count. When a string's bytes are not well-formed, the message also names the offset of the first sequence that is wrong. For bytes left over, it is the first byte not read. A single read call that throws leaves the reader where it was. The exception is `ReadList` when an item is malformed: the items before it have been read. A value made of several parts is read with several calls, so a failure can come after earlier parts have been read; a reader that has thrown is not used again, and the message is rejected as a whole.
