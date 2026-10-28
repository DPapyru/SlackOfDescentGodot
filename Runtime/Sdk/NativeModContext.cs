using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class ModContext
{
    internal string OwnerId { get; set; } = "";
    internal string? DataRoot { get; set; }
    private NativeModData? data;
    public NativeModData Data => data ??= new(DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlackOfDescentGodot", "Content", "ModData"),
        string.IsNullOrEmpty(OwnerId) ? Path.GetFileName(Directory) : OwnerId);
    public void Subscribe(string id, string eventName, NativeEventPhase phase, Action<NativeGameEvent> handler)
    { Content.EventHandlers.Add(id, new(OwnerId, eventName, phase, handler)); Content.Refresh(); }
}
