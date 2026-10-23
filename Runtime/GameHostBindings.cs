using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private static Key GodotKey(int code)=>code switch
    {
        8=>Key.Backspace,9=>Key.Tab,13=>Key.Enter,16=>Key.Shift,17=>Key.Ctrl,18=>Key.Alt,27=>Key.Escape,32=>Key.Space,
        33=>Key.Pageup,34=>Key.Pagedown,35=>Key.End,36=>Key.Home,37=>Key.Left,38=>Key.Up,39=>Key.Right,40=>Key.Down,45=>Key.Insert,46=>Key.Delete,
        >=112 and <=123=>Key.F1+(code-112),_=>(Key)code
    };
    private void SyncGrenadeBinding()
    {
        var title=world.Select(world.Constants["objTitle"].Int,new(new GmInstance())).FirstOrDefault();
        bool visible=title is not null&&title.Scope.Values.ContainsKey("VIEW_KEYCONFIG")&&title.Scope.ReadOr("View").Equals(title.Scope.Read("VIEW_KEYCONFIG"))&&!title.Scope.ReadOr("KeyWaiting").Truth;
        if(grenadeBinding is null)
        {
            grenadeBinding=new Button{Name="GrenadeBinding",TooltipText="手榴弹／次要射击"};
            grenadeBinding.Pressed+=OpenGrenadeBinding;AddChild(grenadeBinding);
        }
        grenadeBinding.Visible=visible;
        grenadeBinding.Text="手榴弹："+OS.GetKeycodeString(GodotKey(controls.SecondaryKey));
        grenadeBinding.Position=new Vector2((float)GetViewportRect().Size.X-180,4);
        grenadeBinding.Size=new Vector2(172,28);
    }
    private void OpenGrenadeBinding()
    {
        if(bindingDialog is not null)return;
        bindingDialog=new ConfirmationDialog{Name="GrenadeKeyDialog",Title="手榴弹／次要射击",DialogText="请按一个键",MinSize=new Vector2I(300,100)};
        bindingDialog.GetOkButton().Hide();
        bindingDialog.WindowInput+=CaptureGrenadeBinding;
        bindingDialog.Canceled+=()=>bindingDialog?.Hide();
        bindingDialog.CloseRequested+=()=>bindingDialog?.Hide();
        AddChild(bindingDialog);
        bindingDialog.VisibilityChanged+=()=>
        {
            if(bindingDialog is null||bindingDialog.Visible)return;
            var closed=bindingDialog;bindingDialog=null;modalDepth--;controls.Clear(world);elapsed=0;
            closed.QueueFree();
        };
        modalDepth++;controls.Clear(world);bindingDialog.PopupCentered();
    }
    private void CaptureGrenadeBinding(InputEvent input)
    {
        if(bindingDialog is null||input is not InputEventKey{Echo:false,Pressed:true} binding)return;
        int code=VirtualKey(binding.Keycode);
        if(binding.Keycode!=Key.Escape)
        {
            if(code is <=1 or >=256)return;
            controls.SaveBinding(world,code);
        }
        bindingDialog.Hide();
    }
}
