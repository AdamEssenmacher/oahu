using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class OahuWriterTests
{
    [Fact]
    public void A_new_writer_is_empty()
    {
        var writer = new OahuWriter();

        Assert.Equal(0, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.IsEmpty);
    }

    [Fact]
    public void A_negative_initial_capacity_is_an_argument_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OahuWriter(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    public void The_buffer_grows_past_its_initial_capacity_and_keeps_what_was_written(int initialCapacity)
    {
        var writer = new OahuWriter(initialCapacity);
        var expected = new List<byte>();
        for (var i = 0; i < 100; i++)
        {
            // Alternates one-byte and ten-byte values, so some writes cross the end of the buffer.
            if (i % 2 == 0)
            {
                writer.WriteVarUInt((ulong)i);
                expected.Add((byte)i);
            }
            else
            {
                writer.WriteVarUInt(ulong.MaxValue);
                expected.AddRange(Hex.Bytes("ffffffffffffffffff01"));
            }
        }

        Assert.Equal(expected.Count, writer.WrittenCount);
        Assert.Equal(expected.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void A_write_that_exactly_fills_the_initial_capacity_is_kept()
    {
        var writer = new OahuWriter(2);
        writer.WriteVarUInt(300);
        writer.WriteVarUInt(1);

        Assert.Equal("ac0201", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void Reset_empties_the_writer()
    {
        var writer = new OahuWriter();
        writer.WriteVarUInt(300);

        writer.Reset();

        Assert.Equal(0, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.IsEmpty);
    }

    [Fact]
    public void A_reset_writer_writes_the_next_message_from_the_start()
    {
        var writer = new OahuWriter(1);
        writer.WriteVarUInt(ulong.MaxValue);
        writer.WriteVarUInt(ulong.MaxValue);

        writer.Reset();
        writer.WriteVarUInt(128);

        Assert.Equal("8001", Hex.Of(writer.WrittenSpan));
        Assert.Equal(2, writer.WrittenCount);
    }

    [Fact]
    public void A_writer_can_be_reset_and_reused_many_times()
    {
        var writer = new OahuWriter(4);
        for (var i = 0; i < 50; i++)
        {
            writer.Reset();
            writer.WriteCount(i);
            writer.WriteVarUInt((ulong)i << 40);

            var reader = new OahuReader(writer.WrittenSpan);
            Assert.Equal(i, reader.ReadCount());
            Assert.Equal((ulong)i << 40, reader.ReadVarUInt());
            reader.EnsureEnd();
        }
    }
}
