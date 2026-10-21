using System.Globalization;

namespace SlackOfDescent;

public readonly struct GmValue : IEquatable<GmValue>
{
    public readonly double Number;
    public readonly string? Text;
    public bool IsString => Text is not null;
    public bool Truth => !IsString && Number >= 0.5;
    public int Int => checked((int)Math.Round(Number, MidpointRounding.ToEven));
    public GmValue(double value) { Number = value; Text = null; }
    public GmValue(string value) { Text = value; Number = 0; }
    public static implicit operator GmValue(double value) => new(value);
    public static implicit operator GmValue(int value) => new(value);
    public static implicit operator GmValue(bool value) => new(value ? 1 : 0);
    public static implicit operator GmValue(string value) => new(value);
    public static GmValue operator +(GmValue a, GmValue b) => a.IsString && b.IsString ? new(a.Text + b.Text) : !a.IsString && !b.IsString ? new(a.Number + b.Number) : throw new InvalidOperationException("Mixed string and numeric addition");
    public static GmValue operator -(GmValue a, GmValue b) => new(a.Number - b.Number);
    public static GmValue operator *(GmValue a, GmValue b) => a.IsString ? new(string.Concat(Enumerable.Repeat(a.Text, Math.Max(0, b.Int)))) : new(a.Number * b.Number);
    public static GmValue operator /(GmValue a, GmValue b) => b.Number == 0 ? throw new DivideByZeroException() : new(a.Number / b.Number);
    public bool Equals(GmValue other) => IsString == other.IsString && (IsString ? Text == other.Text : Number == other.Number);
    public override bool Equals(object? obj) => obj is GmValue value && Equals(value);
    public override int GetHashCode() => IsString ? Text!.GetHashCode() : Number.GetHashCode();
    public override string ToString() => Text ?? Number.ToString("0.################", CultureInfo.InvariantCulture);
    public int CompareTo(GmValue other) => IsString && other.IsString ? string.CompareOrdinal(Text, other.Text) : Number.CompareTo(other.Number);
}

public sealed class GmScope
{
    public readonly Dictionary<string, GmValue> Values = new(StringComparer.Ordinal);
    internal Action<string>? Changed;
    private readonly Dictionary<(string,int),int> lengths=new();
    private static (int Row,int Column) Position(IReadOnlyList<int>? indices)
    {
        int row=indices is {Count:>1}?indices[0]:0,column=indices is {Count:>1}?indices[1]:indices is {Count:1}?indices[0]:0;
        if(row<0||row>=32000||column<0||column>=32000)throw new IndexOutOfRangeException("GML array index outside 0..31999");
        return (row,column);
    }
    public static string Key(string name,IReadOnlyList<int>? indices)
    {
        var (row,column)=Position(indices);
        return row==0&&column==0?name:row==0?$"{name}[{column}]":$"{name}[{row},{column}]";
    }
    public GmValue Read(string name,IReadOnlyList<int>? indices=null)
    {
        if(Values.TryGetValue(Key(name,indices),out var value))return value;
        var (row,column)=Position(indices);
        if(lengths.TryGetValue((name,row),out var length)&&column<length)return 0;
        throw new KeyNotFoundException("Uninitialized variable "+Key(name,indices));
    }
    public GmValue ReadOr(string name, GmValue fallback = default) => Values.GetValueOrDefault(name, fallback);
    public void Write(string name,GmValue value,IReadOnlyList<int>? indices=null)
    {
        string key=Key(name,indices);
        if(Values.TryGetValue(key,out var previous)&&previous.Equals(value))return;
        Values[key]=value;
        var (row,column)=Position(indices);
        if(!lengths.TryGetValue((name,row),out var length)||length<=column)lengths[(name,row)]=column+1;
        Changed?.Invoke(name);
    }
}
