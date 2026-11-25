# Wire format test vectors

`vectors.json` is the wire format's shared test vectors: the executable form of [the wire format page](../../../docs/wire-format.md). For each of many values it gives the exact bytes, and for each of many byte sequences it says that a reader must reject them, and why. Every implementation of the format must pass every vector: the C# `OahuWriter` and `OahuReader` now, and the Swift and Kotlin readers and writers when they are written. Each implementation runs the file through a small test harness of its own. The C# harness, which is the reference, is in [`tests/core/AdamE.Oahu.Tests/Conformance`](../../core/AdamE.Oahu.Tests/Conformance).

## The file

The file is JSON in ASCII: every other character in a string is a `\u` escape. No object has a key twice. Values nest up to 256 levels deep in the JSON, so a harness's parser must allow that depth. The top-level object has five fields:

| Field | Holds |
| --- | --- |
| `about` | A sentence that points here. |
| `reasons` | Why a reader rejects input: one entry for each item of the page's [Malformed input](../../../docs/wire-format.md#malformed-input) list, in the same order. |
| `refusals` | Why a writer refuses a value. |
| `types` | The records, enums and unions that vectors use. |
| `vectors` | The vectors, grouped by the page's sections in the page's order. |

Each entry of `reasons`, `refusals`, `types` and `vectors` is on one line.

## Types

Each vector names the type of its value with a type expression:

```text
type   = single [ "?" ]
single = "list<" type ">" | primitive | declared
```

There is no whitespace. `T?` is a nullable `T`; `T` is never itself nullable, so `int??` is not a type. `list<T>` is a list of `T`. A declared name is a name from `types`, and starts with a capital letter. The primitives are:

| Primitive | The page's section |
| --- | --- |
| `bool` | Booleans |
| `int`, `long` | Signed integers |
| `double` | Doubles |
| `string` | Strings |
| `instant`, `duration` | Instants and durations |
| `varuint` | Variable-length integers: an unsigned 64-bit integer |
| `count` | Counts |

`varuint` and `count` are building blocks of the format, not value types of a contract, so they appear only as a vector's whole type, never inside another type.

Each entry of `types` has a `name` and exactly one of these:

- `"record": [fields]`: a record, whose fields are written in this order. Each field is `{"name": ..., "type": ...}`. A record with no fields is `"record": []`.
- `"enum": [names]`: an enum, whose values are these names in declaration order. A value's index is its position here. The file does not give the values' numbers, because they are never written.
- `"union": [cases]`: a union, whose cases are in this order. Each case is `{"name": ..., "fields": [fields]}`, with fields as for a record. A case is not a type of its own.

A type can refer to itself and to types declared after it. No type in the file is a list of a record that is no bytes, or a record that contains itself through record fields alone; the page gives neither any bytes.

## Values

A value is written in JSON by its type:

| Type | JSON |
| --- | --- |
| `bool` | `true` or `false` |
| `int`, `count` | A JSON integer |
| `long`, `duration` | A decimal string, such as `"-9223372036854775808"`. A duration is its ticks. |
| `instant` | A decimal string of the milliseconds since the Unix epoch, such as `"1762095600123"` |
| `varuint` | A decimal string, such as `"18446744073709551615"` |
| `double` | Its IEEE 754 bit pattern as a string of 16 lowercase hex digits, most significant first, such as `"3ff8000000000000"` for 1.5 |
| `string` | A JSON string when the string is well-formed UTF-16. Otherwise, an array of its UTF-16 code units as integers, such as `[97, 55296]` for `a` and a lone U+D800. |
| `T?` | `null` when absent; otherwise the value of `T` |
| `list<T>` | An array of the items |
| A record | An object with each field's value under its name, in the record's order, such as `{"X": 1, "Y": -1}`. A record with no fields is `{}`. |
| An enum | The value's name, such as `"Normal"` |
| A union | An object with one key, the case's name, whose value is the case's fields as for a record, such as `{"Failed": {"Message": "x"}}` or `{"Loading": {}}` |

Large integers are strings and doubles are bit patterns so that every JSON parser reads them exactly. A string is an array of code units only when it holds a lone surrogate, which a JSON string cannot carry safely.

Two values are equal when they have the same type and the same value: doubles by their bits, so a NaN equals a NaN with the same bits and 0 does not equal -0; strings by their code units; instants by their milliseconds.

## Vectors

Every vector has a `name`, a `type` and a `kind`. The name is its section, a dot and a name within the section, such as `varuint.128` or `string.overlong-2-byte`. Bytes are lowercase hex with no separators; `""` is no bytes. A vector may have a `note`, a sentence for people, and a `doc`, which names the row of a table on the page that it gives; a harness ignores both. A `doc` is the title of the page's `##` section that holds the table, a colon and a space, then the row's first cell exactly as the page writes it, Markdown included, such as `Signed integers: -1` or ``Strings: `Milk` ``. Only rows of tables with a Bytes column are named, each by exactly one vector.

To write a value, a harness writes it as a message: the value alone, in a new writer. To read bytes, it reads one value of the type from the start of the bytes and then checks that the input has ended, as a reader of a message does. It reads and writes records and unions through its own codec calls for them, so that nesting depth is counted as the page says.

| Kind | Fields | A harness checks |
| --- | --- | --- |
| `round-trip` | `value`, `hex` | Writing `value` gives exactly `hex`, and reading `hex` gives exactly `value`. |
| `decode-only` | `value`, `hex`, optional `writes` | Reading `hex` gives `value`. A writer writes something else for `value`, such as the shortest form of an integer. When `writes` is there, writing `value` gives exactly `writes`. |
| `encode-only` | `value`, `hex`, `reads` | Writing `value` gives exactly `hex`, and reading `hex` gives `reads`, a different value. This is a string with a lone surrogate, which a writer writes as U+FFFD. |
| `reject` | `hex`, `reason` | Reading `hex` fails as malformed input, for the cause `reason`. |
| `refuse` | `value`, `reason` | Writing `value` fails. |

A failure must be the implementation's error for malformed input or for a value it refuses, not a crash or an unrelated error. In C#, a rejection is an `OahuFormatException`.

A harness for a language that cannot hold a value may skip the step that needs it, and must say so in its own code. A Swift string cannot hold a lone surrogate, so a Swift harness reads the bytes of an `encode-only` vector but cannot write its value.

### Reasons

A reject vector's `reason` says why a reader rejects the bytes. It is a code from `reasons`. An entry of `reasons` has a `code`, a `kind` and a `rule`, the text of its item of the page's Malformed input list. The entries are one-to-one with the list's items, in its order.

- An entry of kind `cause` names what is wrong, such as `string-utf8`. A vector's `reason` is always a cause.
- An entry of kind `via` is an item that says a part can be malformed the way its own type can, such as `part` for "An item of a list, a present value, a field of a record or a field of a union's case is malformed in any of the ways a value of its type is", or `string-length` for a string's length, which can be malformed as a count can. No vector names a via entry as its reason, because the cause is always the innermost rule: an `int?` whose present `int` is cut short has the reason `varuint-truncated`.

A harness must check the cause of every reject vector. It maps its reader's failure to one of the cause codes and compares that code with the vector's `reason`; a vector whose bytes are rejected for another cause fails. Some rules can only be tested this way: a reader that allows a list's count or a string's length one larger than the bytes left, one that makes room for a list's items before it checks the count, and one with no maximum on lengths and counts each still reject every reject vector, some of them for the wrong cause. The C# harness maps `OahuReader`'s message to a code by the fixed text of each message, and requires exactly one code to match.

Each reject vector has one thing wrong with it, or more than one where the page fixes which is reported first, so every implementation rejects it for the same cause. These have more than one, or could be read two ways:

- `string.length-2147483648` and `list.count-2147483648` have a length or count above 2,147,483,647 that is also longer than the input. The page's [Counts](../../../docs/wire-format.md#counts) says a reader checks that a count is a valid count before it compares it with the bytes that remain, so both are `count-too-large`.
- `varuint.10-continuation-bytes` is ten bytes with their high bits set, and the input ends there. The page's [Variable-length integers](../../../docs/wire-format.md#variable-length-integers) says a tenth byte with its high bit set makes the integer longer than 10 bytes, whatever follows, so it is `varuint-too-long`, not `varuint-truncated`.

### Refusals

An entry of `refusals` has a `code` and a `rule`, a sentence quoted from the page. A refuse vector's `reason` is one of these codes: `negative-count` for a count below 0, and `too-deep` for a value at nesting depth 65.

## What the file does not cover

These parts of the page are left to each implementation's own tests, because a vector cannot express them or would be too large:

- An instant with a fraction of a millisecond, which a writer rounds down, and an instant's offset. The file's instants are whole milliseconds with no offset.
- An enum value that is not exactly one declared value, which a writer refuses. The file names enum values, so it cannot hold one.
- A string's length of three bytes or more, from 16,384 bytes, and the writer's limit on a message's size.
- A writer refusing an instant outside the valid range. A C# `DateTimeOffset` is always in the range, so the C# writer cannot be given one, and a value of the file is the milliseconds the writer would write.
- Writing a signalling NaN, which a runtime may quieten before the writer sees it. The file only reads one.
- The details of an error: its message, its byte offset, and where the reader is left.
- Faults in a codec rather than in the input, such as a record codec that enters a nesting level without leaving it.

## Changing the file

A change to the wire format changes the page, this file and the code together, in one pull request. The C# harness checks that the file and the page agree:

- The `reasons` are the page's Malformed input list, word for word and in order, and the harness knows each code and its kind. Every cause and every refusal is used by a vector, and no vector uses a via code.
- Every `refusals` rule is quoted from the page.
- The harness knows each of the page's `##` sections and which vector sections belong to it. Vector names are unique, and grouped by section in the order of the page's `##` headings, so moving or renaming a section on the page fails a test.
- Every row of each table on the page with a Bytes column has exactly one vector whose `doc` names it, and no `doc` names anything else. Where the row lists its bytes, or says it is no bytes, the vector has exactly those bytes. A number in the row's Value, Milliseconds or Ticks column is the vector's value; a Zigzag value is the zigzag encoding of the vector's value and the value of its bytes; an instant in the Instant column is the vector's milliseconds. A table with a Bytes column that has a column the harness does not know fails a test.
- Every declared type is used by a vector.
- The file is in its layout, one entry per line, and the depth vectors are the generated ones.

These are not checked by machine, and a reviewer reads them:

- Deleting a hand-written vector that no `doc` names. Nothing notices it is gone.
- The page's depth table, which has no Bytes column. The depth vectors follow it, but nothing compares them with it.
- The bytes of a row that describes them in words, such as "`80 01`, then 128 bytes of `61`". Its vector is matched to the row by `doc` only.
- A Duration cell, which is words, such as "One day".
- The order of vectors within a section.
- The page's prose, apart from the Malformed input list and the quoted refusals. A rule stated only in prose has a vector only if someone wrote one.

The vectors of the nesting depth section, whose names start with `depth.`, are generated by `DepthVectors.cs` in the C# harness, because their values nest 64 levels deep. It builds their bytes by repeating each level's bytes, worked out from the page, not with `OahuWriter`. To change them, or to put the file back in its layout after editing it by hand, run the C# tests with the environment variable `OAHU_WRITE_VECTORS` set to `1`; the test of the layout then rewrites the file instead of checking it. Run the tests again without it, and review the change to the file.

Bytes written by hand must be worked out from the page, not copied from a writer's output, so that the file checks the code rather than repeating it. Work them out by hand from the page's rules, or with a few lines of code of your own that follow the page, such as an LEB128 and zigzag encoder, and say in the pull request how you did.
