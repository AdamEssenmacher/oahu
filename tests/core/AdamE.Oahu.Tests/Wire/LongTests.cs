using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class LongTests
{
    [Theory]
    [InlineData(0L, "00")]
    [InlineData(-1L, "01")]
    [InlineData(1L, "02")]
    [InlineData(-2L, "03")]
    [InlineData(2L, "04")]
    [InlineData(-3L, "05")]
    [InlineData(3L, "06")]
    [InlineData(63L, "7e")]
    [InlineData(-64L, "7f")]
    [InlineData(64L, "8001")]
    [InlineData(-65L, "8101")]
    [InlineData(8_191L, "fe7f")]
    [InlineData(-8_192L, "ff7f")]
    [InlineData(8_192L, "808001")]
    [InlineData(-8_193L, "818001")]
    [InlineData(1_048_575L, "feff7f")]
    [InlineData(-1_048_576L, "ffff7f")]
    [InlineData(1_048_576L, "80808001")]
    [InlineData(-1_048_577L, "81808001")]
    [InlineData(134_217_727L, "feffff7f")]
    [InlineData(-134_217_728L, "ffffff7f")]
    [InlineData(134_217_728L, "8080808001")]
    [InlineData(-134_217_729L, "8180808001")]
    [InlineData(2_147_483_647L, "feffffff0f")]
    [InlineData(-2_147_483_648L, "ffffffff0f")]
    [InlineData(2_147_483_648L, "8080808010")]
    [InlineData(-2_147_483_649L, "8180808010")]
    [InlineData(17_179_869_183L, "feffffff7f")]
    [InlineData(-17_179_869_184L, "ffffffff7f")]
    [InlineData(17_179_869_184L, "808080808001")]
    [InlineData(-17_179_869_185L, "818080808001")]
    [InlineData(2_199_023_255_551L, "feffffffff7f")]
    [InlineData(-2_199_023_255_552L, "ffffffffff7f")]
    [InlineData(2_199_023_255_552L, "80808080808001")]
    [InlineData(-2_199_023_255_553L, "81808080808001")]
    [InlineData(281_474_976_710_655L, "feffffffffff7f")]
    [InlineData(-281_474_976_710_656L, "ffffffffffff7f")]
    [InlineData(281_474_976_710_656L, "8080808080808001")]
    [InlineData(-281_474_976_710_657L, "8180808080808001")]
    [InlineData(36_028_797_018_963_967L, "feffffffffffff7f")]
    [InlineData(-36_028_797_018_963_968L, "ffffffffffffff7f")]
    [InlineData(36_028_797_018_963_968L, "808080808080808001")]
    [InlineData(-36_028_797_018_963_969L, "818080808080808001")]
    [InlineData(4_611_686_018_427_387_903L, "feffffffffffffff7f")]
    [InlineData(-4_611_686_018_427_387_904L, "ffffffffffffffff7f")]
    [InlineData(4_611_686_018_427_387_904L, "80808080808080808001")]
    [InlineData(-4_611_686_018_427_387_905L, "81808080808080808001")]
    [InlineData(long.MaxValue, "feffffffffffffffff01")]
    [InlineData(long.MinValue, "ffffffffffffffffff01")]
    public void A_long_is_written_zigzag_encoded_in_its_shortest_form_and_read_back(long value, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteLong(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(value, reader.ReadLong());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("8000", 0L)]
    [InlineData("818000", -1L)]
    [InlineData("82808080808080808000", 1L)]
    public void A_longer_than_shortest_encoding_is_accepted(string hex, long value)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        Assert.Equal(value, reader.ReadLong());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("ffffffffffffffffff", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("ffffffffffffffffff02", "The variable-length integer at byte offset 0 does not fit in 64 bits.")]
    [InlineData("ffffffffffffffffff8101", "The variable-length integer at byte offset 0 is longer than 10 bytes.")]
    public void A_malformed_long_is_rejected(string hex, string message)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadLong();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("01ffff", "The input ends inside a variable-length integer that starts at byte offset 1.")]
    [InlineData("01ffffffffffffffffff02", "The variable-length integer at byte offset 1 does not fit in 64 bits.")]
    [InlineData("01ffffffffffffffffff8101", "The variable-length integer at byte offset 1 is longer than 10 bytes.")]
    public void A_rejected_long_after_the_start_names_its_offset_and_does_not_move_the_reader(string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);
        Assert.Equal(-1L, reader.ReadLong());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadLong();
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
    public void Values_across_the_whole_range_round_trip()
    {
        var values = new List<long> { 0, long.MinValue, long.MaxValue };
        for (var bits = 0; bits < 63; bits++)
        {
            var power = 1L << bits;
            values.AddRange([power - 1, power, power + 1, -power, -power - 1, -power + 1]);
        }

        var random = new Random(20_261_125);
        for (var i = 0; i < 1_000; i++)
            values.Add(random.NextInt64(long.MinValue, long.MaxValue) >> random.Next(0, 64));

        var writer = new OahuWriter();
        foreach (var value in values)
            writer.WriteLong(value);

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var value in values)
            Assert.Equal(value, reader.ReadLong());
        reader.EnsureEnd();
    }
}
