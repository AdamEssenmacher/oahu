using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class OahuReaderTests
{
    [Fact]
    public void Remaining_counts_the_bytes_not_read_yet()
    {
        var reader = new OahuReader(Hex.Bytes("ac0201ff7f"));
        Assert.Equal(5, reader.Remaining);

        reader.ReadVarUInt();
        Assert.Equal(3, reader.Remaining);

        reader.ReadVarUInt();
        Assert.Equal(2, reader.Remaining);

        reader.ReadVarUInt();
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void The_end_check_passes_on_empty_input()
    {
        var reader = new OahuReader([]);

        reader.EnsureEnd();

        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void The_end_check_passes_once_every_byte_is_read()
    {
        var reader = new OahuReader(Hex.Bytes("8001"));
        reader.ReadVarUInt();

        reader.EnsureEnd();
    }

    [Fact]
    public void The_end_check_rejects_bytes_left_over()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("010203"));
            reader.ReadVarUInt();
            reader.EnsureEnd();
        });

        Assert.Equal(1, exception.Offset);
        Assert.Equal(
            "2 unread byte(s) remain at the end of the input, from byte offset 1.", exception.Message);
    }

    [Fact]
    public void The_end_check_rejects_input_that_was_not_read_at_all()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("00"));
            reader.EnsureEnd();
        });

        Assert.Equal(0, exception.Offset);
    }

    [Fact]
    public void Values_of_mixed_kinds_are_read_in_the_order_written()
    {
        var writer = new OahuWriter(1);
        writer.WriteBool(true);
        writer.WriteInt(-65);
        writer.WriteDouble(-0.0);
        writer.WriteLong(long.MinValue);
        writer.WriteCount(3);
        writer.WriteBool(false);
        writer.WriteDouble(1.5);
        writer.WriteInt(int.MaxValue);
        writer.WriteVarUInt(300);
        writer.WriteLong(1);

        Assert.Equal(
            "01" + "8101" + "0000000000000080" + "ffffffffffffffffff01" + "03" + "00" + "000000000000f83f" +
            "feffffff0f" + "ac02" + "02",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.True(reader.ReadBool());
        Assert.Equal(-65, reader.ReadInt());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        Assert.Equal(long.MinValue, reader.ReadLong());
        Assert.Equal(3, reader.ReadCount());
        Assert.False(reader.ReadBool());
        Assert.Equal(1.5, reader.ReadDouble());
        Assert.Equal(int.MaxValue, reader.ReadInt());
        Assert.Equal(300UL, reader.ReadVarUInt());
        Assert.Equal(1L, reader.ReadLong());
        reader.EnsureEnd();
    }

    [Fact]
    public void A_format_error_is_a_FormatException()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader([]);
            reader.ReadVarUInt();
        });

        Assert.IsAssignableFrom<FormatException>(exception);
    }
}
