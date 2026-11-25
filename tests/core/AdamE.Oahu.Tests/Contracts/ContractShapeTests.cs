using Xunit;

namespace AdamE.Oahu.Tests.Contracts;

public sealed class ContractShapeTests
{
    [OahuContract]
    public interface IDownloadViewModel
    {
        IOahuState<string> FileName { get; }

        IOahuEvents<string> Messages { get; }
    }

    // Enough to show that the shapes compose. These are not sources: they never change and never deliver later.
    private sealed class FixedState<T>(T current) : IOahuState<T>
    {
        public T Current { get; } = current;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnNext(Current);
            return NoSubscription.Instance;
        }
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

    private sealed class DownloadViewModel : IDownloadViewModel
    {
        public IOahuState<string> FileName { get; } = new FixedState<string>("report.pdf");

        public IOahuEvents<string> Messages { get; } = new SilentEvents<string>();
    }

    [Fact]
    public void A_class_implements_a_contract_with_state_and_event_properties()
    {
        // A compile-time shape check and nothing more: it shows that a class can implement a contract whose
        // properties are stream interfaces. It tests no stream behavior; the sources' tests do that.
        IDownloadViewModel viewModel = new DownloadViewModel();

        Assert.NotNull(viewModel.FileName);
        Assert.NotNull(viewModel.Messages);
    }

    [Fact]
    public void A_state_is_covariant()
    {
        IOahuState<string> state = new FixedState<string>("x");

        IOahuState<object> widened = state;
        IObservable<object> observable = state;

        Assert.Equal("x", widened.Current);
        Assert.Same(state, observable);
    }

    [Fact]
    public void Events_are_covariant()
    {
        IOahuEvents<string> events = new SilentEvents<string>();

        IOahuEvents<object> widened = events;
        IObservable<object> observable = events;

        Assert.Same(events, widened);
        Assert.Same(events, observable);
    }
}
