using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class VarUIntTests
{
    [Theory]
    [InlineData(0UL, "00")]
    [InlineData(1UL, "01")]
    [InlineData(127UL, "7f")]
    [InlineData(128UL, "8001")]
    [InlineData(300UL, "ac02")]
    [InlineData(16_383UL, "ff7f")]
    [InlineData(16_384UL, "808001")]
    [InlineData(2_097_151UL, "ffff7f")]
    [InlineData(2_097_152UL, "80808001")]
    [InlineData(268_435_455UL, "ffffff7f")]
    [InlineData(268_435_456UL, "8080808001")]
    [InlineData(4_294_967_295UL, "ffffffff0f")]
    [InlineData(34_359_738_367UL, "ffffffff7f")]
    [InlineData(34_359_738_368UL, "808080808001")]
    [InlineData(4_398_046_511_103UL, "ffffffffff7f")]
    [InlineData(4_398_046_511_104UL, "80808080808001")]
    [InlineData(562_949_953_421_311UL, "ffffffffffff7f")]
    [InlineData(562_949_953_421_312UL, "8080808080808001")]
    [InlineData(72_057_594_037_927_935UL, "ffffffffffffff7f")]
    [InlineData(72_057_594_037_927_936UL, "808080808080808001")]
    [InlineData(9_223_372_036_854_775_807UL, "ffffffffffffffff7f")]
    [InlineData(9_223_372_036_854_775_808UL, "80808080808080808001")]
    [InlineData(ulong.MaxValue, "ffffffffffffffffff01")]
    public void A_value_is_written_in_its_shortest_form_and_read_back(ulong value, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteVarUInt(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));
        Assert.Equal(hex.Length / 2, writer.WrittenCount);

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(value, reader.ReadVarUInt());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(63)]
    [InlineData(64)]
    public void The_length_is_one_byte_for_each_seven_bits_of_the_value(int bits)
    {
        // The largest value with this many significant bits.
        var value = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        var expectedLength = Math.Max(1, (bits + 6) / 7);

        var writer = new OahuWriter();
        writer.WriteVarUInt(value);

        Assert.Equal(expectedLength, writer.WrittenCount);
    }

    [Theory]
    [InlineData("8000", 0UL)]
    [InlineData("818000", 1UL)]
    [InlineData("ff80808000", 127UL)]
    [InlineData("80808080808080808000", 0UL)]
    public void A_longer_than_shortest_encoding_is_accepted(string hex, ulong value)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        Assert.Equal(value, reader.ReadVarUInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("ffff")]
    [InlineData("808080808080808080")]
    public void Input_that_ends_inside_the_integer_is_rejected(string hex)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadVarUInt();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The input ends inside a variable-length integer that starts at byte offset 0.", exception.Message);
    }

    [Theory]
    [InlineData("ffffffffffffffffff02")]
    [InlineData("ffffffffffffffffff7f")]
    [InlineData("80808080808080808002")]
    public void A_ten_byte_integer_whose_last_byte_is_more_than_one_is_rejected(string hex)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadVarUInt();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal("The variable-length integer at byte offset 0 does not fit in 64 bits.", exception.Message);
    }

    [Theory]
    [InlineData("ffffffffffffffffff8101")]
    [InlineData("8080808080808080808000")]
    public void An_integer_longer_than_ten_bytes_is_rejected(string hex)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadVarUInt();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal("The variable-length integer at byte offset 0 is longer than 10 bytes.", exception.Message);
    }

    [Fact]
    public void An_error_names_the_offset_where_the_integer_starts()
    {
        // 300, then 1, then an integer cut short at byte offset 3.
        var input = Hex.Bytes("ac020180");

        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(input);
            reader.ReadVarUInt();
            reader.ReadVarUInt();
            reader.ReadVarUInt();
        });

        Assert.Equal(3, exception.Offset);
        Assert.Contains("byte offset 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_read_does_not_move_the_reader()
    {
        var reader = new OahuReader(Hex.Bytes("0180"));
        reader.ReadVarUInt();

        try
        {
            reader.ReadVarUInt();
            Assert.Fail("The read should have thrown.");
        }
        catch (OahuFormatException)
        {
        }

        Assert.Equal(1, reader.Remaining);
    }

    [Theory]
    [InlineData("01ffffffffffffffffff8101", "The variable-length integer at byte offset 1 is longer than 10 bytes.")]
    [InlineData("01ffffffffffffffffff02", "The variable-length integer at byte offset 1 does not fit in 64 bits.")]
    public void A_failed_read_after_the_start_names_its_offset_and_does_not_move_the_reader(string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);
        reader.ReadVarUInt();

        OahuFormatException? exception = null;
        try
        {
            reader.ReadVarUInt();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(input.Length - 1, reader.Remaining);
    }

    [Fact]
    public void Reading_at_the_end_of_the_input_is_rejected()
    {
        var reader = new OahuReader(Hex.Bytes("01"));
        reader.ReadVarUInt();

        OahuFormatException? exception = null;
        try
        {
            reader.ReadVarUInt();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(
            "The input ends inside a variable-length integer that starts at byte offset 1.", exception.Message);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void A_one_byte_integer_after_the_start_is_read()
    {
        var reader = new OahuReader(Hex.Bytes("ac027f00"));
        reader.ReadVarUInt();

        Assert.Equal(127UL, reader.ReadVarUInt());
        Assert.Equal(1, reader.Remaining);
        Assert.Equal(0UL, reader.ReadVarUInt());
        reader.EnsureEnd();
    }

    [Fact]
    public void Values_across_the_whole_range_round_trip()
    {
        var values = new List<ulong> { 0, ulong.MaxValue };
        for (var bits = 1; bits < 64; bits++)
        {
            values.Add((1UL << bits) - 1);
            values.Add(1UL << bits);
            values.Add((1UL << bits) + 1);
        }

        var random = new Random(20_261_125);
        for (var i = 0; i < 1_000; i++)
        {
            var bits = random.Next(1, 65);
            values.Add((ulong)random.NextInt64() >> (64 - bits) | (1UL << (bits - 1)));
        }

        var writer = new OahuWriter();
        foreach (var value in values)
            writer.WriteVarUInt(value);

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var value in values)
            Assert.Equal(value, reader.ReadVarUInt());
        reader.EnsureEnd();
    }
}
