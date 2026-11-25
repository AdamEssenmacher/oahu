using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class CountTests
{
    [Theory]
    [InlineData(0, "00")]
    [InlineData(1, "01")]
    [InlineData(127, "7f")]
    [InlineData(128, "8001")]
    [InlineData(int.MaxValue, "ffffffff07")]
    public void A_count_is_written_as_a_variable_length_integer_and_read_back(int count, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteCount(count);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(count, reader.ReadCount());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Writing_a_negative_count_is_an_argument_error_and_writes_nothing(int count)
    {
        var writer = new OahuWriter();

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteCount(count));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void A_count_just_over_int_MaxValue_is_rejected()
    {
        var writer = new OahuWriter();
        writer.WriteVarUInt((ulong)int.MaxValue + 1);
        var input = writer.WrittenSpan.ToArray();

        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(input);
            reader.ReadCount();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The count 2147483648 at byte offset 0 is larger than the largest count, 2147483647.", exception.Message);
    }

    [Fact]
    public void A_count_of_the_largest_variable_length_integer_is_rejected()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("ffffffffffffffffff01"));
            reader.ReadCount();
        });

        Assert.Equal(0, exception.Offset);
    }

    [Fact]
    public void A_rejected_count_does_not_move_the_reader()
    {
        var reader = new OahuReader(Hex.Bytes("0780808080080a"));
        Assert.Equal(7, reader.ReadCount());

        try
        {
            reader.ReadCount();
            Assert.Fail("The read should have thrown.");
        }
        catch (OahuFormatException exception)
        {
            Assert.Equal(1, exception.Offset);
        }

        Assert.Equal(6, reader.Remaining);
    }

    [Fact]
    public void A_malformed_integer_is_rejected_as_a_count_too()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("80"));
            reader.ReadCount();
        });

        Assert.Equal(
            "The input ends inside a variable-length integer that starts at byte offset 0.", exception.Message);
    }
}
