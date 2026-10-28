using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public sealed class NativeMotionPart
{
    public double[] PosDir { get; set; } = [];
    public double[] PosDis { get; set; } = [];
    public double[] Angle { get; set; } = [];
    public double[] Picture { get; set; } = [];
    public double[] ScaleX { get; set; } = [];
    public double[] ScaleY { get; set; } = [];
    public static NativeMotionPart Create(int frames) => new()
    {
        PosDir = Fill(frames, 0), PosDis = Fill(frames, 0), Angle = Fill(frames, 0), Picture = Fill(frames, 0), ScaleX = Fill(frames, 1), ScaleY = Fill(frames, 1)
    };
    public void Resize(int frames)
    {
        PosDir = Fit(PosDir, frames, 0); PosDis = Fit(PosDis, frames, 0); Angle = Fit(Angle, frames, 0);
        Picture = Fit(Picture, frames, 0); ScaleX = Fit(ScaleX, frames, 1); ScaleY = Fit(ScaleY, frames, 1);
    }
    public NativeMotionPart Clone() => new()
    {
        PosDir = (double[])PosDir.Clone(), PosDis = (double[])PosDis.Clone(), Angle = (double[])Angle.Clone(),
        Picture = (double[])Picture.Clone(), ScaleX = (double[])ScaleX.Clone(), ScaleY = (double[])ScaleY.Clone()
    };
    public bool Written(int frame) => PosDir[frame] != 0 || PosDis[frame] != 0 || Angle[frame] != 0 || Picture[frame] != 0 || ScaleX[frame] != 1 || ScaleY[frame] != 1;
    internal static double[] Fill(int count, double value) { var values = new double[Math.Max(0, count)]; Array.Fill(values, value); return values; }
    private static double[] Fit(double[] source, int frames, double fallback)
    {
        if (source.Length == frames) return (double[])source.Clone();
        var values = Fill(frames, fallback);
        Array.Copy(source, values, Math.Min(source.Length, frames));
        return values;
    }
}

public sealed class NativeMotionDocument
{
    public const int DefaultFrames = 50, DefaultParts = 15, DefaultMotions = 150;
    public int FormatVersion { get; set; } = 1;
    public string MotionName { get; set; } = "";
    public double? Length { get; set; }
    public int Frames { get; set; }
    public int Parts { get; set; }
    public double?[] Step { get; set; } = [];
    public double[] PosX { get; set; } = [];
    public double[] PosY { get; set; } = [];
    public NativeMotionPart[] Channels { get; set; } = [];

    public static NativeMotionDocument Create(string name, int frames = DefaultFrames, int parts = DefaultParts, double? length = null)
    {
        var document = new NativeMotionDocument { MotionName = name };
        document.Resize(frames, parts);
        document.Length = length;
        return document;
    }

    public void Resize(int frames, int parts)
    {
        frames = Math.Max(0, frames); parts = Math.Max(0, parts);
        var step = new double?[frames];
        Array.Copy(Step, step, Math.Min(Step.Length, frames));
        var posX = NativeMotionPart.Fill(frames, 0); Array.Copy(PosX, posX, Math.Min(PosX.Length, frames));
        var posY = NativeMotionPart.Fill(frames, 0); Array.Copy(PosY, posY, Math.Min(PosY.Length, frames));
        Step = step; PosX = posX; PosY = posY;
        var channels = new NativeMotionPart[parts];
        for (int part = 0; part < parts; part++)
        {
            if (part < Channels.Length) { Channels[part].Resize(frames); channels[part] = Channels[part]; }
            else channels[part] = NativeMotionPart.Create(frames);
        }
        Channels = channels; Frames = frames; Parts = parts;
    }

    public NativeMotionDocument Clone()
    {
        var clone = new NativeMotionDocument
        {
            FormatVersion = FormatVersion, MotionName = MotionName, Length = Length, Frames = Frames, Parts = Parts,
            Step = (double?[])Step.Clone(), PosX = (double[])PosX.Clone(), PosY = (double[])PosY.Clone(),
            Channels = Channels.Select(channel => channel.Clone()).ToArray()
        };
        return clone;
    }

    public static string Path(string userRoot, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidDataException("动作数据需要有效名称。");
        return System.IO.Path.Combine(userRoot, "Chara", "Motion", name + ".txt");
    }

    public static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);

    public string Format()
    {
        var text = new StringBuilder();
        if(Length is {} length)Line("data_motLong = " + Number(length) + ";",true);
        for (int frame = 0; frame < Frames; frame++)
        {
            if (Step[frame] is { } step) Line("data_motStep[" + Index(frame) + "] = " + Number(step) + ";", true);
            if (PosX[frame] != 0) Line("data_motPosX[" + Index(frame) + "] = " + Number(PosX[frame]) + ";", true);
            if (PosY[frame] != 0) Line("data_motPosY[" + Index(frame) + "] = " + Number(PosY[frame]) + ";", true);
        }
        for (int part = 0; part < Parts; part++)
        {
            Line("with(Process[" + Index(part) + "]){", true);
            var channel = Channels[part];
            for (int frame = 0; frame < Frames; frame++)
            {
                string key = Index(frame) + "] = ";
                if (channel.PosDir[frame] != 0) Line("data_motPartPosDir[" + key + Number(channel.PosDir[frame]) + ";", true);
                if (channel.PosDis[frame] != 0) Line("data_motPartPosDis[" + key + Number(channel.PosDis[frame]) + ";", true);
                if (channel.Angle[frame] != 0) Line("data_motPartAngle[" + key + Number(channel.Angle[frame]) + ";", true);
                if (channel.Picture[frame] != 0) Line("data_motPartPicture[" + key + Number(channel.Picture[frame]) + ";", true);
                if (channel.ScaleX[frame] != 1) Line("data_motPartScaleX[" + key + Number(channel.ScaleX[frame]) + ";", true);
                if (channel.ScaleY[frame] != 1) Line("data_motPartScaleY[" + key + Number(channel.ScaleY[frame]) + ";", true);
            }
            Line("}", true);
        }
        return text.ToString();

        void Line(string value, bool write) { if (write) text.Append(value).Append('\n'); }
    }

    public void Save(string path) => NativeContentStore.WriteAtomic(path, new UTF8Encoding(false).GetBytes(Format()));

    public void SaveFor(string userRoot) => Save(Path(userRoot, MotionName));

    public static NativeMotionDocument Load(string path) => Parse(File.ReadAllText(path));

    private static readonly Regex Tokens = new(@"with\(Process\[(?<part>\d+)\]\)\{|(?<close>\})|(?<field>data_[A-Za-z]+)(?:\[(?<index>\d+)\])?\s*=\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    public static NativeMotionDocument Parse(string text)
    {
        var document = new NativeMotionDocument();
        int frames = 0, parts = 0, block = -1;
        var step = new SortedDictionary<int, double>();
        var posX = new SortedDictionary<int, double>();
        var posY = new SortedDictionary<int, double>();
        var channels = new SortedDictionary<int, SortedDictionary<string, SortedDictionary<int, double>>>();
        var scrubbed=Regex.Replace(text,@"//[^\r\n]*|/\*[\s\S]*?\*/","");
        string remainder=Tokens.Replace(scrubbed,"");
        if(remainder.Any(c=>!char.IsWhiteSpace(c)&&c!=';'))throw new InvalidDataException("Unsupported motion text syntax; import the original Motion AST for arithmetic expressions.");
        foreach (Match match in Tokens.Matches(scrubbed))
        {
            if (match.Groups["part"].Success) { block = int.Parse(match.Groups["part"].Value, CultureInfo.InvariantCulture); parts = Math.Max(parts, block + 1); continue; }
            if (match.Groups["close"].Success) { block = -1; continue; }
            string field = match.Groups["field"].Value;
            double value = double.Parse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!match.Groups["index"].Success) { if (field == "data_motLong") document.Length = value; continue; }
            int index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            frames = Math.Max(frames, index + 1);
            switch (field)
            {
                case "data_motStep": step[index] = value; break;
                case "data_motPosX": posX[index] = value; break;
                case "data_motPosY": posY[index] = value; break;
                default:
                    if (block < 0) break;
                    if (!channels.TryGetValue(block, out var channel)) channels[block] = channel = new();
                    if (!channel.TryGetValue(field, out var values)) channel[field] = values = new();
                    values[index] = value;
                    break;
            }
        }
        document.Resize(frames, parts);
        foreach (var pair in step) document.Step[pair.Key] = pair.Value;
        foreach (var pair in posX) document.PosX[pair.Key] = pair.Value;
        foreach (var pair in posY) document.PosY[pair.Key] = pair.Value;
        foreach (var pair in channels)
            foreach (var field in pair.Value)
                foreach (var entry in field.Value)
                    Assign(document.Channels[pair.Key], field.Key, entry.Key, entry.Value);
        return document;

        static void Assign(NativeMotionPart channel, string field, int index, double value)
        {
            if (index < 0 || index >= channel.PosDir.Length) return;
            switch (field)
            {
                case "data_motPartPosDir": channel.PosDir[index] = value; break;
                case "data_motPartPosDis": channel.PosDis[index] = value; break;
                case "data_motPartAngle": channel.Angle[index] = value; break;
                case "data_motPartPicture": channel.Picture[index] = value; break;
                case "data_motPartScaleX": channel.ScaleX[index] = value; break;
                case "data_motPartScaleY": channel.ScaleY[index] = value; break;
            }
        }
    }

    public IReadOnlyList<string> Overflow(int framesCapacity, int partsCapacity)
    {
        var messages = new List<string>();
        if (Frames > framesCapacity) messages.Add("关键帧数 " + Index(Frames) + " 超过该对象的 KEYFRM_MAX " + Index(framesCapacity) + "，超出的帧在运行时会静默丢弃。");
        if (Parts > partsCapacity) messages.Add("部件块数 " + Index(Parts) + " 超过该对象的 PARTS_MAX " + Index(partsCapacity) + "，超出的块在运行时会静默丢弃。");
        return messages;
    }

    public int WrittenParts() => Channels.Count(channel => Enumerable.Range(0, Frames).Any(channel.Written));
    public int WrittenFrames() => Enumerable.Range(0, Frames).Count(frame => Step[frame] is not null || PosX[frame] != 0 || PosY[frame] != 0 || Channels.Any(channel => channel.Written(frame)));
    private static string Index(int value) => value.ToString(CultureInfo.InvariantCulture);
}
