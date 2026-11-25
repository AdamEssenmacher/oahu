# Wire format

Oahu's C# and native code exchange values as bytes in the format this page specifies. Every implementation of the format must read and write exactly these bytes. In C#, `OahuWriter` writes the format and `OahuReader` reads it, both in the `AdamE.Oahu.Wire` namespace.

This page covers the parts of the format that exist so far: messages, variable-length integers and counts.

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

## Malformed input

A reader rejects input that is not in the format. It never guesses at a value or skips bytes. Each of these makes a message malformed:

- The input ends inside a variable-length integer.
- A variable-length integer is longer than 10 bytes.
- The last byte of a 10-byte integer is greater than `01`, so the value does not fit in 64 bits.
- A count is larger than 2,147,483,647.
- Bytes are left over after the message's value.

In C#, `OahuReader` throws `OahuFormatException` for each of these. Its message says what was wrong and at which byte offset, counted from 0 at the start of the input; its `Offset` property holds the same offset. For a malformed integer or count, the offset is where that integer starts. For bytes left over, it is the first byte not read. A single read call that throws leaves the reader where it was. A value made of several parts is read with several calls, so a failure can come after earlier parts have been read; a reader that has thrown is not used again, and the message is rejected as a whole.
