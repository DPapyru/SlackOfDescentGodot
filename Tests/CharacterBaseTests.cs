namespace SlackOfDescent;

public static class CharacterBaseTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Character base: " + message); }

        Check(NativeCharacterBones.Index("BODY_BOTTOM") == 0 && NativeCharacterBones.Index("HEAD") == 2 && NativeCharacterBones.Index("TAIL") == 2, "bone index reads the doll process order");
        Check(NativeCharacterBones.Index("UNKNOWN") == -1, "unknown target has no bone");
        Check(NativeCharacterBones.RestOffset("BODY_BOTTOM") == (0, 0) && NativeCharacterBones.RestOffset("HEAD") == (1, -18), "rest pose keeps the standing offsets from StandFem0");

        var part = new NativeCharacterPart { Target = "HEAD", Width = 42, Height = 35, AnchorX = 20, AnchorY = 34, OffsetX = 5, OffsetY = -2 };
        Check(NativeCharacterLayout.Place(part) == (6, -20), "part offset adds to the bone rest position");
        Check(NativeCharacterLayout.Corner(part) == (-14, -54), "corner subtracts the sprite origin");
        Check(NativeCharacterLayout.Bounds(part) == new NativePixelRect(-14, -54, 42, 35), "bounds describe the part in character space");

        var definition = new CharacterDefinition();
        var body = Make(definition, "BODY_BOTTOM", 20, 20, 0, 10, 10);
        var hair = Make(definition, "HEAD", 10, 10, 20, 5, 5);
        var bounds = NativeCharacterLayout.Bounds(definition);
        Check(bounds.X == -10 && bounds.Y == -23 && bounds.Width == 20 && bounds.Height == 33, "character bounds enclose every part, got " + bounds);
        Check(NativeCharacterLayout.Ordered(definition).First().Id == body.Id, "ordered parts start with the lowest layer");

        var pixels = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in definition.Parts) pixels[item.Frames[0].Id] = new byte[item.Width * item.Height * 4];
        byte[] Frame(NativeCharacterPart item) => pixels[item.Frames[0].Id];
        int hairOffset = (2 * 10 + 2) * 4;
        Frame(hair)[hairOffset + 3] = 255;
        Check(NativeCharacterLayout.Hit(definition, Frame, -2, -21) == hair.Id, "top layer wins the pixel hit test");
        Frame(body)[(5 * 20 + 5) * 4 + 3] = 255;
        Check(NativeCharacterLayout.Hit(definition, Frame, -5, -5) == body.Id, "transparent top layers fall through to the body");
        Check(NativeCharacterLayout.Hit(definition, Frame, 100, 100) is null, "empty space hits nothing");
        Frame(hair)[hairOffset + 3] = 0;
        Check(NativeCharacterLayout.Hit(definition, Frame, -2, -21) is null, "transparent pixels never capture a stroke");
        hair.Visible = false;
        Frame(hair)[hairOffset + 3] = 255;
        Check(NativeCharacterLayout.Hit(definition, Frame, -2, -21) is null, "hidden layers are not hit");
        Check(NativeCharacterLayout.Ordered(definition).All(p => p.Id != hair.Id), "hidden layers leave the draw order");
        hair.Visible = true;

        var available = new HashSet<string>(StringComparer.Ordinal)
        {
            "Chara/Doll/Body/Character-Nana_Body_Bottom.gif", "Chara/Doll/Body/Character-Nana_Body_Top.gif",
            "Chara/Doll/Head/Character-Nana_Hair.gif", "Chara/Doll/Head/Character-Nana_Eye.gif", "Chara/Doll/Head/Character-Nana_Mouth.gif",
            "Chara/Doll/Hand_Above/Character-Nana_Hand1_above.gif", "Chara/Doll/Hand_Above/Character-Nana_Hand2_above.gif",
            "Chara/Doll/Foot_Above/Character-Nana_Shoes_above.gif", "Chara/Doll/Foot_Above/Character-Nana_Socks2_above.gif",
        };
        var resolved = NativeCharacterBase.Resolve("Nana", available);
        Check(resolved.Count == 9, "every available slot resolves exactly once, got " + resolved.Count);
        Check(resolved.Select(r => r.Asset).Distinct().Count() == resolved.Count, "base generation never plans the same asset twice");
        Check(resolved.All(r => r.Slot.Target is "BODY_BOTTOM" or "BODY_TOP" or "HEAD" or "HAND1_ABOVE" or "HAND2_ABOVE" or "FOOT1_ABOVE" or "FOOT2_ABOVE"), "planned parts attach to real doll bones");
        Check(resolved.Select(r => r.Slot.Layer).Distinct().Count() == resolved.Count, "planned layers stay unique so the draw order is stable");
        Check(resolved.Select(r => r.Slot.Layer).SequenceEqual(resolved.Select(r => r.Slot.Layer).Order()), "planned layers increase with the intended draw order");
        Check(NativeCharacterBase.Resolve("Ryoko", available).All(r => r.Asset.Contains("Ryoko", StringComparison.Ordinal)) || NativeCharacterBase.Resolve("Ryoko", available).Count == 0, "presets select their own asset family");
        Check(NativeCharacterBase.Resolve("Nana", new HashSet<string>(StringComparer.Ordinal)).Count == 0, "missing assets produce no plan");
        var ryo = new HashSet<string>(StringComparer.Ordinal) { "Chara/Doll/Body/Character-Ryoko_Body_Bottom_Pants.gif", "Chara/Doll/Foot_Above/Character-Ryoko_Socks0_above.gif" };
        var ryoko = NativeCharacterBase.Resolve("Ryoko", ryo);
        Check(ryoko.Count == 2 && ryoko.All(r => r.Asset.Contains("Ryoko", StringComparison.Ordinal)), "fallback assets resolve for the active preset");
        Check(ryoko.Single(r => r.Slot.Target == "FOOT2_ABOVE").Asset.EndsWith("Socks0_above.gif", StringComparison.Ordinal), "the first available fallback wins");

        var existing = new CharacterDefinition();
        var previous = Make(existing, "HEAD", 8, 8, 5, 4, 4);
        previous.Name = "Hair";
        var plan = NativeCharacterBase.Plan(existing, "Nana", available);
        Check(plan.Single(p => p.Part == "Hair").Existing == previous.Id, "the plan reports parts the editor already holds");
        Check(plan.Single(p => p.Part == "Body_Bottom").Existing is null, "the plan leaves unknown parts unassigned");
        Check(plan.Count == 9 && plan.Select(p => p.Asset).Distinct().Count() == 9, "planning keeps the resolved assets");

        var target = NativeCharacterDocument.Create();
        target.Definition.Parts.Clear();
        foreach (var item in resolved)
        {
            var created = target.AddPart(item.Part, 1, 1, item.Slot.Layer);
            created.Target = item.Slot.Target; created.AnchorX = 1; created.AnchorY = 1;
            target.ImportFrames(created.Id, [new(4, 4, new byte[64])], anchorX: 2, anchorY: 3);
        }
        var composed = NativeCharacterLayout.Bounds(target.Definition);
        Check(composed.Width > 0 && composed.Height > 0, "a generated base document composes into a drawable rectangle");
        Check(target.Definition.Parts.All(p => p.Frames.Count > 0 && p.AnchorX == 2 && p.AnchorY == 3), "generated parts keep imported frames and anchors");
        Check(target.CanUndo, "generating a base document is undoable");
        return checks;
    }
    private static NativeCharacterPart Make(CharacterDefinition definition, string target, int width, int height, int layer, int anchorX, int anchorY)
    {
        var part = new NativeCharacterPart { Target = target, Width = width, Height = height, Layer = layer, AnchorX = anchorX, AnchorY = anchorY };
        part.Frames.Add(new NativeCharacterFrame());
        definition.Parts.Add(part);
        return part;
    }
}
