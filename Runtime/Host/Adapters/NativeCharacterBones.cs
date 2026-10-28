using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public static class NativeCharacterBones
{
    public static readonly string[] Order = MotionSkeleton.Bones;
    private static readonly (int X, int Y)[] Rest = [(0, 0), (0, 0), (1, -18), (2, -15), (4, -3), (3, 9), (2, -15), (0, -3), (-2, 9), (1, 10), (-4, 32), (5, 55), (1, 10), (1, 32), (0, 57)];
    public static GmInstance? Resolve(GmWorld world,GmInstance actor,string name)
    {
        int id=actor.Scope.ReadOr(name=="TAIL"?"NEW_TAIL":name,-4).Int;
        return world.Instances.FirstOrDefault(i=>i.Id==id&&!i.Destroyed);
    }
    public static int Index(string target) => target == "TAIL" ? 2 : Array.IndexOf(Order, target);
    public static (int X, int Y) RestOffset(string target) { int index = Index(target); return index < 0 ? (0, 0) : Rest[index]; }
}
