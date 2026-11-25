using System.Globalization;
using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class InstantTests
{
    private const long MinMilliseconds = -62_135_596_800_000;
    private const long MaxMilliseconds = 253_402_300_799_999;

    [Theory]
    [InlineData("1970-01-01T00:00:00Z", 0L, "00")]
    [InlineData("1970-01-01T00:00:00.001Z", 1L, "02")]
    [InlineData("1969-12-31T23:59:59.999Z", -1L, "01")]
    [InlineData("2025-11-02T15:00:00.123Z", 1_762_095_600_123L, "f6c7a8d1c866")]
    [InlineData("0001-01-01T00:00:00Z", MinMilliseconds, "ffdfe6a2e2a01c")]
    [InlineData("9999-12-31T23:59:59.999Z", MaxMilliseconds, "feeffea1fa9d73")]
    public void An_instant_is_written_as_milliseconds_since_the_Unix_epoch_and_read_back_in_UTC(
        string text, long milliseconds, string hex)
    {
        var value = Parse(text);
        Assert.Equal(milliseconds, value.ToUnixTimeMilliseconds());

        var writer = new OahuWriter();
        writer.WriteInstant(value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes(hex));
        var read = reader.ReadInstant();
        reader.EnsureEnd();

        Assert.Equal(value.UtcTicks, read.UtcTicks);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Theory]
    [InlineData("1970-01-01T00:00:00.0009999Z", "1970-01-01T00:00:00Z", "00")]
    [InlineData("1970-01-01T00:00:00.0000001Z", "1970-01-01T00:00:00Z", "00")]
    [InlineData("1969-12-31T23:59:59.9995Z", "1969-12-31T23:59:59.999Z", "01")]
    [InlineData("1969-12-31T23:59:59.9999999Z", "1969-12-31T23:59:59.999Z", "01")]
    [InlineData("1969-12-31T23:59:59.9989999Z", "1969-12-31T23:59:59.998Z", "03")]
    [InlineData("2025-11-02T15:00:00.1239999Z", "2025-11-02T15:00:00.123Z", "f6c7a8d1c866")]
    [InlineData("0001-01-01T00:00:00.0009999Z", "0001-01-01T00:00:00Z", "ffdfe6a2e2a01c")]
    [InlineData("9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.999Z", "feeffea1fa9d73")]
    public void A_fraction_of_a_millisecond_is_rounded_down_towards_the_earlier_time(
        string text, string expected, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteInstant(Parse(text));

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadInstant();
        reader.EnsureEnd();

        Assert.Equal(Parse(expected).UtcTicks, read.UtcTicks);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Fact]
    public void The_largest_DateTimeOffset_is_written_as_the_largest_instant()
    {
        var writer = new OahuWriter();
        writer.WriteInstant(DateTimeOffset.MaxValue);
        writer.WriteInstant(DateTimeOffset.MinValue);

        Assert.Equal("feeffea1fa9d73" + "ffdfe6a2e2a01c", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void The_offset_is_not_carried_and_the_instant_is_read_back_in_UTC()
    {
        var utc = Parse("2025-11-02T15:00:00.123+00:00");
        var east = Parse("2025-11-03T05:00:00.123+14:00");
        var west = Parse("2025-11-02T03:00:00.123-12:00");
        Assert.Equal(utc.UtcTicks, east.UtcTicks);
        Assert.Equal(utc.UtcTicks, west.UtcTicks);

        foreach (var value in new[] { utc, east, west })
        {
            var writer = new OahuWriter();
            writer.WriteInstant(value);

            Assert.Equal("f6c7a8d1c866", Hex.Of(writer.WrittenSpan));

            var reader = new OahuReader(writer.WrittenSpan);
            var read = reader.ReadInstant();
            reader.EnsureEnd();

            Assert.Equal(utc.UtcTicks, read.UtcTicks);
            Assert.Equal(TimeSpan.Zero, read.Offset);
        }
    }

    [Theory]
    [InlineData("8000", 0L)]
    [InlineData("828000", 1L)]
    [InlineData("81808080808080808000", -1L)]
    [InlineData("feeffea1fa9df300", MaxMilliseconds)]
    public void A_longer_than_shortest_encoding_is_accepted(string hex, long milliseconds)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        var read = reader.ReadInstant();
        reader.EnsureEnd();

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcTicks, read.UtcTicks);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Fact]
    public void A_longer_than_shortest_encoding_outside_the_range_is_rejected_as_its_shortest_form()
    {
        var input = Hex.Bytes("80f0fea1fa9df300");
        var reader = new OahuReader(input);

        OahuFormatException? exception = null;
        try
        {
            reader.ReadInstant();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(OutOfRangeMessage(0, MaxMilliseconds + 1), exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Theory]
    [InlineData("81e0e6a2e2a01c", MinMilliseconds - 1)]
    [InlineData("80f0fea1fa9d73", MaxMilliseconds + 1)]
    [InlineData("ffffffffffffffffff01", long.MinValue)]
    [InlineData("feffffffffffffffff01", long.MaxValue)]
    public void An_instant_outside_the_range_of_DateTimeOffset_is_rejected(string hex, long milliseconds)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        OahuFormatException? exception = null;
        try
        {
            reader.ReadInstant();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(OutOfRangeMessage(0, milliseconds), exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Theory]
    [InlineData("0081e0e6a2e2a01c", MinMilliseconds - 1)]
    [InlineData("0080f0fea1fa9d73", MaxMilliseconds + 1)]
    [InlineData("00ffffffffffffffffff01", long.MinValue)]
    [InlineData("00feffffffffffffffff01", long.MaxValue)]
    public void A_rejected_instant_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, long milliseconds)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);
        Assert.Equal(DateTimeOffset.UnixEpoch, reader.ReadInstant());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadInstant();
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(OutOfRangeMessage(1, milliseconds), exception.Message);
        Assert.Equal(input.Length - 1, reader.Remaining);
    }

    [Theory]
    [InlineData("", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("ffdfe6a2e2a0", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("ffffffffffffffffff02", "The variable-length integer at byte offset 0 does not fit in 64 bits.")]
    [InlineData("ffffffffffffffffff8101", "The variable-length integer at byte offset 0 is longer than 10 bytes.")]
    public void A_malformed_integer_is_rejected_as_for_a_long(string hex, string message)
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadInstant();
        });

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void An_instant_has_the_bytes_of_a_long_with_its_milliseconds()
    {
        foreach (var value in Spread())
        {
            var instantWriter = new OahuWriter();
            instantWriter.WriteInstant(value);
            var longWriter = new OahuWriter();
            longWriter.WriteLong(value.ToUnixTimeMilliseconds());

            Assert.Equal(Hex.Of(longWriter.WrittenSpan), Hex.Of(instantWriter.WrittenSpan));
        }
    }

    [Fact]
    public void Instants_across_the_whole_range_round_trip_to_the_millisecond()
    {
        var values = Spread();

        var writer = new OahuWriter();
        foreach (var value in values)
            writer.WriteInstant(value);

        var reader = new OahuReader(writer.WrittenSpan);
        foreach (var value in values)
        {
            var read = reader.ReadInstant();
            Assert.Equal(value.ToUnixTimeMilliseconds(), read.ToUnixTimeMilliseconds());
            Assert.Equal(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), read.UtcTicks);
            Assert.Equal(TimeSpan.Zero, read.Offset);
        }

        reader.EnsureEnd();
    }

    [Fact]
    public void Writing_and_reading_an_instant_allocate_nothing()
    {
        var value = Parse("2025-11-02T15:00:00.123+14:00");
        var writer = new OahuWriter();

        Assert.Equal(0, Allocations.Smallest(() => WriteAndRead(writer, value)));

        static void WriteAndRead(OahuWriter writer, DateTimeOffset value)
        {
            writer.Reset();
            writer.WriteInstant(value);
            var reader = new OahuReader(writer.WrittenSpan);
            Assert.True(reader.ReadInstant() == value);
        }
    }

    [Fact]
    public void Instants_mixed_with_other_values_are_read_in_the_order_written()
    {
        var writer = new OahuWriter(1);
        writer.WriteInstant(DateTimeOffset.UnixEpoch);
        writer.WriteBool(true);
        writer.WriteInstant(Parse("1969-12-31T23:59:59.999Z"));
        writer.WriteString("Milk");
        writer.WriteInstant(Parse("2025-11-02T15:00:00.123Z"));
        writer.WriteDuration(TimeSpan.FromMilliseconds(1));
        writer.WriteLong(1);

        Assert.Equal(
            "00" + "01" + "01" + "044d696c6b" + "f6c7a8d1c866" + "a09c01" + "02", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(DateTimeOffset.UnixEpoch, reader.ReadInstant());
        Assert.True(reader.ReadBool());
        Assert.Equal(Parse("1969-12-31T23:59:59.999Z"), reader.ReadInstant());
        Assert.Equal("Milk", reader.ReadString());
        Assert.Equal(Parse("2025-11-02T15:00:00.123Z"), reader.ReadInstant());
        Assert.Equal(TimeSpan.FromMilliseconds(1), reader.ReadDuration());
        Assert.Equal(1L, reader.ReadLong());
        reader.EnsureEnd();
    }

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);

    // Instants with and without fractions of a millisecond, at several offsets, from the smallest to the largest.
    private static List<DateTimeOffset> Spread()
    {
        var values = new List<DateTimeOffset>
        {
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.UnixEpoch,
        };

        var random = new Random(20_261_125);
        TimeSpan[] offsets = [TimeSpan.Zero, TimeSpan.FromHours(14), TimeSpan.FromHours(-12), new(5, 45, 0)];
        for (var i = 0; i < 1_000; i++)
        {
            var utcTicks = random.NextInt64(DateTimeOffset.MinValue.UtcTicks, DateTimeOffset.MaxValue.UtcTicks + 1);
            if (i % 2 == 0)
                utcTicks -= utcTicks % TimeSpan.TicksPerMillisecond;

            // The local time must stay within the range too, so an offset is used only where it fits.
            var offset = offsets[i % offsets.Length];
            var localTicks = utcTicks + offset.Ticks;
            if (localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks)
                offset = TimeSpan.Zero;

            values.Add(new DateTimeOffset(utcTicks + offset.Ticks, offset));
        }

        return values;
    }

    private static string OutOfRangeMessage(int start, long milliseconds) =>
        $"The instant at byte offset {start} is {milliseconds.ToString(CultureInfo.InvariantCulture)} milliseconds " +
        "from the Unix epoch, outside the range from -62135596800000 (0001-01-01T00:00:00Z) to 253402300799999 " +
        "(9999-12-31T23:59:59.999Z).";
}
