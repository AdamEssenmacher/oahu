using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class DoubleTests
{
    [Theory]
    [InlineData(0x0000000000000000UL, "0000000000000000")]
    [InlineData(0x8000000000000000UL, "0000000000000080")]
    [InlineData(0x3FF0000000000000UL, "000000000000f03f")]
    [InlineData(0x3FF8000000000000UL, "000000000000f83f")]
    [InlineData(0xC004000000000000UL, "00000000000004c0")]
    [InlineData(0x400921FB54442D18UL, "182d4454fb210940")]
    [InlineData(0x0000000000000001UL, "0100000000000000")]
    [InlineData(0x8000000000000001UL, "0100000000000080")]
    [InlineData(0x0010000000000000UL, "0000000000001000")]
    [InlineData(0x7FEFFFFFFFFFFFFFUL, "ffffffffffffef7f")]
    [InlineData(0xFFEFFFFFFFFFFFFFUL, "ffffffffffffefff")]
    [InlineData(0x7FF0000000000000UL, "000000000000f07f")]
    [InlineData(0xFFF0000000000000UL, "000000000000f0ff")]
    [InlineData(0x7FF8000000000000UL, "000000000000f87f")]
    [InlineData(0xFFF8000000000000UL, "000000000000f8ff")]
    [InlineData(0x7FF8DEADBEEF1234UL, "3412efbeaddef87f")]
    [InlineData(0x7FF0000000000001UL, "010000000000f07f")]
    [InlineData(0xFFF4000000C0FFEEUL, "eeffc0000000f4ff")]
    [InlineData(0xFFFFFFFFFFFFFFFFUL, "ffffffffffffffff")]
    public void A_double_is_written_as_its_bits_little_endian_and_read_back_exactly(ulong bits, string hex)
    {
        var value = BitConverter.UInt64BitsToDouble(bits);

        var writer = new OahuWriter();
        writer.WriteDouble(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal((long)bits, BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        reader.EnsureEnd();
    }

    [Fact]
    public void The_two_zeros_are_written_differently()
    {
        var writer = new OahuWriter();
        writer.WriteDouble(0.0);
        writer.WriteDouble(-0.0);

        Assert.Equal("00000000000000000000000000000080", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.Epsilon)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    [InlineData(double.NaN)]
    [InlineData(1e-300)]
    [InlineData(-123_456.789)]
    public void Special_and_extreme_values_round_trip(double value)
    {
        var writer = new OahuWriter();
        writer.WriteDouble(value);

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_quiet_and_a_signalling_NaN_keep_their_payloads()
    {
        // The quiet bit is the top bit of the fraction; a signalling NaN has it clear and a payload that is not zero.
        var quiet = BitConverter.Int64BitsToDouble(0x7FF8_0000_0001_2345);
        var signalling = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF0_0000_0006_7890));
        Assert.True(double.IsNaN(quiet));
        Assert.True(double.IsNaN(signalling));

        var writer = new OahuWriter();
        writer.WriteDouble(quiet);
        writer.WriteDouble(signalling);

        Assert.Equal("452301000000f87f907806000000f0ff", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(0x7FF8_0000_0001_2345, BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        Assert.Equal(unchecked((long)0xFFF0_0000_0006_7890), BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("00000000000000")]
    public void Input_that_ends_inside_the_double_is_rejected(string hex)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadDouble();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal("The input ends inside a double that starts at byte offset 0.", exception.Message);
    }

    [Fact]
    public void A_truncated_double_after_the_start_names_its_offset_and_does_not_move_the_reader()
    {
        var input = Hex.Bytes("000000000000f03f00000000000000");
        var reader = new OahuReader(input);
        Assert.Equal(1.0, reader.ReadDouble());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadDouble();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(8, exception.Offset);
        Assert.Equal("The input ends inside a double that starts at byte offset 8.", exception.Message);
        Assert.Equal(7, reader.Remaining);
    }

    [Fact]
    public void Random_bit_patterns_round_trip_exactly()
    {
        var random = new Random(20_261_125);
        var patterns = new long[1_000];
        for (var i = 0; i < patterns.Length; i++)
            patterns[i] = random.NextInt64(long.MinValue, long.MaxValue);

        var writer = new OahuWriter();
        foreach (var bits in patterns)
            writer.WriteDouble(BitConverter.Int64BitsToDouble(bits));

        Assert.Equal(patterns.Length * 8, writer.WrittenCount);

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var bits in patterns)
            Assert.Equal(bits, BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        reader.EnsureEnd();
    }
}
