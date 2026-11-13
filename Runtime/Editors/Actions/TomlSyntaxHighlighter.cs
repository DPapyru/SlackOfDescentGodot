using Godot;

namespace SlackOfDescent;

public partial class TomlSyntaxHighlighter : SyntaxHighlighter
{
    private static readonly Color Plain = new("#d4d4d4"), Comment = new("#6a9955"), String = new("#ce9178"), Number = new("#b5cea8"), Header = new("#4ec9b0"), Key = new("#9cdcfe"), Keyword = new("#c586c0");
    private static readonly Color Coordinate = new("#4fa3ff"), Angle = new("#ffb454"), Picture = new("#ff7eb6"), Enum = new("#c792ea");
    private static readonly string[] CoordinateFields = ["x", "y", "scaleX", "scaleY"], EnumFields = ["kind", "target", "comparison"];

    private static Color? Semantic(string key)
    {
        int dot = key.LastIndexOf('.');
        string field = (dot < 0 ? key : key[(dot + 1)..]).Trim();
        if (CoordinateFields.Contains(field)) return Coordinate;
        if (field == "angle") return Angle;
        if (field == "picture") return Picture;
        return EnumFields.Contains(field) ? Enum : null;
    }

    public override Godot.Collections.Dictionary _GetLineSyntaxHighlighting(int line)
    {
        var result = new Godot.Collections.Dictionary();
        var edit = GetTextEdit();
        if (edit is null || line < 0 || line >= edit.GetLineCount()) return result;
        string text = edit.GetLine(line);
        result[0] = new Godot.Collections.Dictionary { { "color", Plain } };
        void Paint(int start, int end, Color color)
        {
            if (end > start) result[start] = new Godot.Collections.Dictionary { { "color", color } };
        }
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('#')) { Paint(0, text.Length, Comment); return result; }
        if (trimmed.StartsWith('[')) { Paint(0, text.Length, Header); return result; }
        int index = 0;
        Color? semantic = null;
        while (index < text.Length)
        {
            char character = text[index];
            if (character == '#') { Paint(index, text.Length, Comment); break; }
            if (character == '"')
            {
                int start = index++;
                while (index < text.Length)
                {
                    if (text[index] == '\\') { index += 2; continue; }
                    if (text[index++] == '"') break;
                }
                Paint(start, Math.Min(index, text.Length), semantic ?? String);
                continue;
            }
            if (character == '=') { semantic = Semantic(text[..index]); index++; continue; }
            if (char.IsAsciiDigit(character) || (character == '-' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1])))
            {
                int start = index++;
                while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] is '.' or 'e' or 'E' or '+' or '-')) index++;
                Paint(start, index, semantic ?? Number);
                continue;
            }
            if (char.IsAsciiLetter(character) || character == '_')
            {
                int start = index++;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '_' or '-' or ':' or '.')) index++;
                Paint(start, index, text[start..index] is "true" or "false" ? Keyword : Key);
                continue;
            }
            index++;
        }
        return result;
    }
}
