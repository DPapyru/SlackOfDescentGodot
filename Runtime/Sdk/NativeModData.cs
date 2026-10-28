using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed class NativeModData(string root, string owner)
{
    private readonly string directory = Path.Combine(root, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(owner)));
    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains('/') || key.Contains('\\') || key is "." or "..")
            throw new ArgumentException("数据键必须是单一文件名。", nameof(key));
        return Path.Combine(directory, key + ".json");
    }
    public T Read<T>(string key, T fallback) => File.Exists(PathFor(key)) ? NativeContentStore.Load<T>(PathFor(key)) : fallback;
    public void Write<T>(string key, T value) => NativeContentStore.Save(PathFor(key), value);
    public void Delete(string key) => File.Delete(PathFor(key));
}
