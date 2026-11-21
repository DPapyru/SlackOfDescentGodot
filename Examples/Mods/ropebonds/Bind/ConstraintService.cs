using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class ConstraintService : IDisposable
{
    private const string HandGraph = "ropebonds.bound", HandClip = "bound", FootGraph = "ropebonds.legs", FootClip = "legs", QteGraph = "ropebonds.qte", QteClip = "qte";
    public static readonly string[] HandBones = MotionSkeleton.Bones.Where(name => name.StartsWith("HAND", StringComparison.Ordinal)).ToArray();
    public static readonly string[] FootBones = MotionSkeleton.Bones.Where(name => name.StartsWith("FOOT", StringComparison.Ordinal)).ToArray();
    private readonly BoneLayer hands = new("ropebonds.hand"), feet = new("ropebonds.leg");

    public void Attach(ModContext context)
    {
        hands.Attach(context);
        feet.Attach(context);
    }

    public bool Bind(ActorContext actor, BindLayer layer) =>
        Bind(actor, layer, layer == BindLayer.Hands ? HandGraph : FootGraph, layer == BindLayer.Hands ? HandClip : FootClip);

    public bool Bind(ActorContext actor, BindLayer layer, string graph, string clip) =>
        Bind(Layer(layer), Other(layer), actor, graph, clip, Bones(layer));

    public bool BindQte(ActorContext actor) => Bind(hands, feet, actor, QteGraph, QteClip, MotionSkeleton.Bones);

    public bool Release(ActorContext actor, BindLayer layer) => Layer(layer).Release(actor);

    public bool Release(ActorContext actor)
    {
        bool handsReleased = hands.Release(actor);
        bool feetReleased = feet.Release(actor);
        return handsReleased || feetReleased;
    }

    public void ReleaseAll()
    {
        hands.ReleaseAll();
        feet.ReleaseAll();
    }

    public bool Bound(int actorId) => hands.Holds(actorId) || feet.Holds(actorId);

    public bool Bound(int actorId, BindLayer layer) => Layer(layer).Holds(actorId);

    public (double X, double Y)? BoundHands(int actorId) => hands.Center(actorId, "HAND3_ABOVE", "HAND3_BELOW");

    public (double X, double Y)? BoundFeet(int actorId) => feet.Center(actorId, "FOOT3_ABOVE", "FOOT3_BELOW");

    public void Dispose() => ReleaseAll();

    private BoneLayer Layer(BindLayer layer) => layer == BindLayer.Hands ? hands : feet;

    private BoneLayer Other(BindLayer layer) => layer == BindLayer.Hands ? feet : hands;

    private static string[] Bones(BindLayer layer) => layer == BindLayer.Hands ? HandBones : FootBones;

    private static bool Bind(BoneLayer layer, BoneLayer other, ActorContext actor, string graph, string clip, string[] bones) =>
        !other.Holds(actor.Id, bones) && layer.Bind(actor, graph, clip, bones);
}
