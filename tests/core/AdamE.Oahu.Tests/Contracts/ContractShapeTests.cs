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

    // Not a source either: it refuses every edit, so its value never changes and it never delivers later.
    private sealed class RefusingEditable<T>(T current) : IOahuEditable<T>
    {
        public T Current { get; } = current;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnNext(Current);
            return NoSubscription.Instance;
        }

        public void Edit(T value)
        {
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
    public void An_editable_is_a_state_and_widens_as_one()
    {
        // IOahuEditable<T> is invariant, but it derives from the covariant IOahuState<T>, so it converts to a state
        // of its own type or of a wider one. It does not convert to an IOahuEditable<object>.
        IOahuEditable<string> editable = new RefusingEditable<string>("x");

        IOahuState<string> state = editable;
        IOahuState<object> widened = editable;
        IObservable<object> observable = editable;

        Assert.Equal("x", state.Current);
        Assert.Equal("x", widened.Current);
        Assert.Same(editable, observable);
        Assert.False(typeof(IOahuEditable<object>).IsAssignableFrom(typeof(IOahuEditable<string>)));
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
