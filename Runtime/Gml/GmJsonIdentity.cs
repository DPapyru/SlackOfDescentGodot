using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SlackOfDescent;

public sealed class GmJsonIdentity : IEqualityComparer<JsonElement>
{
    public static readonly GmJsonIdentity Instance=new();
    [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_parent")]
    private static extern ref JsonDocument Parent(ref JsonElement element);
    [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_idx")]
    private static extern ref int Index(ref JsonElement element);
    public bool Equals(JsonElement a,JsonElement b)=>ReferenceEquals(Parent(ref a),Parent(ref b))&&Index(ref a)==Index(ref b);
    public int GetHashCode(JsonElement element)=>HashCode.Combine(RuntimeHelpers.GetHashCode(Parent(ref element)),Index(ref element));
}
