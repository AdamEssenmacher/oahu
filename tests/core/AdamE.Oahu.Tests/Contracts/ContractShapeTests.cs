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

    [OahuContract]
    public interface ITasksViewModel
    {
        IOahuKeyedList<TaskRow> Tasks { get; }
    }

    // Each kind of method a contract may declare: void or Task, no value parameter or one, and for a Task method a
    // trailing CancellationToken.
    [OahuContract]
    public interface IDownloadControlsViewModel
    {
        IOahuState<string> Status { get; }

        void Cancel();

        void SaveTo(Destination destination);

        void Choose(int? index);

        Task Refresh();

        Task Rename(string name);

        Task Retry(CancellationToken cancellation);

        Task Search(string text, CancellationToken cancellation);
    }

    public sealed record Destination(string Folder, bool Overwrite);

    public sealed record TaskRow(string Key, string Text, bool Done) : IOahuKeyed;

    // A union row: every case has the positional Key, passed to the union's own.
    public abstract record BoardRow(string Key) : IOahuKeyed
    {
        public sealed record Header(string Key, string Title) : BoardRow(Key);

        public sealed record Card(string Key, string Text) : BoardRow(Key);
    }

    public readonly record struct TagRow(string Key, string Name) : IOahuKeyed;

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

    // Not a source either: its rows never change, so it only ever sends the replay.
    private sealed class FixedKeyedList<T>(IReadOnlyList<T> items) : IOahuKeyedList<T>
        where T : IOahuKeyed
    {
        public IReadOnlyList<T> Items { get; } = items;

        public IDisposable Subscribe(IObserver<IReadOnlyList<ListOp<T>>> observer)
        {
            List<ListOp<T>> replay = [new ListOp<T>.Clear()];
            for (var index = 0; index < Items.Count; index++)
                replay.Add(new ListOp<T>.Insert(index, Items[index]));

            observer.OnNext(replay);
            return NoSubscription.Instance;
        }
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

    private sealed class TasksViewModel : ITasksViewModel
    {
        public IOahuKeyedList<TaskRow> Tasks { get; } =
            new FixedKeyedList<TaskRow>([new TaskRow("t0", "Milk", false)]);
    }

    // Implements every method with no behavior: nothing calls a contract's methods yet.
    private sealed class DownloadControlsViewModel : IDownloadControlsViewModel
    {
        public IOahuState<string> Status { get; } = new FixedState<string>("waiting");

        public void Cancel()
        {
        }

        public void SaveTo(Destination destination)
        {
        }

        public void Choose(int? index)
        {
        }

        public Task Refresh() => Task.CompletedTask;

        public Task Rename(string name) => Task.CompletedTask;

        public Task Retry(CancellationToken cancellation) => Task.CompletedTask;

        public Task Search(string text, CancellationToken cancellation) => Task.CompletedTask;
    }

    [Fact]
    public void A_class_implements_a_contract_with_each_kind_of_method()
    {
        // A compile-time shape check and nothing more: a contract can declare each kind of method the contract rules
        // allow, and a class implement it. It calls no method and tests no behavior; nothing dispatches a method yet.
        IDownloadControlsViewModel viewModel = new DownloadControlsViewModel();

        Assert.Equal("waiting", viewModel.Status.Current);
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

    [Fact]
    public void A_class_implements_a_contract_with_a_keyed_list()
    {
        // A compile-time shape check and nothing more: a contract can declare a keyed list of a record row, and a class
        // implement it. It tests no stream behavior; the sources' tests do that.
        ITasksViewModel viewModel = new TasksViewModel();
        IOahuKeyed row = viewModel.Tasks.Items[0];

        Assert.Equal("t0", row.Key);
    }

    [Fact]
    public void A_union_and_a_record_struct_can_be_rows()
    {
        // A compile-time shape check: the positional Key parameter implements IOahuKeyed.Key, on every case of a union
        // and on a record struct.
        IOahuKeyedList<BoardRow> board = new FixedKeyedList<BoardRow>(
            [new BoardRow.Header("h", "Today"), new BoardRow.Card("c1", "Milk")]);
        IOahuKeyedList<TagRow> tags = new FixedKeyedList<TagRow>([new TagRow("k", "Home")]);

        Assert.Equal(["h", "c1"], board.Items.Select(row => row.Key));
        Assert.Equal("k", tags.Items[0].Key);
    }

    [Fact]
    public void A_keyed_list_is_invariant_and_an_observable_of_its_batches()
    {
        // ListOp<T> is a record, and a class cannot vary in its type argument, so IOahuKeyedList<T> cannot be
        // covariant. It is still an observable of its batches, which widens to an observable of object.
        var parameter = typeof(IOahuKeyedList<>).GetGenericArguments()[0];
        Assert.Equal(
            System.Reflection.GenericParameterAttributes.None,
            parameter.GenericParameterAttributes & System.Reflection.GenericParameterAttributes.VarianceMask);
        Assert.Equal([typeof(IOahuKeyed)], parameter.GetGenericParameterConstraints());

        IOahuKeyedList<BoardRow> board = new FixedKeyedList<BoardRow>([]);
        IObservable<IReadOnlyList<ListOp<BoardRow>>> batches = board;
        IObservable<object> observable = board;

        Assert.Same(board, batches);
        Assert.Same(board, observable);

        // A case on its own is never a contract's row type; it stands here only for a type that derives from the row.
        Assert.False(typeof(IOahuKeyedList<BoardRow>).IsAssignableFrom(typeof(IOahuKeyedList<BoardRow.Card>)));
    }
}
