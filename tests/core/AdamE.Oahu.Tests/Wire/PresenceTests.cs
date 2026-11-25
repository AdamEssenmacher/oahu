using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class PresenceTests
{
    [Theory]
    [InlineData(false, "00")]
    [InlineData(true, "01")]
    public void A_presence_byte_is_written_as_one_byte_and_read_back(bool present, string hex)
    {
        var writer = new OahuWriter();
        writer.WritePresence(present);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(present, reader.ReadPresence());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("02", "The presence byte at byte offset 0 is 0x02, which is neither 0x00 nor 0x01.")]
    [InlineData("7f", "The presence byte at byte offset 0 is 0x7f, which is neither 0x00 nor 0x01.")]
    [InlineData("80", "The presence byte at byte offset 0 is 0x80, which is neither 0x00 nor 0x01.")]
    [InlineData("ff", "The presence byte at byte offset 0 is 0xff, which is neither 0x00 nor 0x01.")]
    [InlineData("", "The input ends before the presence byte at byte offset 0.")]
    public void A_malformed_presence_byte_is_rejected(string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        OahuFormatException? exception = null;
        try
        {
            reader.ReadPresence();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Theory]
    [InlineData("0102", "The presence byte at byte offset 1 is 0x02, which is neither 0x00 nor 0x01.", 1)]
    [InlineData("017f", "The presence byte at byte offset 1 is 0x7f, which is neither 0x00 nor 0x01.", 1)]
    [InlineData("0180", "The presence byte at byte offset 1 is 0x80, which is neither 0x00 nor 0x01.", 1)]
    [InlineData("01ff00", "The presence byte at byte offset 1 is 0xff, which is neither 0x00 nor 0x01.", 2)]
    [InlineData("01", "The input ends before the presence byte at byte offset 1.", 0)]
    public void A_malformed_presence_byte_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, string message, int remaining)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.True(reader.ReadPresence());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadPresence();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(remaining, reader.Remaining);
    }

    [Theory]
    [InlineData(null, "00")]
    [InlineData(0, "0100")]
    [InlineData(5, "010a")]
    [InlineData(-65, "018101")]
    [InlineData(int.MinValue, "01ffffffff0f")]
    public void A_nullable_int_is_a_presence_byte_then_the_int_when_present(int? value, string hex)
    {
        var writer = new OahuWriter();
        WriteNullableInt(writer, value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, ReadNullableInt(ref reader));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(null, "00")]
    [InlineData("", "0100")]
    [InlineData("x", "010178")]
    [InlineData("Milk", "01044d696c6b")]
    public void A_nullable_string_is_a_presence_byte_then_the_string_when_present(string? value, string hex)
    {
        var writer = new OahuWriter();
        WriteNullableString(writer, value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, ReadNullableString(ref reader));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_nullable_double_is_a_presence_byte_then_the_double_when_present()
    {
        double?[] values = [null, 1.5, -0.0];
        string[] hexes = ["00", "01" + "000000000000f83f", "01" + "0000000000000080"];

        for (var i = 0; i < values.Length; i++)
        {
            var writer = new OahuWriter();
            WriteNullableDouble(writer, values[i]);

            Assert.Equal(hexes[i], Hex.Of(writer.WrittenSpan));

            var reader = new OahuReader(writer.WrittenSpan);
            var read = ReadNullableDouble(ref reader);
            Assert.Equal(values[i].HasValue, read.HasValue);
            if (values[i] is { } expected)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(read!.Value));
            reader.EnsureEnd();
        }
    }

    [Fact]
    public void An_absent_value_has_no_bytes_after_its_presence_byte()
    {
        var writer = new OahuWriter();
        WriteNullableInt(writer, null);
        WriteNullableString(writer, "x");
        WriteNullableDouble(writer, null);
        WriteNullableInt(writer, 1);

        Assert.Equal("00" + "010178" + "00" + "0102", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Null(ReadNullableInt(ref reader));
        Assert.Equal("x", ReadNullableString(ref reader));
        Assert.Null(ReadNullableDouble(ref reader));
        Assert.Equal(1, ReadNullableInt(ref reader));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_present_value_that_is_missing_is_rejected_by_the_value_read()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("01"));
            ReadNullableInt(ref reader);
        });

        Assert.Equal(1, exception.Offset);
        Assert.Equal(
            "The input ends inside a variable-length integer that starts at byte offset 1.", exception.Message);
    }

    [Fact]
    public void A_bad_presence_byte_before_a_value_is_rejected()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("0205"));
            ReadNullableInt(ref reader);
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The presence byte at byte offset 0 is 0x02, which is neither 0x00 nor 0x01.", exception.Message);
    }

    [Fact]
    public void Writing_and_reading_a_presence_byte_allocate_nothing()
    {
        var writer = new OahuWriter();

        Assert.Equal(0, Allocations.Smallest(() => WriteAndRead(writer)));

        static void WriteAndRead(OahuWriter writer)
        {
            writer.Reset();
            WriteNullableInt(writer, null);
            WriteNullableInt(writer, 7);
            var reader = new OahuReader(writer.WrittenSpan);
            Assert.True(ReadNullableInt(ref reader) is null);
            Assert.True(ReadNullableInt(ref reader) == 7);
        }
    }

    private static void WriteNullableInt(OahuWriter writer, int? value)
    {
        writer.WritePresence(value.HasValue);
        if (value is { } present)
            writer.WriteInt(present);
    }

    private static int? ReadNullableInt(ref OahuReader reader) => reader.ReadPresence() ? reader.ReadInt() : null;

    private static void WriteNullableString(OahuWriter writer, string? value)
    {
        writer.WritePresence(value is not null);
        if (value is not null)
            writer.WriteString(value);
    }

    private static string? ReadNullableString(ref OahuReader reader) =>
        reader.ReadPresence() ? reader.ReadString() : null;

    private static void WriteNullableDouble(OahuWriter writer, double? value)
    {
        writer.WritePresence(value.HasValue);
        if (value is { } present)
            writer.WriteDouble(present);
    }

    private static double? ReadNullableDouble(ref OahuReader reader) =>
        reader.ReadPresence() ? reader.ReadDouble() : null;
}
