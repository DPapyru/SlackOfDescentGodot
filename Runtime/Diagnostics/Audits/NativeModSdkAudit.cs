using System.Text.Json;
using Godot;

namespace SlackOfDescent;

public partial class NativeModSdkAudit : Node
{
    private int assertions;
    private void Expect(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); assertions++; }
    public override void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "native-sdk-audit-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temporary);
        try
        {
            string project = ProjectSettings.GlobalizePath("res://");
            var world = new GmWorld(Path.Combine(project, "GameData"), Path.Combine(project, "GameData", "assets"), temporary);
            var registry = new ContentRegistry();
            using var bridge = new GmNativeBridge(world, registry);
            world.Evaluator.ResetBudget();
            foreach (string name in new[] { "Quake", "Spark", "MotionChange", "move_outside_afterBind", "endBind" })
                world.Code[world.Scripts[name]] = JsonSerializer.SerializeToElement(new object[] { "group", Array.Empty<object>() });
            world.Platform = (name, args, context) => name.StartsWith("sound_", StringComparison.Ordinal) || name.StartsWith("effect_", StringComparison.Ordinal) ? 0 : null;
            VerifyInput(registry, bridge.Services, temporary);
            VerifyData(temporary);
            VerifyEvents(registry, bridge.Services, world);
            VerifyEngineEvents(registry, world);
            VerifyEntities(registry, bridge, world);
            VerifyCombat(bridge.Services, world);
            VerifyWeapon(registry, bridge, world);
            VerifyController(registry, bridge, world);
            VerifyOwnership(registry, bridge, world, temporary);
            GD.Print("NATIVE_MOD_SDK_AUDIT_OK assertions=", assertions);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_MOD_SDK_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { System.IO.Directory.Delete(temporary, true); }
    }
    private void VerifyInput(ContentRegistry registry, NativeModServices services, string temporary)
    {
        registry.RegisterInput(new() { Id = "audit.reload", Name = "换弹", DefaultKey = 82 });
        var pressed = new InputSnapshot(new HashSet<int> { 82 }, new HashSet<int> { 82 }, new HashSet<int>());
        Expect(services.Input.Pressed("audit.reload", pressed) && services.Input.Down("audit.reload", pressed), "named operation resolves its default binding");
        services.Input.Context = NativeInputContext.Menu;
        Expect(!services.Input.Pressed("audit.reload", pressed), "game operation is gated in menu context");
        services.Input.Context = NativeInputContext.Editor;
        Expect(!services.Input.Down("audit.reload", pressed), "game operation is gated in editor context");
        services.Input.Context = NativeInputContext.Game; services.Input.SetKey("audit.reload", 84);
        Expect(services.Input.GetKey("audit.reload") == 84 && !services.Input.Pressed("audit.reload", pressed), "rebind replaces the default key");
        var persisted = new NativeInputService(registry, temporary);
        Expect(persisted.GetKey("audit.reload") == 84, "rebind persists across service recreation");
        Expect(persisted.Released("audit.reload", new(new HashSet<int>(), new HashSet<int>(), new HashSet<int> { 84 })), "released operation resolves rebound key");
    }
    private void VerifyData(string temporary)
    {
        NativeModData first = new(temporary, "audit.first"), second = new(temporary, "audit.second");
        first.Write("state", new[] { 4, 5 });
        Expect(first.Read("state", Array.Empty<int>()).SequenceEqual(new[] { 4, 5 }) && second.Read("state", Array.Empty<int>()).Length == 0, "mod data persists with package isolation");
        bool rejected = false; try { first.Write("../outside", 1); } catch (ArgumentException) { rejected = true; }
        Expect(rejected, "data keys cannot escape their owner directory");
        first.Delete("state"); Expect(first.Read("state", 17) == 17, "deleted data returns fallback");
    }
    private void VerifyEvents(ContentRegistry registry, NativeModServices services, GmWorld world)
    {
        var actor = new ActorContext(world, new GmInstance { Id = 800000 });
        int original = 0;
        registry.EventHandlers.Add("audit.before", new("audit", "audit.event", NativeEventPhase.Before, value => { value.Handled = true; value.Result = 23; }));
        registry.EventHandlers.Add("audit.after", new("audit", "audit.event", NativeEventPhase.After, value => value.Result = value.Result.Number + 1));
        registry.EventHandlers.Add("audit.error", new("audit", "audit.event", NativeEventPhase.After, value => throw new InvalidOperationException("expected-audit-handler-error")));
        Expect(services.Invoke("audit.event", actor, () => ++original).Int == 24 && original == 0, "before takeover and after result modification preserve exception isolation");
        registry.EventHandlers.Clear();
        Expect(services.Invoke("audit.event", actor, () => ++original).Int == 1, "removed subscribers no longer run");
    }
    private void VerifyEngineEvents(ContentRegistry registry,GmWorld world)
    {
        world.Code["scripts/AuditSync"]=world.Code[world.Compile("return argument0+1;")];
        int before=0,after=0;
        registry.EventHandlers.Add("audit.engine.before",new("audit","engine.auditsync",NativeEventPhase.Before,value=>
        {before++;Expect(value.Arguments["argument0"].Int==8,"engine event exposes original arguments");}));
        registry.EventHandlers.Add("audit.engine.after",new("audit","engine.auditsync",NativeEventPhase.After,value=>{after++;value.Result=value.Result.Number+1;}));
        var context=new GmContext(new GmInstance(),args:[8]);
        Expect(world.Evaluator.Run("scripts/AuditSync",context).Int==10&&before==1&&after==1,"synchronous engine execution has one before/after pair and modified result");
        Expect(world.Evaluator.RunAsync("scripts/AuditSync",context).GetAwaiter().GetResult().Int==10&&before==2&&after==2,"async to sync fallback dispatches exactly once");
        world.Code["scripts/AuditWait"]=world.Code[world.Compile("io_handle();return 9;")];
        world.Wait=(_,_,_)=>ValueTask.FromResult<GmValue>(0);
        registry.EventHandlers.Add("audit.engine.wait.before",new("audit","engine.auditwait",NativeEventPhase.Before,_=>before++));
        registry.EventHandlers.Add("audit.engine.wait.after",new("audit","engine.auditwait",NativeEventPhase.After,value=>{after++;value.Result=value.Result.Number+2;}));
        Expect(world.Evaluator.RunAsync("scripts/AuditWait",context).GetAwaiter().GetResult().Int==11&&before==3&&after==3,"waiting engine execution has one before/after pair");
        registry.EventHandlers["audit.engine.wait.before"]=new("audit","engine.auditwait",NativeEventPhase.Before,value=>{value.Handled=true;value.Result=20;});
        Expect(world.Evaluator.RunAsync("scripts/AuditWait",context).GetAwaiter().GetResult().Int==22&&after==4,"before handler replaces engine execution and after still runs");
        registry.EventHandlers.Clear();world.Wait=null;
    }
    private void VerifyEntities(ContentRegistry registry, GmNativeBridge bridge, GmWorld world)
    {
        registry.Resources.Add("audit.entity.image", "image.png");
        registry.RegisterEntity(new NativeEntityDefinition { Id = "audit.entity", ResourceId = "audit.entity.image", Width = 12, Height = 8, Parameters = new() { ["value"] = new() { Default = "7", Min = 0, Max = 10 } } }, () => new AuditEntity());
        var entity = bridge.Services.Entities.Spawn("audit.entity", 20, 30);
        Expect(entity.Read("created") == 1 && entity.Read("entered") == 1 && entity.Read("value") == 7, "entity defaults and creation lifecycle initialize once");
        var bounds = world.Bounds(entity.Instance);
        Expect(bounds.Left == 14 && bounds.Right == 26 && bounds.Top == 26 && bounds.Bottom == 34, "native collision shape matches the declared dimensions");
        bridge.Update(new(FrameContext.FixedDelta, 0, 0, InputSnapshot.Empty));
        Expect(entity.Read("updates") == 1 && bridge.Services.Entities.Query("audit.entity").Count == 1, "native entity participates in update and query");
        Expect(bridge.Services.Presentation.Sprites.Count(sprite => sprite.Owner == entity.Id) == 1
            && bridge.Services.Presentation.Sprites.Single(sprite => sprite.Owner == entity.Id).Angle == 30,
            "entity behavior replaces its default sprite without a duplicate visual");
        bridge.Services.Entities.Interact(entity.Id, entity);
        Expect(entity.Read("interactions") == 1, "entity interaction reaches its behavior");
        world.CurrentRoom = 42; bridge.Update(new(FrameContext.FixedDelta, 0, 1, InputSnapshot.Empty));
        Expect(entity.Read("exited") == 1 && entity.Read("entered") == 2, "room transition delivers exit and entry");
        bridge.Services.Entities.Destroy(entity.Id); bridge.Update(new(FrameContext.FixedDelta, 0, 2, InputSnapshot.Empty));
        Expect(entity.Read("destroyed") == 1 && entity.Read("detached") == 1 && bridge.Services.Entities.Query("audit.entity").Count == 0, "destroy removes native entity and detaches behavior exactly once");
        int before = world.Instances.Count; bool rejected = false;
        try { bridge.Services.Entities.Spawn("audit.entity", 0, 0, parameters: new Dictionary<string, GmValue> { ["value"] = 11 }); } catch (InvalidDataException) { rejected = true; }
        Expect(rejected && world.Instances.Count == before, "invalid entity parameter leaves no partial instance");
        registry.Resources.Remove("audit.entity.image");
    }
    private void VerifyCombat(NativeModServices services, GmWorld world)
    {
        var source = new ActorContext(world, world.CreateNativeEntity(0, 0));
        GmInstance Enemy(double x)
        {
            var instance = world.Create(world.Constants["objEnemyDoll"].Int, x, 0, run: false);
            instance.Scope.Write("mask_index", world.Constants["sprMask"]); instance.Scope.Write("DAMAGE_ACTIVE", 1);
            instance.Scope.Write("HP", 1000); instance.Scope.Write("INV", 0); instance.Scope.Write("WEAK", 0);
            instance.Scope.Write("MotionNum", 0); instance.Scope.Write("DEAD_MOTION", 1); instance.Scope.Write("BindTarget", -4);
            return instance;
        }
        GmInstance far = Enemy(240), near = Enemy(120);
        var hit = services.Combat.Raycast(source, 0, 0, 0, 1600);
        Expect(hit is {} first && first.Target.Id == near.Id && !first.Wall, "ray selects the nearest enemy independently of instance ordering");
        Expect(services.Combat.Damage(source, hit!.Value, 300, 0) && near.Scope.ReadOr("HP").Int == 700 && far.Scope.ReadOr("HP").Int == 1000, "one ray applies requested damage to one enemy");
        near.Scope.Write("WEAK", 1); services.Combat.Damage(source, hit.Value, 300, 0);
        Expect(near.Scope.ReadOr("HP").Int == 200 && near.Scope.ReadOr("RECV_DAMAGE").Truth, "damage preserves original weakness and receive flags");
        near.Scope.Write("INV", 1); services.Combat.Damage(source, hit.Value, 300, 0);
        Expect(near.Scope.ReadOr("HP").Int == 200, "damage preserves original invulnerability");
        var wall = world.Create(world.Constants["objBlock"].Int, 60, 0, run: false);
        wall.Scope.Write("mask_index", world.Constants["sprMask"]); wall.Scope.Write("solid", 1);
        var stopped = services.Combat.Raycast(source, 0, 0, 0, 1600);
        Expect(stopped is {} stop && stop.Wall && stop.Target.Id == wall.Id && !services.Combat.Damage(source, stop, 300, 0), "nearest wall stops the ray without enemy damage");
        Expect(services.Combat.Raycast(source, 0, 0, 180, 30) is null, "finite ray range excludes remote geometry");
        wall.Destroyed = true; near.Destroyed = true; far.Destroyed = true;
        var edge=Enemy(0);var shape=world.Bounds(edge);
        foreach(int angle in new[]{0,90,180,270})
        {
            edge.Scope.Write("x",angle==0?100-shape.Left:angle==180?-100-shape.Right:0);
            edge.Scope.Write("y",angle==90?-100-shape.Bottom:angle==270?100-shape.Top:0);
            var boundary=services.Combat.Raycast(source,0,0,angle,100);
            Expect(boundary is {} endpoint&&endpoint.Target.Id==edge.Id&&Math.Abs(endpoint.Distance-100)<1e-8,"ray includes exact range boundary at angle "+angle);
            Expect(services.Combat.Raycast(source,0,0,angle,99.9) is null,"ray excludes targets outside finite range at angle "+angle);
        }
        edge.Destroyed=true;source.Instance.Destroyed = true;
    }
    private void VerifyWeapon(ContentRegistry registry, GmNativeBridge bridge, GmWorld world)
    {
        registry.RegisterAction("audit.weapon", () => new AuditWeapon());
        var actor = world.CreateNativeEntity(0, 0);
        var controller = world.Create(world.Constants["objController"].Int, 0, 0, run: false);
        controller.Scope.Write("CtrlTarget", actor.Id); controller.Scope.Write("Aim", 1); controller.Scope.Write("m_dir", 0);
        bridge.BindCharacter(actor, new() { Actions = new() { [CharacterActionSlot.Attack] = "audit.weapon" } });
        var gun = world.Create(world.Constants["objNana_Gun"].Int, 0, 0, run: false);
        gun.Scope.Write("DrawTarget", actor.Id); gun.Scope.Write("Gun", 0); gun.Scope.Write("Bullet", 8);
        int bullets = world.Created.GetValueOrDefault(world.Constants["objBullet"].Int);
        world.Frame = 100; world.Event(gun, 7, 10); world.Event(gun, 7, 10);
        Expect(actor.Scope.ReadOr("shots").Int == 1 && gun.Scope.ReadOr("Bullet").Int == 8 && world.Created.GetValueOrDefault(world.Constants["objBullet"].Int) == bullets, "weapon consumes original fire once and prevents vanilla ammo and bullets");
        world.Frame++; world.Event(gun, 7, 10); world.Event(gun, 7, 11); world.Event(gun, 7, 11);
        Expect(actor.Scope.ReadOr("shots").Int == 2 && actor.Scope.ReadOr("reloads").Int == 1, "fire and reload consume separate requests once per original frame");
        bridge.UnbindCharacter(actor.Id);
        Expect(actor.Scope.ReadOr("detached").Int == 1, "weapon detaches when character is unbound");
    }
    private void VerifyController(ContentRegistry registry,GmNativeBridge bridge,GmWorld world)
    {
        registry.RegisterAction("audit.controller",()=>new AuditController());
        var actor=world.CreateNativeEntity(0,0);
        var controller=world.Create(world.Constants["objController"].Int,0,0,run:false);controller.Scope.Write("CtrlTarget",actor.Id);controller.Scope.Write("vanillaControl",0);
        const string code="objects/objController/3/0/0";var original=world.Code[code];
        world.Code[code]=world.Code[world.Compile("vanillaControl+=1;")];
        bridge.BindCharacter(actor,new(){Actions=new(){[CharacterActionSlot.Controller]="audit.controller"}});
        bridge.Update(new(FrameContext.FixedDelta,0,0,new(new HashSet<int>{68},new HashSet<int>{68},new HashSet<int>())));
        world.Evaluator.RunAsync(code,new(controller)).GetAwaiter().GetResult();
        Expect(actor.Scope.ReadOr("x").Number==3&&controller.Scope.ReadOr("vanillaControl").Number==0,"full controller receives snapshot and suppresses original controller execution");
        bridge.UnbindCharacter(actor.Id);world.Evaluator.RunAsync(code,new(controller)).GetAwaiter().GetResult();
        Expect(controller.Scope.ReadOr("vanillaControl").Number==1,"unbinding full controller restores original execution");
        registry.RegisterAction("audit.controller.second",()=>new AuditController(CharacterActionSlot.Dash));
        bool rejected=false;
        try{bridge.BindCharacter(actor,new(){Actions=new(){[CharacterActionSlot.Controller]="audit.controller",[CharacterActionSlot.Dash]="audit.controller.second"}});}
        catch(InvalidDataException){rejected=true;}
        Expect(rejected,"two full controllers on one character are rejected");bridge.UnbindCharacter(actor.Id);
        world.Code[code]=original;
    }
    private void VerifyOwnership(ContentRegistry registry, GmNativeBridge bridge, GmWorld world, string temporary)
    {
        var sounds = new HashSet<int>(); var deleted = new HashSet<int>(); int nextSound = 1000;
        world.Platform = (name, args, context) =>
        {
            if (name == "sound_add") { sounds.Add(++nextSound); return nextSound; }
            if (name == "sound_delete") { deleted.Add(args[0].Int); sounds.Remove(args[0].Int); return 0; }
            return name.StartsWith("sound_", StringComparison.Ordinal) ? 0 : null;
        };
        registry.Resources.Add("audit.shared.image", "image.png"); registry.Resources.Add("audit.shared.audio", "audio.wav");
        using var manager = new NativeModManager(registry);
        string Package(string id, CharacterActionSlot slot)
        {
            string directory = Path.Combine(temporary, id); System.IO.Directory.CreateDirectory(directory);
            NativeContentStore.Save(Path.Combine(directory, "mod.json"), new NativeModManifest { FormatVersion = 1, Id = id, EntryType = "ScopeMod", Sources = ["ScopeMod.cs"] });
            File.WriteAllText(Path.Combine(directory, "ScopeMod.cs"), $$"""
                using SlackOfDescent;
                public sealed class ScopeMod : IMod
                {
                    public string Id => "{{id}}";
                    public void Load(ModContext context)
                    {
                        context.Content.RegisterEntity(new NativeEntityDefinition { Id = "{{id}}.entity" }, () => new ScopeEntity());
                        context.Content.RegisterAction("{{id}}.action", () => new ScopeAction());
                        context.Subscribe("{{id}}.event", "audit.scope", NativeEventPhase.After, value =>
                        { if (value.Actor is {} actor) actor.Services.Presentation.Text(actor.Id, "event", "{{id}}"); });
                    }
                    public void Unload() { }
                }
                public sealed class ScopeAction : ICharacterAction
                {
                    public string Id => "{{id}}.action";
                    public CharacterActionSlot Slot => CharacterActionSlot.{{slot}};
                    private bool created;
                    public void Update(ActorContext actor, FrameContext frame)
                    {
                        if (created) return; created = true;
                        actor.Services.Presentation.Text(actor.Id, "shared", "{{id}}");
                        actor.Services.Presentation.Sprite(actor.Id, "shared", "audit.shared.image", 1, 2);
                        actor.Services.Audio.Play(actor, "audit.shared.audio", true);
                        actor.Services.Entities.Spawn("{{id}}.entity", 0, 0, actor.Id);
                    }
                    public void Detach(ActorContext actor) { actor.Services.Presentation.Clear(actor.Id); actor.Services.Audio.Clear(actor.Id); }
                }
                public sealed class ScopeEntity : IEntityBehavior
                {
                    public string Id => "{{id}}.entity";
                    public void Created(ActorContext actor) { }
                    public void Update(ActorContext actor, FrameContext frame) { }
                }
                """);
            return directory;
        }
        string first = Package("audit.scope-a", CharacterActionSlot.Attack), second = Package("audit.scope-b", CharacterActionSlot.Dash);
        Expect(manager.ReloadMany([first, second]).Success, "independent ownership packages compile and activate");
        var actor = world.CreateNativeEntity(2000, 2000);
        bridge.BindCharacter(actor, new() { Actions = new() { [CharacterActionSlot.Attack] = "audit.scope-a.action", [CharacterActionSlot.Dash] = "audit.scope-b.action" } });
        FrameContext frame = new(FrameContext.FixedDelta, 10, 600, InputSnapshot.Empty);
        bridge.Update(frame); bridge.Services.Invoke("audit.scope", new(world, actor), () => 0);
        var keptHud = bridge.Services.Presentation.Hud.Single(h => h.Owner == actor.Id && h.Id == "shared" && h.Text == "audit.scope-b");
        var keptSprite = bridge.Services.Presentation.Sprites.Last(h => h.Owner == actor.Id);
        int keptEntity = bridge.Services.Entities.Query("audit.scope-b.entity").Single().Id, keptSound = sounds.Max();
        Expect(bridge.Services.Presentation.Hud.Count(h => h.Owner == actor.Id) == 4 && sounds.Count == 2, "same actor and presentation IDs coexist across action and event owners");
        string sourcePath = Path.Combine(first, "ScopeMod.cs"), source = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath, source.Replace("private bool created;", "public ScopeAction() { throw new System.InvalidOperationException(\"scope-factory-failed\"); } private bool created;", StringComparison.Ordinal));
        var failed = manager.Reload(first); File.WriteAllText(sourcePath, source);
        Expect(!failed.Success && bridge.Services.Presentation.Hud.Contains(keptHud) && sounds.Count == 2 && bridge.Services.Entities.Find(keptEntity) is not null, "candidate construction failure retains every live contribution");
        var changed = manager.Reload(first);
        Expect(changed.Success && ReferenceEquals(keptHud, bridge.Services.Presentation.Hud.Single(h => h.Owner == actor.Id && h.Id == "shared"))
            && bridge.Services.Presentation.Sprites.Contains(keptSprite) && sounds.SetEquals([keptSound]) && !deleted.Contains(keptSound)
            && bridge.Services.Entities.Find(keptEntity) is not null, "reloading A preserves B one-time HUD, sprite, sound and entity without another update");
        Expect(!bridge.Services.Presentation.Hud.Any(h => h.Owner == actor.Id && h.Text == "audit.scope-a"), "successful reload clears previous action and event generation contributions");
        Expect(changed.PreviousContext is {} prior && NativeModManager.VerifyUnloaded(prior), "replaced owner scope retains no collectible assembly references");
        bridge.Update(frame);
        var removed = manager.Unload("audit.scope-a");
        Expect(removed.Success && bridge.Services.Presentation.Hud.Contains(keptHud) && sounds.SetEquals([keptSound]) && bridge.Services.Entities.Find(keptEntity) is not null, "unloading A preserves B one-time contributions");
        var replaced = manager.Reload(second);
        Expect(replaced.Success && !bridge.Services.Presentation.Hud.Contains(keptHud) && !bridge.Services.Presentation.Sprites.Contains(keptSprite)
            && deleted.Contains(keptSound) && bridge.Services.Entities.Find(keptEntity) is null, "reloading B clears its previous resources before replacement update");
        bridge.Update(frame);
        Expect(sounds.Count == 1 && bridge.Services.Entities.Query("audit.scope-b.entity").Single().Id != keptEntity, "new B generation recreates only its own one-time resources");
        var secondSprite = bridge.Services.Presentation.Sprites.Single(h => h.Owner == actor.Id);
        Expect(manager.Reload(first).Success, "independent owner A can be restored before another package lifecycle fails");
        bridge.Update(frame);
        var keptFirstHud = bridge.Services.Presentation.Hud.Single(h => h.Owner == actor.Id && h.Id == "shared" && h.Text == "audit.scope-a");
        var keptFirstSprite = bridge.Services.Presentation.Sprites.Single(h => h.Owner == actor.Id && !ReferenceEquals(h, secondSprite));
        int keptFirstEntity = bridge.Services.Entities.Query("audit.scope-a.entity").Single().Id, keptFirstSound = sounds.Max();
        var lifecycleActor = world.CreateNativeEntity(3000, 3000); bridge.AttachEntity(lifecycleActor.Id, "audit.scope-b.entity");
        sourcePath = Path.Combine(second, "ScopeMod.cs"); source = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath, source.Replace("public void Created(ActorContext actor) { }", "public void Created(ActorContext actor) { throw new System.InvalidOperationException(\"scope-created-failed\"); }", StringComparison.Ordinal));
        var lifecycle = manager.Reload(second);
        Expect(lifecycle.Success && lifecycle.Diagnostics.Contains("audit.scope-b/entity:audit.scope-b.entity", StringComparison.Ordinal)
            && lifecycle.Diagnostics.Contains("scope-created-failed", StringComparison.Ordinal), "activated entity lifecycle failure reports package and contribution in reload diagnostics");
        Expect(bridge.Services.Presentation.Hud.Any(item => ReferenceEquals(item, keptFirstHud)) && bridge.Services.Presentation.Sprites.Any(item => ReferenceEquals(item, keptFirstSprite))
            && sounds.SetEquals([keptFirstSound]) && !deleted.Contains(keptFirstSound) && bridge.Services.Entities.Find(keptFirstEntity) is not null,
            "B lifecycle failure preserves A one-time presentation, sound and entity without another update");
        manager.Unload("audit.scope-a");
        manager.Unload("audit.scope-b"); bridge.Update(frame); bridge.UnbindCharacter(actor.Id);
        Expect(sounds.Count == 0 && !bridge.Services.Presentation.Hud.Any(h => h.Owner == actor.Id) && manager.RetiredContexts.All(context => NativeModManager.VerifyUnloaded(context)), "final owner unload clears resources and all candidate contexts collect");
        registry.Resources.Remove("audit.shared.image"); registry.Resources.Remove("audit.shared.audio");
    }
    private sealed class AuditController(CharacterActionSlot slot=CharacterActionSlot.Controller):ICharacterAction
    {
        public string Id=>"audit.controller";
        public CharacterActionSlot Slot=>slot;
        public CharacterActionMode Mode=>CharacterActionMode.Controller;
        public void Update(ActorContext actor,FrameContext frame){if(frame.Input.Down(68))actor.X+=3;}
    }
    private sealed class AuditWeapon : ICharacterWeapon
    {
        public string Id => "audit.weapon";
        public CharacterActionSlot Slot => CharacterActionSlot.Attack;
        public void Update(ActorContext actor, FrameContext frame) { }
        public void Fire(ActorContext actor, NativeFireRequest request) => actor.Write("shots", actor.Read("shots") + 1);
        public void Reload(ActorContext actor) => actor.Write("reloads", actor.Read("reloads") + 1);
        public void Detach(ActorContext actor) => actor.Write("detached", actor.Read("detached") + 1);
    }
    private sealed class AuditEntity : IEntityBehavior
    {
        public string Id => "audit.entity";
        public void Created(ActorContext actor) => actor.Write("created", actor.Read("created") + 1);
        public void Update(ActorContext actor, FrameContext frame)
        {
            actor.Write("updates", actor.Read("updates") + 1);
            actor.Services.Presentation.Sprite(actor.Id, "entity", "audit.entity.image", actor.X, actor.Y, 30);
        }
        public void Interacted(ActorContext actor, ActorContext other) => actor.Write("interactions", actor.Read("interactions") + 1);
        public void RoomEntered(ActorContext actor) => actor.Write("entered", actor.Read("entered") + 1);
        public void RoomExited(ActorContext actor) => actor.Write("exited", actor.Read("exited") + 1);
        public void Destroyed(ActorContext actor) => actor.Write("destroyed", actor.Read("destroyed") + 1);
        public void Detach(ActorContext actor) => actor.Write("detached", actor.Read("detached") + 1);
    }
}
