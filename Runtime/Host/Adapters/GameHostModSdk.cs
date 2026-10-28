using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private Button? nativeBindingsButton;
    private Window? nativeBindingsWindow;
    private void SyncNativeBindings()
    {
        if(nativeBindingsButton is null)
        {
            nativeBindingsButton=new Button{Name="NativeInputBindings",AutoTranslateMode=AutoTranslateModeEnum.Disabled};
            nativeBindingsButton.Pressed+=OpenNativeBindings;AddChild(nativeBindingsButton);
        }
        nativeBindingsButton.Text=NativeLocalization.IsChinese?"Mod 按键":"Mod controls";
        nativeBindingsButton.Visible=grenadeBinding?.Visible==true&&content.InputActions.Count>0;
        nativeBindingsButton.Position=new((float)GetViewportRect().Size.X-360,4);nativeBindingsButton.Size=new(172,28);
    }
    private void OpenNativeBindings()
    {
        if(nativeBindingsWindow is not null)return;
        var window=new Window{Name="NativeInputBindingsWindow",Title=NativeLocalization.IsChinese?"Mod 按键":"Mod controls",Size=new(620,420),Transient=true,Exclusive=true,Visible=false};
        nativeBindingsWindow=window;
        var margin=new MarginContainer();margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);window.AddChild(margin);
        foreach(string side in new[]{"left","right","top","bottom"})margin.AddThemeConstantOverride("margin_"+side,16);
        var scroll=new ScrollContainer();margin.AddChild(scroll);var rows=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};scroll.AddChild(rows);
        string? selected=null;Button? waiting=null;
        foreach(var action in content.InputActions.Values.OrderBy(action=>NativeContentLocalization.Display(content,action.Id,NativeLocalization.Text(action.Name)),StringComparer.Ordinal))
        {
            var row=new HBoxContainer();rows.AddChild(row);
            row.AddChild(new Label{Text=NativeContentLocalization.Display(content,action.Id,NativeLocalization.Text(action.Name)),TooltipText=NativeContentLocalization.Description(content,action.Id,NativeLocalization.Text(action.Description)),AutoTranslateMode=AutoTranslateModeEnum.Disabled,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});
            var button=new Button{Name="Bind_"+action.Id,Text=OS.GetKeycodeString(GodotKey(nativeBridge.Services.Input.GetKey(action.Id))),CustomMinimumSize=new(150,28)};row.AddChild(button);
            button.SetMeta("action_id",action.Id);
            button.Pressed+=()=>
            {
                if(waiting is not null&&selected is not null)waiting.Text=OS.GetKeycodeString(GodotKey(nativeBridge.Services.Input.GetKey(selected)));
                selected=action.Id;waiting=button;button.Text=NativeLocalization.IsChinese?"请按一个键…":"Press a key…";
            };
        }
        window.WindowInput+=input=>
        {
            if(selected is null||waiting is null||input is not InputEventKey{Pressed:true,Echo:false} key)return;
            int code=VirtualKey(key.Keycode);if(key.Keycode!=Key.Escape&&code is >=2 and <=255)nativeBridge.Services.Input.SetKey(selected,code);
            else if(key.Keycode!=Key.Escape)return;
            waiting.Text=OS.GetKeycodeString(GodotKey(nativeBridge.Services.Input.GetKey(selected)));selected=null;waiting=null;
        };
        window.CloseRequested+=window.Hide;
        window.VisibilityChanged+=()=>
        {
            if(window.Visible||nativeBindingsWindow!=window)return;
            nativeBindingsWindow=null;modalDepth--;ResetNativeInput();window.QueueFree();
        };
        AddChild(window);modalDepth++;ResetNativeInput();window.PopupCentered();
    }
    private void NativeSdkDraws(List<GmGraphics.DepthDraw> draws)
    {
        foreach (var line in nativeBridge.Services.Presentation.Lines)
        {
            var owner = nativeBridge.Services.Entities.Find(line.Owner);
            if (owner is null || !owner.Instance.Active) continue;
            draws.Add(new(-10000, () => graphics.Call("draw_line_width_color", [line.X1, line.Y1, line.X2, line.Y2, line.Width, line.Colour, line.Colour], new(owner.Instance))));
        }
        foreach (var sprite in nativeBridge.Services.Presentation.Sprites)
        {
            var owner = nativeBridge.Services.Entities.Find(sprite.Owner);
            if (owner is null || !owner.Instance.Active || !content.Resources.TryGetValue(sprite.Resource, out string? path)) continue;
            draws.Add(new(sprite.Depth, owner.Instance, path, new((float)sprite.OriginX, (float)sprite.OriginY), new((float)sprite.X, (float)sprite.Y),
                new((float)sprite.ScaleX, (float)sprite.ScaleY), -Mathf.DegToRad((float)sprite.Angle)));
        }
        foreach (var item in nativeBridge.Services.Presentation.Hud)
        {
            var owner = nativeBridge.Services.Entities.Find(item.Owner);
            if (owner is null || !owner.Instance.Active) continue;
            draws.Add(new(-1_000_000_000, () =>
            {
                var context = new GmContext(owner.Instance);
                var colour = graphics.Call("draw_get_color", [], context) ?? 16777215;
                var alpha = graphics.Call("draw_get_alpha", [], context) ?? 1;
                graphics.Call("draw_set_color", [16777215], context); graphics.Call("draw_set_alpha", [1], context);
                graphics.Call("draw_text", [graphics.Camera.X + item.X, graphics.Camera.Y + item.Y, item.Text], context);
                graphics.Call("draw_set_color", [colour], context); graphics.Call("draw_set_alpha", [alpha], context);
            }));
        }
    }
}
