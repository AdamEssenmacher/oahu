# Wire format

Oahu's C# and native code exchange values as bytes in the format this page specifies. Every implementation of the format must read and write exactly these bytes. In C#, `OahuWriter` writes the format and `OahuReader` reads it, both in the `AdamE.Oahu.Wire` namespace.

This page covers the parts of the format that exist so far: messages, variable-length integers, counts, booleans, signed integers and doubles.

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
- Bytes are left over after the message's value.

In C#, `OahuReader` throws `OahuFormatException` for each of these. Its message says what was wrong and at which byte offset, counted from 0 at the start of the input; its `Offset` property holds the same offset. For a malformed value, the offset is where that value starts. For bytes left over, it is the first byte not read. A single read call that throws leaves the reader where it was. A value made of several parts is read with several calls, so a failure can come after earlier parts have been read; a reader that has thrown is not used again, and the message is rejected as a whole.
