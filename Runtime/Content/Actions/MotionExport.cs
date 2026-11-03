using System.Text.RegularExpressions;

namespace SlackOfDescent;

public static class MotionExport
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id);
    public static string MotionPath(string id) => "motions/" + id + ".motion.json";
}
