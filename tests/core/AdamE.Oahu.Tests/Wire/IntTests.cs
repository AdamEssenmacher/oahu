using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class IntTests
{
    [Theory]
    [InlineData(0, "00")]
    [InlineData(-1, "01")]
    [InlineData(1, "02")]
    [InlineData(-2, "03")]
    [InlineData(2, "04")]
    [InlineData(-3, "05")]
    [InlineData(3, "06")]
    [InlineData(63, "7e")]
    [InlineData(-64, "7f")]
    [InlineData(64, "8001")]
    [InlineData(-65, "8101")]
    [InlineData(300, "d804")]
    [InlineData(-300, "d704")]
    [InlineData(8_191, "fe7f")]
    [InlineData(-8_192, "ff7f")]
    [InlineData(8_192, "808001")]
    [InlineData(-8_193, "818001")]
    [InlineData(1_000_000, "80897a")]
    [InlineData(-1_000_000, "ff887a")]
    [InlineData(1_048_575, "feff7f")]
    [InlineData(-1_048_576, "ffff7f")]
    [InlineData(1_048_576, "80808001")]
    [InlineData(-1_048_577, "81808001")]
    [InlineData(134_217_727, "feffff7f")]
    [InlineData(-134_217_728, "ffffff7f")]
    [InlineData(134_217_728, "8080808001")]
    [InlineData(-134_217_729, "8180808001")]
    [InlineData(int.MaxValue, "feffffff0f")]
    [InlineData(int.MinValue, "ffffffff0f")]
    public void An_int_is_written_zigzag_encoded_in_its_shortest_form_and_read_back(int value, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteInt(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(value, reader.ReadInt());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("8000", 0)]
    [InlineData("8180808000", -1)]
    [InlineData("80808080808080808000", 0)]
    [InlineData("ffffffff8f00", int.MinValue)]
    [InlineData("feffffff8f8080808000", int.MaxValue)]
    public void A_longer_than_shortest_encoding_is_accepted_when_its_value_fits(string hex, int value)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        Assert.Equal(value, reader.ReadInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("8080808010", 4_294_967_296UL)]
    [InlineData("ffffffff1f", 8_589_934_591UL)]
    [InlineData("ffffffffffffffffff01", ulong.MaxValue)]
    public void An_int_whose_value_does_not_fit_in_32_bits_is_rejected(string hex, ulong value)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadInt();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            $"The int at byte offset 0 does not fit in 32 bits: its zigzag-encoded value {value} is larger than " +
            "4294967295.",
            exception.Message);
    }

    [Fact]
    public void A_long_form_of_a_value_over_the_limit_is_rejected_as_an_int()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("808080809080808000"));
            reader.ReadInt();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The int at byte offset 0 does not fit in 32 bits: its zigzag-encoded value 4294967296 is larger than " +
            "4294967295.",
            exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("ffffffff")]
    public void Input_that_ends_inside_the_int_is_rejected(string hex)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadInt();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The input ends inside a variable-length integer that starts at byte offset 0.", exception.Message);
    }

    [Theory]
    [InlineData(
        "028080808010",
        "The int at byte offset 1 does not fit in 32 bits: its zigzag-encoded value 4294967296 is larger than 4294967295.")]
    [InlineData("02ffff", "The input ends inside a variable-length integer that starts at byte offset 1.")]
    [InlineData("02ffffffffffffffffff02", "The variable-length integer at byte offset 1 does not fit in 64 bits.")]
    public void A_rejected_int_after_the_start_names_its_offset_and_does_not_move_the_reader(string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);
        Assert.Equal(1, reader.ReadInt());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadInt();
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

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-134_217_729)]
    [InlineData(-134_217_728)]
    [InlineData(-1_048_577)]
    [InlineData(-8_193)]
    [InlineData(-65)]
    [InlineData(-64)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(8_192)]
    [InlineData(1_048_576)]
    [InlineData(134_217_728)]
    [InlineData(int.MaxValue)]
    public void An_int_and_a_long_with_the_same_value_have_the_same_bytes(int value)
    {
        var intWriter = new OahuWriter();
        intWriter.WriteInt(value);
        var longWriter = new OahuWriter();
        longWriter.WriteLong(value);

        Assert.Equal(Hex.Of(longWriter.WrittenSpan), Hex.Of(intWriter.WrittenSpan));
    }

    [Fact]
    public void Values_across_the_whole_range_round_trip()
    {
        var values = new List<int> { 0, int.MinValue, int.MaxValue };
        for (var bits = 0; bits < 31; bits++)
        {
            var power = 1 << bits;
            values.AddRange([power - 1, power, power + 1, -power, -power - 1, -power + 1]);
        }

        var random = new Random(20_261_125);
        for (var i = 0; i < 1_000; i++)
            values.Add(random.Next(int.MinValue, int.MaxValue) >> random.Next(0, 32));

        var writer = new OahuWriter();
        foreach (var value in values)
            writer.WriteInt(value);

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var value in values)
            Assert.Equal(value, reader.ReadInt());
        reader.EnsureEnd();
    }
}
