using System.Globalization;

namespace SlackOfDescent;

public readonly struct GmValue : IEquatable<GmValue>
{
    public readonly double Number;
    public readonly string? Text;
    public bool IsString => Text is not null;
    public bool Truth => !IsString && Number >= 0.5;
    public int Int => checked((int)Math.Round(Number, MidpointRounding.ToEven));

    public GmValue(double value)
    {
        Number = value;
        Text = null;
    }

    public GmValue(string value)
    {
        Text = value;
        Number = 0;
    }

    public static implicit operator GmValue(double value) => new(value);

    public static implicit operator GmValue(int value) => new(value);

    public static implicit operator GmValue(bool value) => new(value ? 1 : 0);

    public static implicit operator GmValue(string value) => new(value);

    public static GmValue operator +(GmValue a, GmValue b) =>
        a.IsString || b.IsString ? new(a.ToString() + b.ToString()) : new(a.Number + b.Number);

    public static GmValue operator -(GmValue a, GmValue b) => new(a.Number - b.Number);

    public static GmValue operator *(GmValue a, GmValue b) => new(a.Number * b.Number);

    public static GmValue operator /(GmValue a, GmValue b) =>
        b.Number == 0 ? throw new DivideByZeroException() : new(a.Number / b.Number);

    public bool Equals(GmValue other) =>
        IsString == other.IsString && (IsString ? Text == other.Text : Number == other.Number);

    public bool AlmostEquals(GmValue other) =>
        IsString == other.IsString
        && (IsString ? Text == other.Text : Math.Abs(Number - other.Number) < 1e-13);

    public int GmlCompare(GmValue other) =>
        IsString && other.IsString ? string.CompareOrdinal(Text, other.Text)
        : Math.Abs(Number - other.Number) < 1e-13 ? 0
        : Number.CompareTo(other.Number);

    public override bool Equals(object? obj) => obj is GmValue value && Equals(value);

    public override int GetHashCode() => IsString ? Text!.GetHashCode() : Number.GetHashCode();

    public override string ToString() =>
        Text ?? Number.ToString("0.################", CultureInfo.InvariantCulture);

    public int CompareTo(GmValue other) =>
        IsString && other.IsString
            ? string.CompareOrdinal(Text, other.Text)
            : Number.CompareTo(other.Number);
}

internal readonly struct GmIndices
{
    private readonly IReadOnlyList<int>? indices;
    private readonly int first,
        second;
    public readonly int Count;
    public bool IsSpecified => indices is not null || Count > 0;

    public GmIndices(IReadOnlyList<int>? indices)
    {
        this.indices = indices;
        Count = indices?.Count ?? 0;
        first = second = 0;
    }

    public GmIndices(int first)
    {
        this.first = first;
        second = 0;
        Count = 1;
        indices = null;
    }

    public GmIndices(int first, int second)
    {
        this.first = first;
        this.second = second;
        Count = 2;
        indices = null;
    }

    public int this[int index] =>
        indices is not null
            ? indices[index]
            : index switch
            {
                0 when Count > 0 => first,
                1 when Count > 1 => second,
                _ => throw new IndexOutOfRangeException(),
            };

    public int FirstOr(int fallback) => IsSpecified ? this[0] : fallback;
}

public sealed class GmScope
{
    private readonly Dictionary<string, GmValue> values = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, int), int> lengths = new();

    internal Dictionary<string, int>? Layout;
    internal GmValue[]? SlotValues;
    internal bool[]? SlotDefined;
    internal Dictionary<string, int>? ArrayLayout;
    internal Dictionary<int, ArrayRow>[]? ArraySlots;
    private readonly Dictionary<string, Dictionary<int, ArrayRow>> arrays = new(
        StringComparer.Ordinal
    );
    private readonly HashSet<string> composite = new(StringComparer.Ordinal);
    public readonly ValueDictionary Values;
    internal Action<string>? Changed;

    public GmScope() => Values = new(this);

    internal void Reset()
    {
        values.Clear();
        lengths.Clear();
        arrays.Clear();
        composite.Clear();
        Changed = null;
        Layout = null;
        SlotValues = null;
        SlotDefined = null;
        ArrayLayout = null;
        ArraySlots = null;
    }

    internal void UseLayout(Dictionary<string, int>? next, Dictionary<string, int>? arrays = null)
    {
        if (!ReferenceEquals(Layout, next))
        {
            if (Layout is not null)
                foreach (var pair in Layout)
                    if (SlotDefined![pair.Value])
                        values[pair.Key] = SlotValues![pair.Value];
            Layout = next;
            if (next is null)
            {
                SlotValues = null;
                SlotDefined = null;
            }
            else
            {
                SlotValues = new GmValue[next.Count];
                SlotDefined = new bool[next.Count];
                foreach (var pair in next)
                    if (values.Remove(pair.Key, out var existing))
                    {
                        SlotValues[pair.Value] = existing;
                        SlotDefined[pair.Value] = true;
                    }
            }
        }
        if (ReferenceEquals(ArrayLayout, arrays))
            return;
        if (ArrayLayout is not null)
            foreach (var pair in ArrayLayout)
                if (ArraySlots![pair.Value] is { } container)
                    this.arrays[pair.Key] = container;
        ArrayLayout = arrays;
        ArraySlots = null;
        if (arrays is null || arrays.Count == 0)
            return;
        ArraySlots = new Dictionary<int, ArrayRow>[arrays.Count];
        foreach (var pair in arrays)
            if (this.arrays.Remove(pair.Key, out var container))
                ArraySlots[pair.Value] = container;
    }

    internal bool TrySlot(string name, out int slot)
    {
        if (Layout is not null && Layout.TryGetValue(name, out slot))
            return true;
        slot = 0;
        return false;
    }

    internal sealed class ArrayRow
    {
        public int Length,
            DefaultLength;
        public GmValue Default;
        public readonly Dictionary<int, GmValue> Entries = new();

        public bool TryGetValue(int column, out GmValue value)
        {
            if (Entries.TryGetValue(column, out value))
                return true;
            if (column < DefaultLength)
            {
                value = Default;
                return true;
            }
            return false;
        }

        public int Count => DefaultLength + Entries.Count(p => p.Key >= DefaultLength);

        public void Fill(int count, GmValue value)
        {
            if (count < DefaultLength)
                for (int column = count; column < DefaultLength; column++)
                    Entries.TryAdd(column, Default);
            foreach (int column in Entries.Keys.Where(c => c < count).ToArray())
                Entries.Remove(column);
            DefaultLength = count;
            Default = value;
            Length = Math.Max(Length, count);
        }

        public IEnumerable<KeyValuePair<int, GmValue>> Defined()
        {
            for (int column = 0; column < DefaultLength; column++)
                yield return new(column, Entries.GetValueOrDefault(column, Default));
            foreach (var entry in Entries)
                if (entry.Key >= DefaultLength)
                    yield return entry;
        }
    }

    private static (int Row, int Column) Position(GmIndices indices)
    {
        int row = indices is { Count: > 1 } ? indices[0] : 0,
            column = indices is { Count: > 1 } ? indices[1]
            : indices is { Count: 1 } ? indices[0]
            : 0;
        if (row < 0 || row >= 32000 || column < 0 || column >= 32000)
            throw new IndexOutOfRangeException("GML array index outside 0..31999");
        return (row, column);
    }

    private static string Key(string name, int row, int column) =>
        row == 0 && column == 0 ? name
        : row == 0 ? $"{name}[{column}]"
        : $"{name}[{row},{column}]";

    public static string Key(string name, IReadOnlyList<int>? indices) =>
        Key(name, new GmIndices(indices));

    internal static string Key(string name, GmIndices indices)
    {
        var (row, column) = Position(indices);
        return Key(name, row, column);
    }

    private static bool SplitKey(string key, out string name, out int row, out int column)
    {
        name = key;
        row = column = 0;
        if (key.Length == 0 || key[^1] != ']')
            return false;
        int bracket = key.LastIndexOf('[');
        if (bracket < 0)
            return false;
        var indices = key.AsSpan(bracket + 1, key.Length - bracket - 2);
        int comma = indices.IndexOf(',');
        if (comma < 0)
        {
            if (!int.TryParse(indices, out column) || column <= 0 || column >= 32000)
                return false;
        }
        else if (
            !int.TryParse(indices[..comma], out row)
            || !int.TryParse(indices[(comma + 1)..], out column)
            || row <= 0
            || row >= 32000
            || column < 0
            || column >= 32000
        )
            return false;
        string parsedName = key[..bracket];
        if (Key(parsedName, row, column) != key)
            return false;
        name = parsedName;
        return true;
    }

    private bool TryGetDefined(string key, out GmValue value)
    {
        if (Layout is not null && Layout.TryGetValue(key, out int slot) && SlotDefined![slot])
        {
            value = SlotValues![slot];
            return true;
        }
        if (values.TryGetValue(key, out value))
            return true;
        if (arrays.Count == 0 && ArrayLayout is null)
            return false;
        SplitKey(key, out string name, out int row, out int column);
        return TryArrayRows(name, out var rows)
            && rows!.TryGetValue(row, out var data)
            && data.TryGetValue(column, out value);
    }

    internal bool Contains(string name, GmIndices indices)
    {
        var (row, column) = Position(indices);
        if (row == 0 && column == 0 && TrySlot(name, out int slot) && SlotDefined![slot])
            return true;
        if (name.IndexOf('[') >= 0)
            return TryGetDefined(Key(name, row, column), out _);
        if (TryArrayRows(name, out var rows))
            return rows!.TryGetValue(row, out var data) && data.TryGetValue(column, out _);
        if (row == 0 && column == 0)
            return values.ContainsKey(name);
        if (!composite.Contains(name))
            return false;
        return values.ContainsKey(Key(name, row, column));
    }

    public GmValue Read(string name, IReadOnlyList<int>? indices = null) =>
        Read(name, new GmIndices(indices));

    public GmValue Read(string name, int index) => Read(name, new GmIndices(index));

    public GmValue Read(string name, int row, int column) => Read(name, new GmIndices(row, column));

    internal GmValue Read(string name, GmIndices indices)
    {
        var (row, column) = Position(indices);
        if (TryArrayRows(name, out var rows) && rows!.TryGetValue(row, out var data))
        {
            if (data.TryGetValue(column, out var entry))
                return entry;
            if (column < data.Length)
                return 0;
        }
        string key = Key(name, row, column);
        if (TryGetDefined(key, out var value))
            return value;
        if (lengths.TryGetValue((name, row), out var length) && column < length)
            return 0;
        throw new KeyNotFoundException("Uninitialized variable " + key);
    }

    public GmValue ReadOr(string name, GmValue fallback = default) =>
        TryGetDefined(name, out var value) ? value : fallback;

    internal GmValue ReadOr(string name, GmIndices indices, GmValue fallback = default)
    {
        var (row, column) = Position(indices);
        if (TryArrayRows(name, out var rows) && rows!.TryGetValue(row, out var data))
        {
            if (data.TryGetValue(column, out var entry))
                return entry;
            if (column < data.Length)
                return 0;
        }
        if (row == 0 && column == 0 && values.TryGetValue(name, out var plain))
            return plain;
        if (composite.Contains(name) && values.TryGetValue(Key(name, row, column), out var compositeValue))
            return compositeValue;
        return fallback;
    }

    public void Write(string name, GmValue value, IReadOnlyList<int>? indices = null) =>
        Write(name, value, new GmIndices(indices));

    public void Write(string name, GmValue value, int index) =>
        Write(name, value, new GmIndices(index));

    public void Write(string name, GmValue value, int row, int column) =>
        Write(name, value, new GmIndices(row, column));

    internal void Write(string name, GmValue value, GmIndices indices)
    {
        var (row, column) = Position(indices);
        string field = name;
        bool sparse = TryArrayRows(field, out var rows);
        if (
            !sparse
            && row == 0
            && column == 0
            && SplitKey(name, out var parsed, out int parsedRow, out int parsedColumn)
            && TryArrayRows(parsed, out rows)
        )
        {
            field = parsed;
            row = parsedRow;
            column = parsedColumn;
            sparse = true;
        }
        if (!sparse && name.IndexOf('[') < 0 && (row != 0 || column != 0 || IsArraySlotName(name)))
        {
            rows = ArrayRows(name);
            sparse = true;
        }
        if (sparse)
        {
            if (!rows!.TryGetValue(row, out var data))
                rows[row] = data = new();
            if (data.TryGetValue(column, out var previous) && previous.Equals(value))
                return;
            if (column < data.DefaultLength && data.Default.Equals(value))
                data.Entries.Remove(column);
            else
                data.Entries[column] = value;
            data.Length = Math.Max(data.Length, column + 1);
        }
        else if (row == 0 && column == 0 && TrySlot(name, out int slot))
        {
            if (SlotDefined![slot] && SlotValues![slot].Equals(value))
                return;
            SlotValues[slot] = value;
            SlotDefined[slot] = true;
        }
        else
        {
            string key = Key(name, row, column);
            if (values.TryGetValue(key, out var previous) && previous.Equals(value))
                return;
            int bracket = name.IndexOf('[');
            if (bracket > 0)
                composite.Add(name[..bracket]);
            values[key] = value;
            if (!lengths.TryGetValue((name, row), out var length) || length <= column)
                lengths[(name, row)] = column + 1;
        }
        Changed?.Invoke(name);
    }

    private bool TryArrayRows(string name, out Dictionary<int, ArrayRow>? rows)
    {
        if (ArrayLayout is not null && ArrayLayout.TryGetValue(name, out int slot))
        {
            rows = ArraySlots![slot];
            return rows is not null;
        }
        return arrays.TryGetValue(name, out rows);
    }

    private bool IsArraySlotName(string name) =>
        ArrayLayout is not null && ArrayLayout.ContainsKey(name);

    internal bool TryReadArraySlot(int slot, GmIndices indices, out GmValue value)
    {
        var (row, column) = Position(indices);
        value = default;
        if (ArraySlots![slot] is not { } rows || !rows.TryGetValue(row, out var data))
            return false;
        if (data.TryGetValue(column, out value))
            return true;
        if (column < data.Length)
        {
            value = 0;
            return true;
        }
        return false;
    }

    internal bool WriteArraySlot(int slot, string name, GmIndices indices, GmValue value)
    {
        var (row, column) = Position(indices);
        var rows = ArraySlots![slot] ?? ArrayRows(name);
        if (!rows.TryGetValue(row, out var data))
            rows[row] = data = new();
        if (data.TryGetValue(column, out var previous) && previous.Equals(value))
            return false;
        if (column < data.DefaultLength && data.Default.Equals(value))
            data.Entries.Remove(column);
        else
            data.Entries[column] = value;
        data.Length = Math.Max(data.Length, column + 1);
        return true;
    }

    internal int SlotArrayEntryCount()
    {
        if (ArrayLayout is null)
            return 0;
        int count = 0;
        foreach (var pair in ArrayLayout)
            if (ArraySlots![pair.Value] is { } rows)
                foreach (var row in rows.Values)
                    count += row.Count;
        return count;
    }

    internal IEnumerable<KeyValuePair<string, GmValue>> SlotArrayEntries()
    {
        if (ArrayLayout is null)
            yield break;
        foreach (var pair in ArrayLayout)
            if (ArraySlots![pair.Value] is { } rows)
                foreach (var row in rows)
                foreach (var entry in row.Value.Defined())
                    yield return new(Key(pair.Key, row.Key, entry.Key), entry.Value);
    }

    private Dictionary<int, ArrayRow> ArrayRows(string name)
    {
        Dictionary<int, ArrayRow> rows;
        if (ArrayLayout is not null && ArrayLayout.TryGetValue(name, out int slot))
        {
            if (ArraySlots![slot] is { } existing)
                return existing;
            ArraySlots[slot] = rows = new();
        }
        else
        {
            if (arrays.TryGetValue(name, out var found))
                return found;
            arrays[name] = rows = new();
        }
        string prefix = name + "[";
        foreach (
            var entry in values
                .Where(p => p.Key == name || p.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray()
        )
        {
            SplitKey(entry.Key, out string field, out int row, out int column);
            if (field != name)
                continue;
            if (!rows.TryGetValue(row, out var data))
                rows[row] = data = new() { Length = lengths.GetValueOrDefault((name, row)) };
            data.Entries[column] = entry.Value;
            values.Remove(entry.Key);
        }
        return rows;
    }

    internal void FillArrayPrefix(string name, int row, int count, GmValue value)
    {
        if (count <= 0)
            return;
        Position(new GmIndices(row, count - 1));
        var rows = ArrayRows(name);
        if (!rows.TryGetValue(row, out var data))
            rows[row] = data = new() { Length = lengths.GetValueOrDefault((name, row)) };
        data.Fill(count, value);
        Changed?.Invoke(name);
    }

    internal void CopyArrayPrefix(string source, string target, int targetRow, int count)
    {
        if (count <= 0)
            return;
        Position(new GmIndices(targetRow, count - 1));
        if (
            arrays.TryGetValue(source, out var sourceRows)
            && sourceRows.TryGetValue(0, out var sourceData)
            && sourceData.DefaultLength >= count
        )
        {
            var overrides = sourceData.Entries.Where(p => p.Key < count).ToArray();
            FillArrayPrefix(target, targetRow, count, sourceData.Default);
            foreach (var entry in overrides)
                Write(target, entry.Value, targetRow, entry.Key);
            return;
        }
        for (int column = 0; column < count; column++)
            Write(target, Read(source, column), targetRow, column);
    }

    public sealed class ValueDictionary : IReadOnlyDictionary<string, GmValue>
    {
        private readonly GmScope scope;

        internal ValueDictionary(GmScope scope) => this.scope = scope;

        public GmValue this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public int Count =>
            scope.values.Count
            + scope.arrays.Values.Sum(rows => rows.Values.Sum(row => row.Count))
            + scope.SlotArrayEntryCount()
            + (scope.SlotDefined?.Count(defined => defined) ?? 0);
        public IEnumerable<string> Keys => this.Select(p => p.Key);
        public IEnumerable<GmValue> Values => this.Select(p => p.Value);

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out GmValue value) =>
            scope.TryGetDefined(key, out value);

        public IEnumerator<KeyValuePair<string, GmValue>> GetEnumerator()
        {
            if (scope.Layout is not null)
                foreach (var pair in scope.Layout)
                    if (scope.SlotDefined![pair.Value])
                        yield return new(pair.Key, scope.SlotValues![pair.Value]);
            foreach (var entry in scope.values)
                yield return entry;
            foreach (var field in scope.arrays)
            foreach (var row in field.Value)
            foreach (var entry in row.Value.Defined())
                yield return new(Key(field.Key, row.Key, entry.Key), entry.Value);
            foreach (var entry in scope.SlotArrayEntries())
                yield return entry;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}