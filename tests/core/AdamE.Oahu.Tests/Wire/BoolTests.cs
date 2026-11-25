using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class BoolTests
{
    [Theory]
    [InlineData(false, "00")]
    [InlineData(true, "01")]
    public void A_bool_is_written_as_one_byte_and_read_back(bool value, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteBool(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadBool());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("02", "The bool at byte offset 0 is 0x02, which is neither 0x00 nor 0x01.")]
    [InlineData("80", "The bool at byte offset 0 is 0x80, which is neither 0x00 nor 0x01.")]
    [InlineData("ff", "The bool at byte offset 0 is 0xff, which is neither 0x00 nor 0x01.")]
    [InlineData("", "The input ends before the bool at byte offset 0.")]
    public void A_malformed_bool_is_rejected(string hex, string message)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadBool();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("0102", "The bool at byte offset 1 is 0x02, which is neither 0x00 nor 0x01.", 1)]
    [InlineData("01", "The input ends before the bool at byte offset 1.", 0)]
    public void A_malformed_bool_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, string message, int remaining)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.True(reader.ReadBool());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadBool();
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
}
