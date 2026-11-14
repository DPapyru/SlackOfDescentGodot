using System;
using System.Collections.Generic;
using SlackOfDescent;

namespace RopeBonds;

public enum BindPart { Hands, HandAbove, HandBelow, Feet, Body }

public static class BoneSets
{
    public static readonly string[] HandAbove = ["HAND1_ABOVE", "HAND2_ABOVE", "HAND3_ABOVE"];
    public static readonly string[] HandBelow = ["HAND1_BELOW", "HAND2_BELOW", "HAND3_BELOW"];
    public static readonly string[] Hands = [.. HandAbove, .. HandBelow];
    public static readonly string[] Feet = ["FOOT1_ABOVE", "FOOT2_ABOVE", "FOOT3_ABOVE", "FOOT1_BELOW", "FOOT2_BELOW", "FOOT3_BELOW"];
    public static readonly string[] Body = ["BODY_BOTTOM", "BODY_TOP", "HEAD"];
    public static IReadOnlyList<string> Of(BindPart part) => part switch
    {
        BindPart.Hands => Hands,
        BindPart.HandAbove => HandAbove,
        BindPart.HandBelow => HandBelow,
        BindPart.Feet => Feet,
        _ => Body
    };
    public static void Validate()
    {
        foreach (string name in UnionAll())
            if (Array.IndexOf(NativeCharacterBones.Order, name) < 0) throw new InvalidOperationException("Unknown bone name: " + name);
    }
    private static IEnumerable<string> UnionAll()
    {
        foreach (string name in HandAbove) yield return name;
        foreach (string name in HandBelow) yield return name;
        foreach (string name in Feet) yield return name;
        foreach (string name in Body) yield return name;
    }
}
