using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class IndexTests
{
    [Theory]
    [InlineData(0, 1, "00")]
    [InlineData(0, 3, "00")]
    [InlineData(1, 3, "01")]
    [InlineData(127, 128, "7f")]
    [InlineData(128, 129, "8001")]
    [InlineData(128, 1000, "8001")]
    [InlineData(int.MaxValue - 1, int.MaxValue, "feffffff07")]
    public void An_index_is_written_as_a_variable_length_integer_and_read_back(int index, int count, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteIndex(index, count);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(index, reader.ReadIndex(count));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_reader_accepts_a_longer_form_of_an_index_than_the_shortest()
    {
        var reader = new OahuReader(Hex.Bytes("8100"));

        Assert.Equal(1, reader.ReadIndex(2));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("03", 3, "The index 3 at byte offset 0 is not below 3, the number of values or cases of its type.")]
    [InlineData("01", 1, "The index 1 at byte offset 0 is not below 1, the number of values or cases of its type.")]
    [InlineData("07", 3, "The index 7 at byte offset 0 is not below 3, the number of values or cases of its type.")]
    [InlineData(
        "8001",
        128,
        "The index 128 at byte offset 0 is not below 128, the number of values or cases of its type.")]
    [InlineData("8001", 3, "The index 128 at byte offset 0 is not below 3, the number of values or cases of its type.")]
    [InlineData(
        "ffffffffffffffffff01",
        3,
        "The index 18446744073709551615 at byte offset 0 is not below 3, the number of values or cases of its type.")]
    public void An_index_that_is_not_below_the_count_is_rejected_and_does_not_move_the_reader(
        string hex, int count, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        var exception = ReadIndexFails(ref reader, count);

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Theory]
    [InlineData(
        "0203",
        3,
        "The index 3 at byte offset 1 is not below 3, the number of values or cases of its type.",
        1)]
    [InlineData(
        "02040000",
        3,
        "The index 4 at byte offset 1 is not below 3, the number of values or cases of its type.",
        3)]
    [InlineData(
        "02800100",
        2,
        "The index 128 at byte offset 1 is not below 2, the number of values or cases of its type.",
        3)]
    public void An_index_out_of_range_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, int count, string message, int remaining)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(2, reader.ReadCount());

        var exception = ReadIndexFails(ref reader, count);

        Assert.Equal(1, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(remaining, reader.Remaining);
    }

    [Theory]
    [InlineData("", 0, "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("80", 0, "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("0180", 1, "The input ends inside a variable-length integer that starts at byte offset 1.")]
    [InlineData("ffffffffffffffffff8101", 0, "The variable-length integer at byte offset 0 is longer than 10 bytes.")]
    [InlineData("ffffffffffffffffff02", 0, "The variable-length integer at byte offset 0 does not fit in 64 bits.")]
    public void A_malformed_variable_length_integer_is_rejected_as_an_index(string hex, int offset, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);
        if (offset == 1)
            Assert.Equal(1, reader.ReadCount());

        var exception = ReadIndexFails(ref reader, 3);

        Assert.Equal(offset, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(input.Length - offset, reader.Remaining);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, int.MinValue)]
    public void Writing_an_index_for_a_count_below_1_is_an_argument_error_and_writes_nothing(int index, int count)
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteIndex(index, count));

        Assert.Equal("count", exception.ParamName);
        Assert.Equal(0, writer.WrittenCount);
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(int.MinValue, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Writing_an_index_outside_the_count_is_an_argument_error_and_writes_nothing(int index, int count)
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteIndex(index, count));

        Assert.Equal("index", exception.ParamName);
        Assert.Equal(0, writer.WrittenCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Reading_an_index_for_a_count_below_1_is_an_argument_error_and_does_not_move_the_reader(int count)
    {
        var reader = new OahuReader(Hex.Bytes("00"));

        ArgumentOutOfRangeException? exception = null;
        try
        {
            reader.ReadIndex(count);
        }
        catch (ArgumentOutOfRangeException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal("count", exception.ParamName);
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void Writing_and_reading_an_index_allocate_nothing()
    {
        var writer = new OahuWriter();

        Assert.Equal(0, Allocations.Smallest(() => WriteAndRead(writer)));

        static void WriteAndRead(OahuWriter writer)
        {
            writer.Reset();
            writer.WriteIndex(0, 3);
            writer.WriteIndex(128, 200);
            var reader = new OahuReader(writer.WrittenSpan);
            Assert.True(reader.ReadIndex(3) == 0);
            Assert.True(reader.ReadIndex(200) == 128);
        }
    }

    private static OahuFormatException ReadIndexFails(ref OahuReader reader, int count)
    {
        OahuFormatException? exception = null;
        try
        {
            reader.ReadIndex(count);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        return exception;
    }
}
