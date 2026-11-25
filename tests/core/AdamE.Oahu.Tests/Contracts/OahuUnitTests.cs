using System.Runtime.CompilerServices;
using Xunit;

namespace AdamE.Oahu.Tests.Contracts;

public sealed class OahuUnitTests
{
    [OahuContract]
    public interface IEditorViewModel
    {
        IOahuEvents<OahuUnit> Saved { get; }
    }

    private sealed class SilentEvents<T> : IOahuEvents<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => NoSubscription.Instance;
    }

    private sealed class NoSubscription : IDisposable
    {
        public static readonly NoSubscription Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class EditorViewModel : IEditorViewModel
    {
        public IOahuEvents<OahuUnit> Saved { get; } = new SilentEvents<OahuUnit>();
    }

    [Fact]
    public void Every_way_of_getting_the_value_gives_one_value()
    {
        OahuUnit[] values = [OahuUnit.Value, default, new OahuUnit()];

        foreach (var left in values)
        {
            foreach (var right in values)
            {
                Assert.True(left == right);
                Assert.False(left != right);
                Assert.True(left.Equals(right));
                Assert.True(left.Equals((object)right));
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        }
    }

    [Fact]
    public void It_is_not_equal_to_another_type()
    {
        Assert.False(OahuUnit.Value.Equals(null));
        Assert.False(OahuUnit.Value.Equals((object)0));
    }

    [Fact]
    public void Its_text_is_an_empty_pair_of_parentheses()
    {
        Assert.Equal("()", OahuUnit.Value.ToString());
        Assert.Equal("()", default(OahuUnit).ToString());
    }

    [Fact]
    public void It_is_a_struct_with_no_fields()
    {
        Assert.True(typeof(OahuUnit).IsValueType);
        Assert.Empty(typeof(OahuUnit).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));

        // An empty struct still takes one byte in memory; nothing of it is written to the wire.
        Assert.Equal(1, Unsafe.SizeOf<OahuUnit>());
    }

    [Fact]
    public void A_contract_declares_events_of_it()
    {
        // A compile-time shape check: a contract can declare events that carry no value, and a class implement it.
        IEditorViewModel viewModel = new EditorViewModel();

        Assert.NotNull(viewModel.Saved);
    }
}
