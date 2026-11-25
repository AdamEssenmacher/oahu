namespace AdamE.Oahu.Tests.Conformance;

internal sealed record PageTableRow(string Section, IReadOnlyList<string> Header, IReadOnlyList<string> Cells)
{
    /// <summary>The key a vector's <c>doc</c> field gives for this row: the section's title and the row's first cell.</summary>
    public string Key => Section + ": " + Cells[0];
}

/// <summary>The parts of docs/wire-format.md that the vector file must agree with.</summary>
internal sealed class WireFormatPage
{
    private static readonly Lazy<WireFormatPage> LazyCurrent =
        new(() => Parse(File.ReadAllText(Repository.WireFormatPage)));

    private WireFormatPage(string text)
    {
        Text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = Text.Split('\n');

        var section = "";
        var sections = new List<string>();
        var malformed = new List<string>();
        var rows = new List<PageTableRow>();
        IReadOnlyList<string>? header = null;
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // A fenced code block's lines are code, not tables, sections or bullets. It ends at a line of at least as
            // many of its own fence characters.
            var trimmed = line.TrimStart(' ');
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.TrimEnd().All(c => c == fence[0]))
                    fence = null;
                continue;
            }

            if (line.Length - trimmed.Length < 4 && (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = new string(trimmed[0], trimmed.TakeWhile(c => c == trimmed[0]).Count());
                header = null;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..];
                sections.Add(section);
                header = null;
            }
            else if (line.StartsWith('|'))
            {
                var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
                if (header is null)
                {
                    header = cells;
                    i++; // The separator row.
                }
                else
                {
                    rows.Add(new PageTableRow(section, header, cells));
                }
            }
            else
            {
                header = null;
                if (section == "Malformed input" && line.StartsWith("- ", StringComparison.Ordinal))
                    malformed.Add(line[2..]);
            }
        }

        Sections = sections;
        MalformedInput = malformed;
        TableRows = rows;
        ByteTableRows = rows.Where(row => row.Header.Contains("Bytes")).ToList();
    }

    public static WireFormatPage Current => LazyCurrent.Value;

    public static WireFormatPage Parse(string text) => new(text);

    public string Text { get; }

    /// <summary>The titles of the page's <c>##</c> sections, in order.</summary>
    public IReadOnlyList<string> Sections { get; }

    /// <summary>The bullets of the "Malformed input" section, in order.</summary>
    public IReadOnlyList<string> MalformedInput { get; }

    /// <summary>Every row of every table, apart from header and separator rows.</summary>
    public IReadOnlyList<PageTableRow> TableRows { get; }

    /// <summary>Every row of every table with a "Bytes" column.</summary>
    public IReadOnlyList<PageTableRow> ByteTableRows { get; }
}
