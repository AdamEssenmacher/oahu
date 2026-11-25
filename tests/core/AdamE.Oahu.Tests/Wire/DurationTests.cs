using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class DurationTests
{
    [Theory]
    [InlineData(0L, "00")]
    [InlineData(1L, "02")]
    [InlineData(-1L, "01")]
    [InlineData(-7L, "0d")]
    [InlineData(10_000L, "a09c01")]
    [InlineData(-10_000L, "9f9c01")]
    [InlineData(36_000_000_000L, "80a0a39c8c02")]
    [InlineData(864_000_000_000L, "8080cea6a532")]
    [InlineData(long.MaxValue, "feffffffffffffffff01")]
    [InlineData(long.MinValue, "ffffffffffffffffff01")]
    public void A_duration_is_written_as_its_ticks_and_read_back_exactly(long ticks, string hex)
    {
        var value = TimeSpan.FromTicks(ticks);

        var writer = new OahuWriter();
        writer.WriteDuration(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.Equal(ticks, reader.ReadDuration().Ticks);
        reader.EnsureEnd();
    }

    [Fact]
    public void The_named_durations_have_the_expected_ticks()
    {
        var writer = new OahuWriter();
        writer.WriteDuration(TimeSpan.Zero);
        writer.WriteDuration(TimeSpan.FromTicks(1));
        writer.WriteDuration(TimeSpan.FromTicks(-1));
        writer.WriteDuration(TimeSpan.FromMilliseconds(1));
        writer.WriteDuration(TimeSpan.FromHours(1));
        writer.WriteDuration(TimeSpan.FromDays(1));
        writer.WriteDuration(TimeSpan.MaxValue);
        writer.WriteDuration(TimeSpan.MinValue);

        Assert.Equal(
            "00" + "02" + "01" + "a09c01" + "80a0a39c8c02" + "8080cea6a532" + "feffffffffffffffff01" +
            "ffffffffffffffffff01",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(TimeSpan.Zero, reader.ReadDuration());
        Assert.Equal(TimeSpan.FromTicks(1), reader.ReadDuration());
        Assert.Equal(TimeSpan.FromTicks(-1), reader.ReadDuration());
        Assert.Equal(TimeSpan.FromMilliseconds(1), reader.ReadDuration());
        Assert.Equal(TimeSpan.FromHours(1), reader.ReadDuration());
        Assert.Equal(TimeSpan.FromDays(1), reader.ReadDuration());
        Assert.Equal(TimeSpan.MaxValue, reader.ReadDuration());
        Assert.Equal(TimeSpan.MinValue, reader.ReadDuration());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("8000", 0L)]
    [InlineData("818000", -1L)]
    [InlineData("82808080808080808000", 1L)]
    public void A_longer_than_shortest_encoding_is_accepted(string hex, long ticks)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        Assert.Equal(ticks, reader.ReadDuration().Ticks);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("ffffffffffffffffff", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("ffffffffffffffffff02", "The variable-length integer at byte offset 0 does not fit in 64 bits.")]
    [InlineData("ffffffffffffffffff8101", "The variable-length integer at byte offset 0 is longer than 10 bytes.")]
    public void A_malformed_duration_is_rejected_as_for_a_long(string hex, string message)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadDuration();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("01ffff", "The input ends inside a variable-length integer that starts at byte offset 1.")]
    [InlineData("01ffffffffffffffffff02", "The variable-length integer at byte offset 1 does not fit in 64 bits.")]
    [InlineData("01ffffffffffffffffff8101", "The variable-length integer at byte offset 1 is longer than 10 bytes.")]
    public void A_rejected_duration_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);
        Assert.Equal(TimeSpan.FromTicks(-1), reader.ReadDuration());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadDuration();
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
    public void A_duration_has_the_bytes_of_a_long_with_its_ticks()
    {
        foreach (var ticks in Spread())
        {
            var durationWriter = new OahuWriter();
            durationWriter.WriteDuration(TimeSpan.FromTicks(ticks));
            var longWriter = new OahuWriter();
            longWriter.WriteLong(ticks);

            Assert.Equal(Hex.Of(longWriter.WrittenSpan), Hex.Of(durationWriter.WrittenSpan));
        }
    }

    [Fact]
    public void Durations_across_the_whole_range_round_trip_exactly()
    {
        var values = Spread();

        var writer = new OahuWriter();
        foreach (var ticks in values)
            writer.WriteDuration(TimeSpan.FromTicks(ticks));

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var ticks in values)
            Assert.Equal(ticks, reader.ReadDuration().Ticks);
        reader.EnsureEnd();
    }

    [Fact]
    public void Writing_and_reading_a_duration_allocate_nothing()
    {
        var value = TimeSpan.MinValue;
        var writer = new OahuWriter();

        Assert.Equal(0, Allocations.Smallest(() => WriteAndRead(writer, value)));

        static void WriteAndRead(OahuWriter writer, TimeSpan value)
        {
            writer.Reset();
            writer.WriteDuration(value);
            var reader = new OahuReader(writer.WrittenSpan);
            Assert.True(reader.ReadDuration() == value);
        }
    }

    [Fact]
    public void Durations_mixed_with_other_values_are_read_in_the_order_written()
    {
        var writer = new OahuWriter(1);
        writer.WriteDuration(TimeSpan.FromTicks(-7));
        writer.WriteInt(-65);
        writer.WriteDuration(TimeSpan.MaxValue);
        writer.WriteInstant(DateTimeOffset.UnixEpoch);
        writer.WriteDouble(1.5);
        writer.WriteDuration(TimeSpan.Zero);
        writer.WriteString("€");

        Assert.Equal(
            "0d" + "8101" + "feffffffffffffffff01" + "00" + "000000000000f83f" + "00" + "03e282ac",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(TimeSpan.FromTicks(-7), reader.ReadDuration());
        Assert.Equal(-65, reader.ReadInt());
        Assert.Equal(TimeSpan.MaxValue, reader.ReadDuration());
        Assert.Equal(DateTimeOffset.UnixEpoch, reader.ReadInstant());
        Assert.Equal(1.5, reader.ReadDouble());
        Assert.Equal(TimeSpan.Zero, reader.ReadDuration());
        Assert.Equal("€", reader.ReadString());
        reader.EnsureEnd();
    }

    private static List<long> Spread()
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

        return values;
    }
}
