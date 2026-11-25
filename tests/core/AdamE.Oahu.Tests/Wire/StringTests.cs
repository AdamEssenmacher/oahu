using System.Text;
using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class StringTests
{
    [Theory]
    [InlineData("", "00")]
    [InlineData("Milk", "044d696c6b")]
    [InlineData("é", "02c3a9")]
    [InlineData("€", "03e282ac")]
    [InlineData("\U0001F600", "04f09f9880")]
    [InlineData("\U0010FFFF", "04f48fbfbf")]
    [InlineData("a\u0000b", "03610062")]
    [InlineData("\u0000", "0100")]
    [InlineData("﻿", "03efbbbf")]
    [InlineData("﻿a", "04efbbbf61")]
    [InlineData("héllo wörld", "0d68c3a96c6c6f2077c3b6726c64")]
    [InlineData("\U0001F3DD️ Oʻahu", "0ef09f8f9defb88f204fcabb616875")]
    public void A_string_is_written_as_its_UTF8_length_and_bytes_and_read_back(string value, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteString(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(value, reader.ReadString());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("a", 127, "7f")]
    [InlineData("a", 128, "8001")]
    [InlineData("a", 200, "c801")]
    [InlineData("€", 42, "7e")]
    [InlineData("€", 43, "8101")]
    [InlineData("é", 63, "7e")]
    [InlineData("é", 64, "8001")]
    [InlineData("a", 16_383, "ff7f")]
    [InlineData("a", 16_384, "808001")]
    [InlineData("\U0001F600", 4_095, "fc7f")]
    [InlineData("\U0001F600", 4_096, "808001")]
    public void The_length_is_the_number_of_UTF8_bytes_and_crosses_prefix_boundaries(
        string unit, int repeat, string prefixHex)
    {
        var value = string.Concat(Enumerable.Repeat(unit, repeat));
        var utf8 = Encoding.UTF8.GetBytes(value);

        var writer = new OahuWriter();
        writer.WriteString(value);

        Assert.Equal(prefixHex + Hex.Of(utf8), Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadString());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(100_000)]
    public void The_bytes_do_not_depend_on_the_buffer_capacity(int initialCapacity)
    {
        string[] values =
        [
            "", "x", "Milk", new('a', 42), new('a', 43), new('a', 127), new('a', 128), new('é', 64),
            new('€', 43), string.Concat(Enumerable.Repeat("\U0001F600", 40)), new('z', 20_000),
        ];

        var expected = new List<byte>();
        foreach (var value in values)
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            var lengthWriter = new OahuWriter();
            lengthWriter.WriteCount(utf8.Length);
            expected.AddRange(lengthWriter.WrittenSpan.ToArray());
            expected.AddRange(utf8);
        }

        var writer = new OahuWriter(initialCapacity);
        foreach (var value in values)
            writer.WriteString(value);

        Assert.Equal(expected.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void A_long_string_grows_the_buffer_and_keeps_what_was_written_before()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 50_000; i++)
            builder.Append((i % 4) switch { 0 => "a", 1 => "é", 2 => "€", _ => "\U0001F600" });
        var value = builder.ToString();
        var utf8 = Encoding.UTF8.GetBytes(value);
        Assert.Equal(125_000, utf8.Length);

        var writer = new OahuWriter(1);
        writer.WriteBool(true);
        writer.WriteString(value);

        Assert.Equal(1 + 3 + utf8.Length, writer.WrittenCount);
        Assert.Equal("01" + "c8d007", Hex.Of(writer.WrittenSpan[..4]));
        Assert.Equal(utf8, writer.WrittenSpan[4..].ToArray());

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.True(reader.ReadBool());
        Assert.Equal(value, reader.ReadString());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("c0af", 0, false)]
    [InlineData("c180", 0, false)]
    [InlineData("c1bf", 0, false)]
    [InlineData("e080af", 0, false)]
    [InlineData("e09fbf", 0, false)]
    [InlineData("f08080af", 0, false)]
    [InlineData("f08fbfbf", 0, false)]
    [InlineData("eda080", 0, false)]
    [InlineData("edbfbf", 0, false)]
    [InlineData("eda0bdedb880", 0, false)]
    [InlineData("f4908080", 0, false)]
    [InlineData("f5808080", 0, false)]
    [InlineData("f7bfbfbf", 0, false)]
    [InlineData("f8", 0, false)]
    [InlineData("fe", 0, false)]
    [InlineData("ff", 0, false)]
    [InlineData("80", 0, false)]
    [InlineData("bf", 0, false)]
    [InlineData("6180", 1, false)]
    [InlineData("c3a9a9", 2, false)]
    [InlineData("c328", 0, false)]
    [InlineData("e28241", 0, false)]
    [InlineData("4142eda080", 2, false)]
    [InlineData("c3", 0, true)]
    [InlineData("e282", 0, true)]
    [InlineData("f09f98", 0, true)]
    [InlineData("61f09f98", 1, true)]
    public void Ill_formed_UTF8_is_rejected_at_the_start_of_the_input(string body, int badIndex, bool truncated)
    {
        var input = Hex.Bytes($"{body.Length / 2:x2}{body}");

        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(input);
            reader.ReadString();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(IllFormedMessage(0, 1 + badIndex, truncated), exception.Message);
    }

    [Theory]
    [InlineData("c0af", 0, false)]
    [InlineData("e080af", 0, false)]
    [InlineData("f08080af", 0, false)]
    [InlineData("eda080", 0, false)]
    [InlineData("edbfbf", 0, false)]
    [InlineData("f4908080", 0, false)]
    [InlineData("f5808080", 0, false)]
    [InlineData("c1bf", 0, false)]
    [InlineData("ff", 0, false)]
    [InlineData("6180", 1, false)]
    [InlineData("c328", 0, false)]
    [InlineData("e282", 0, true)]
    [InlineData("61f09f98", 1, true)]
    public void Ill_formed_UTF8_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string body, int badIndex, bool truncated)
    {
        // A bool, then the string, then one more byte, so the string does not end the input.
        var input = Hex.Bytes($"01{body.Length / 2:x2}{body}00");
        var reader = new OahuReader(input);
        Assert.True(reader.ReadBool());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadString();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(IllFormedMessage(1, 2 + badIndex, truncated), exception.Message);
        Assert.Equal(input.Length - 1, reader.Remaining);
    }

    [Theory]
    [InlineData("01", 1, 0)]
    [InlineData("0561", 5, 1)]
    [InlineData("8001" + "61", 128, 1)]
    [InlineData("ffffffff07" + "6162", int.MaxValue, 2)]
    public void A_length_larger_than_the_bytes_left_is_rejected(string hex, int length, int available)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadString();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            $"The string at byte offset 0 is {length} byte(s) long, but only {available} byte(s) remain after its length.",
            exception.Message);
    }

    [Fact]
    public void A_huge_length_after_the_start_is_rejected_without_allocating_it()
    {
        var input = Hex.Bytes("026f6b" + "ffffffff07" + "616263");
        var reader = new OahuReader(input);
        Assert.Equal("ok", reader.ReadString());

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        OahuFormatException? exception = null;
        try
        {
            reader.ReadString();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.NotNull(exception);
        Assert.Equal(3, exception.Offset);
        Assert.Equal(
            "The string at byte offset 3 is 2147483647 byte(s) long, but only 3 byte(s) remain after its length.",
            exception.Message);
        Assert.Equal(8, reader.Remaining);
        Assert.True(allocated < 1_000_000, $"The failed read allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData("", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("80", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("8080808008", "The count 2147483648 at byte offset 0 is larger than the largest count, 2147483647.")]
    public void A_malformed_length_is_rejected_as_a_count(string hex, string message)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadString();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void Writing_null_is_an_argument_error_and_writes_nothing()
    {
        var writer = new OahuWriter();
        writer.WriteInt(5);

        Assert.Throws<ArgumentNullException>(() => writer.WriteString(null!));
        Assert.Equal("0a", Hex.Of(writer.WrittenSpan));
    }

    // Attribute strings are stored as UTF-8, which cannot hold a lone surrogate, so these cases are given as chars. The
    // expected string read back is given as chars too, with U+FFFD where each lone surrogate was.
    [Theory]
    [InlineData(new[] { '\uD800' }, "03efbfbd", new[] { '\uFFFD' })]
    [InlineData(new[] { '\uDBFF' }, "03efbfbd", new[] { '\uFFFD' })]
    [InlineData(new[] { '\uDC00' }, "03efbfbd", new[] { '\uFFFD' })]
    [InlineData(new[] { '\uDFFF' }, "03efbfbd", new[] { '\uFFFD' })]
    [InlineData(new[] { '\uDC00', '\uD800' }, "06efbfbdefbfbd", new[] { '\uFFFD', '\uFFFD' })]
    [InlineData(new[] { 'a', '\uD800', 'b' }, "0561efbfbd62", new[] { 'a', '\uFFFD', 'b' })]
    [InlineData(new[] { 'a', 'b', '\uDC00', 'c', 'd' }, "076162efbfbd6364", new[] { 'a', 'b', '\uFFFD', 'c', 'd' })]
    [InlineData(new[] { '\uD800', 'b' }, "04efbfbd62", new[] { '\uFFFD', 'b' })]
    [InlineData(new[] { '\uDC00', 'b' }, "04efbfbd62", new[] { '\uFFFD', 'b' })]
    [InlineData(new[] { 'a', '\uD800' }, "0461efbfbd", new[] { 'a', '\uFFFD' })]
    [InlineData(new[] { 'a', '\uDC00' }, "0461efbfbd", new[] { 'a', '\uFFFD' })]
    [InlineData(new[] { '\uD83D', '\uDE00', '\uDE00' }, "07f09f9880efbfbd", new[] { '\uD83D', '\uDE00', '\uFFFD' })]
    [InlineData(new[] { '\uD800', '\uD83D', '\uDE00' }, "07efbfbdf09f9880", new[] { '\uFFFD', '\uD83D', '\uDE00' })]
    public void A_lone_surrogate_is_written_as_U_FFFD_and_read_back_as_it(char[] chars, string hex, char[] read)
    {
        var writer = new OahuWriter();
        writer.WriteString(new string(chars));

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(new string(read), reader.ReadString());
        reader.EnsureEnd();
    }

    [Fact]
    public void A_well_formed_pair_is_still_written_as_one_four_byte_character()
    {
        var value = new string(['\uD83D', '\uDE00']);
        var writer = new OahuWriter();
        writer.WriteString(value);

        Assert.Equal("04f09f9880", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadString());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(124, '\uD800', "7f")]
    [InlineData(125, '\uD800', "8001")]
    [InlineData(124, '\uDC00', "7f")]
    [InlineData(125, '\uDC00', "8001")]
    public void A_replaced_surrogate_counts_three_bytes_across_the_prefix_boundary(
        int ordinary, char surrogate, string prefixHex)
    {
        var value = new string('a', ordinary) + surrogate;
        var writer = new OahuWriter();
        writer.WriteString(value);

        var bodyHex = string.Concat(Enumerable.Repeat("61", ordinary)) + "efbfbd";
        Assert.Equal(prefixHex + bodyHex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(new string('a', ordinary) + '\uFFFD', reader.ReadString());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(256)]
    [InlineData(100_000)]
    public void A_replaced_surrogate_gives_the_same_bytes_whatever_the_buffer_capacity(int capacity)
    {
        string[] values =
        [
            new(['\uD800']), new(['\uDC00', '\uD800']), new string('x', 100) + '\uDC00',
            new string('x', 20_000) + '\uD800' + "y",
        ];

        var expected = new List<byte>(Hex.Bytes("8101" + "026f6b"));
        foreach (var value in values)
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            var lengthWriter = new OahuWriter();
            lengthWriter.WriteCount(utf8.Length);
            expected.AddRange(lengthWriter.WrittenSpan.ToArray());
            expected.AddRange(utf8);
        }

        var writer = new OahuWriter(capacity);
        writer.WriteInt(-65);
        writer.WriteString("ok");
        foreach (var value in values)
            writer.WriteString(value);

        Assert.Equal(expected.ToArray(), writer.WrittenSpan.ToArray());

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(-65, reader.ReadInt());
        Assert.Equal("ok", reader.ReadString());
        foreach (var value in values)
            Assert.Equal(value.Replace('\uD800', '\uFFFD').Replace('\uDC00', '\uFFFD'), reader.ReadString());
        reader.EnsureEnd();
    }

    [Fact]
    public void Strings_across_the_range_of_code_points_round_trip()
    {
        var values = new List<string>
        {
            "", " ", "\u0000\u0000", "\u007f", "\u0080", "߿", "ࠀ", "퟿", "", "�", "￿",
            "\U00010000", "\U0010FFFF", "﻿﻿", "line\nbreak\ttab", "日本語",
        };

        var random = new Random(20_261_125);
        for (var i = 0; i < 500; i++)
        {
            var builder = new StringBuilder();
            var runes = random.Next(0, 60);
            for (var j = 0; j < runes; j++)
            {
                var plane = random.Next(4);
                var scalar = plane switch
                {
                    0 => random.Next(0, 0x80),
                    1 => random.Next(0x80, 0x800),
                    2 => random.Next(0x800, 0x10000),
                    _ => random.Next(0x10000, 0x110000),
                };
                if (scalar is >= 0xD800 and <= 0xDFFF)
                    scalar = 0xFFFD;
                builder.Append(char.ConvertFromUtf32(scalar));
            }

            values.Add(builder.ToString());
        }

        var writer = new OahuWriter(3);
        foreach (var value in values)
            writer.WriteString(value);

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var value in values)
            Assert.Equal(value, reader.ReadString());
        reader.EnsureEnd();
    }

    [Fact]
    public void Strings_mixed_with_other_values_are_read_in_the_order_written()
    {
        var writer = new OahuWriter(1);
        writer.WriteString("Milk");
        writer.WriteBool(true);
        writer.WriteString("");
        writer.WriteInt(-65);
        writer.WriteString("€");
        writer.WriteDouble(1.5);
        writer.WriteString("a\u0000b");
        writer.WriteLong(1);
        writer.WriteCount(3);

        Assert.Equal(
            "044d696c6b" + "01" + "00" + "8101" + "03e282ac" + "000000000000f83f" + "03610062" + "02" + "03",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal("Milk", reader.ReadString());
        Assert.True(reader.ReadBool());
        Assert.Equal("", reader.ReadString());
        Assert.Equal(-65, reader.ReadInt());
        Assert.Equal("€", reader.ReadString());
        Assert.Equal(1.5, reader.ReadDouble());
        Assert.Equal("a\u0000b", reader.ReadString());
        Assert.Equal(1L, reader.ReadLong());
        Assert.Equal(3, reader.ReadCount());
        reader.EnsureEnd();
    }

    // Given as chars so that lone surrogates survive.
    public static TheoryData<char[]> ByteCountCases => new(
        [], ['a'], "Milk".ToCharArray(), ['\u007f'], ['\u0080'], ['\u07ff'], ['\u0800'], ['\uffff'], ['\u0000'],
        ['\uD83D', '\uDE00'], ['\uDBFF', '\uDFFF'], "héllo wörld €".ToCharArray(),
        ['\uD800'], ['\uDBFF'], ['\uDC00'], ['\uDFFF'], ['\uDC00', '\uD800'], ['\uD800', '\uD800'],
        ['a', '\uD800', 'b'], ['a', '\uD800'], ['\uD83D', '\uDE00', '\uDE00'], ['\uD800', '\uD83D', '\uDE00'],
        ['é', '\uDC00', '€', '\uD83D'], (new string('a', 100) + "é" + new string('b', 100)).ToCharArray());

    [Theory]
    [MemberData(nameof(ByteCountCases))]
    public void The_byte_count_matches_the_bytes_written_for_well_formed_and_ill_formed_strings(char[] chars)
    {
        var value = new string(chars);

        Assert.Equal(Encoding.UTF8.GetByteCount(value), OahuWriter.Utf8ByteCount(value));

        var writer = new OahuWriter();
        writer.WriteString(value);
        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(OahuWriter.Utf8ByteCount(value), reader.ReadCount());
    }

    [Fact]
    public void The_byte_count_of_random_strings_with_lone_surrogates_matches_the_encoder()
    {
        var random = new Random(20_261_125);
        for (var i = 0; i < 1_000; i++)
        {
            var chars = new char[random.Next(0, 40)];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = random.Next(5) switch
                {
                    0 => (char)random.Next(0, 0x80),
                    1 => (char)random.Next(0x80, 0x800),
                    2 => (char)random.Next(0x800, 0x10000),
                    3 => (char)random.Next(0xD800, 0xDC00),
                    _ => (char)random.Next(0xDC00, 0xE000),
                };
            }

            var value = new string(chars);
            Assert.Equal(Encoding.UTF8.GetByteCount(value), OahuWriter.Utf8ByteCount(value));
        }
    }

    [Fact]
    public void The_byte_count_of_a_string_the_writer_cannot_hold_is_a_long_beyond_the_limit()
    {
        // Pieces split between whole characters count to the count of the whole.
        var piece = new string('€', 1_000_000);
        var doubled = piece + piece;
        Assert.Equal(2 * OahuWriter.Utf8ByteCount(piece), OahuWriter.Utf8ByteCount(doubled));

        // So 715,827,883 euro signs, the shortest such string with more bytes than int range, count to this. It is
        // composed from pieces, so no such string is allocated.
        long count = 715 * OahuWriter.Utf8ByteCount(piece) + OahuWriter.Utf8ByteCount(new string('€', 827_883));
        Assert.Equal(2_147_483_649L, count);
        Assert.True(count > int.MaxValue);
        Assert.True(count > Array.MaxLength);
    }

    [Theory]
    [InlineData("Milk", 256)]
    [InlineData("héllo wörld", 256)]
    public void Writing_a_string_in_one_pass_allocates_nothing(string value, int capacity)
    {
        AssertWritesWithoutAllocating(value, capacity);
    }

    [Fact]
    public void Writing_a_string_with_a_lone_surrogate_in_one_pass_allocates_nothing()
    {
        AssertWritesWithoutAllocating(new string(['a', '\uD800', 'b']), 256);
    }

    [Fact]
    public void Writing_a_string_whose_length_is_counted_first_allocates_nothing()
    {
        // The upper bound, three bytes per char, does not fit the exact-size buffer, so the length is counted first.
        AssertWritesWithoutAllocating("aéb", 1 + 4);

        // The bounds take different numbers of length bytes: 43 and 129.
        AssertWritesWithoutAllocating(new string('€', 43), 1_000);
    }

    [Fact]
    public void Writing_a_string_with_a_lone_surrogate_whose_length_is_counted_first_allocates_nothing()
    {
        AssertWritesWithoutAllocating(new string(['a', '\uD800', 'b']), 1 + 5);
        AssertWritesWithoutAllocating(new string('x', 42) + '\uDC00', 1_000);
    }

    [Fact]
    public void Reading_the_empty_string_allocates_nothing()
    {
        byte[] input = [0];

        Assert.Equal(0, Allocations.Smallest(() => ReadOnce(input)));

        static void ReadOnce(byte[] input)
        {
            var reader = new OahuReader(input);
            Assert.Same(string.Empty, reader.ReadString());
        }
    }

    [Theory]
    [InlineData("e0a080", 0x0800)]
    [InlineData("e0bfbf", 0x0FFF)]
    [InlineData("ed8080", 0xD000)]
    [InlineData("ed9fbf", 0xD7FF)]
    [InlineData("ee8080", 0xE000)]
    [InlineData("f0908080", 0x10000)]
    [InlineData("f0bfbfbf", 0x3FFFF)]
    [InlineData("f4808080", 0x100000)]
    [InlineData("f48fbfbf", 0x10FFFF)]
    public void The_lowest_and_highest_sequences_after_each_special_lead_byte_are_read(string body, int codePoint)
    {
        var reader = new OahuReader(Hex.Bytes($"{body.Length / 2:x2}{body}"));
        var value = reader.ReadString();
        reader.EnsureEnd();

        Assert.Equal(char.ConvertFromUtf32(codePoint), value);
    }

    [Theory]
    [InlineData(2_097_151, 'a', "ffff7f")]
    [InlineData(2_097_152, 'a', "80808001")]
    [InlineData(699_050, '€', "feff7f")]
    [InlineData(699_051, '€', "81808001")]
    public void The_length_takes_four_bytes_from_2097152_bytes(int repeat, char unit, string prefixHex)
    {
        var value = new string(unit, repeat);
        var utf8 = Encoding.UTF8.GetBytes(value);

        var writer = new OahuWriter();
        writer.WriteString(value);

        var prefixSize = prefixHex.Length / 2;
        Assert.Equal(prefixSize + utf8.Length, writer.WrittenCount);
        Assert.Equal(prefixHex, Hex.Of(writer.WrittenSpan[..prefixSize]));
        Assert.True(writer.WrittenSpan[prefixSize..].SequenceEqual(utf8));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadString());
        reader.EnsureEnd();
    }

    private static void AssertWritesWithoutAllocating(string value, int capacity)
    {
        var writer = new OahuWriter(capacity);

        Assert.Equal(
            0,
            Allocations.Smallest(() =>
            {
                writer.Reset();
                writer.WriteString(value);
            }));
    }

    private static string IllFormedMessage(int start, int badOffset, bool truncated) =>
        $"The string at byte offset {start} is not well-formed UTF-8: the sequence at byte offset {badOffset} " +
        (truncated ? "is cut short by the end of the string." : "is ill-formed.");
}
