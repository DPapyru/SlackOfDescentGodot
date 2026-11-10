using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private enum EditorInputContext { Canvas, Text }
    private enum KeyMode { Default, Vim }
    private EditorInputContext inputContext = EditorInputContext.Canvas;
    private KeyMode keyMode;
    private bool vimNormal = true;
    private bool vimDeletePending;
    private string KeyModePath => Path.Combine(store.Root, "Editor", "action-editor-keys.json");
    private void LoadKeyMode()
    {
        try { if (File.Exists(KeyModePath)) keyMode = NativeContentStore.Load<string>(KeyModePath) == "vim" ? KeyMode.Vim : KeyMode.Default; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { keyMode = KeyMode.Default; }
    }
    private void SaveKeyMode()
    {
        try { NativeContentStore.Save(KeyModePath, keyMode == KeyMode.Vim ? "vim" : "default"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = error.GetBaseException().Message; }
    }
    private void SetKeyMode(KeyMode mode)
    {
        keyMode = mode; vimNormal = true; vimDeletePending = false; SaveKeyMode();
        status.Text = mode == KeyMode.Vim ? "键位模式：vim（Esc 普通模式 / i 插入模式）。" : "键位模式：默认。";
    }
    private void TrackFocus(Control focus) => inputContext = IsTextControl(focus) ? EditorInputContext.Text : EditorInputContext.Canvas;
    private static bool IsTextControl(Control focus) => focus is LineEdit or TextEdit or CodeEdit or SpinBox;
    private void TakeCanvasFocus()
    {
        inputContext = EditorInputContext.Canvas;
        if (GetViewport().GuiGetFocusOwner() is not null) GetViewport().GuiReleaseFocus();
    }
    private bool EditorShortcutsEnabled()
    {
        if (inputContext == EditorInputContext.Text && GetViewport().GuiGetFocusOwner() is null) inputContext = EditorInputContext.Canvas;
        return inputContext == EditorInputContext.Canvas;
    }
    private bool HandleEditorKey(InputEventKey key)
    {
        if (!EditorShortcutsEnabled()) return false;
        if (key.Keycode == Key.Escape)
        {
            if (dragging) { CancelDrag(); return true; }
            if (keyMode == KeyMode.Vim && !vimNormal) { vimNormal = true; status.Text = "vim：普通模式。"; return true; }
            return false;
        }
        if (key.Keycode == Key.F) { FitView(); return true; }
        if (key.CtrlPressed && key.Keycode == Key.Z) { RunAction(key.ShiftPressed ? "redo" : "undo"); return true; }
        if (key.CtrlPressed && key.Keycode == Key.S) { Save(); return true; }
        if (key.CtrlPressed && key.Keycode == Key.Space) { ShowCompletion(); return true; }
        return keyMode == KeyMode.Vim && HandleVimKey(key);
    }
    private bool HandleVimKey(InputEventKey key)
    {
        if (!vimNormal) return false;
        if (key.Keycode != Key.D) vimDeletePending = false;
        switch (key.Keycode)
        {
            case Key.I: vimNormal = false; status.Text = "vim：插入模式。"; return true;
            case Key.H: StepFrame(-1); return true;
            case Key.L: StepFrame(1); return true;
            case Key.J: StepBone(1); return true;
            case Key.K: StepBone(-1); return true;
            case Key.U: RunAction("undo"); return true;
            case Key.D:
                if (vimDeletePending) { vimDeletePending = false; DeleteCurrentKey(); }
                else { vimDeletePending = true; status.Text = "dd：再按一次 D 删除当前帧关键帧。"; }
                return true;
            default: return false;
        }
    }
    private void StepFrame(double delta) => frame.Value = Math.Clamp(frame.Value + delta, frame.MinValue, frame.MaxValue);
    private void StepBone(int delta)
    {
        if (boneList.ItemCount == 0) return;
        int index = SelectedIndex(boneList);
        index = index < 0 ? (delta > 0 ? 0 : boneList.ItemCount - 1) : Math.Clamp(index + delta, 0, boneList.ItemCount - 1);
        boneList.Select(index);
        SelectBone(boneList.GetItemText(index));
    }
    private void DeleteCurrentKey()
    {
        if (!rootTrack.ButtonPressed && SelectedBone is null) { status.Text = "dd：请先选择骨骼或姿态根偏移。"; return; }
        var keys = rootTrack.ButtonPressed || SelectedBone == "root" ? Clip.Root : Clip.Tracks.FirstOrDefault(track => track.Bone == SelectedBone)?.Keys;
        if (keys is null || !keys.Any(item => item.Frame == frame.Value)) { status.Text = "dd：当前帧没有关键帧。"; return; }
        Change(_ => Keys().RemoveAll(item => item.Frame == frame.Value));
    }
    private void ShowCompletion()
    {
        ShowMessage("TOML 帧块补全：\n[[frame]]\nindex = 帧号\n骨骼.字段 = 数值\n字段：x、y、angle、scaleX、scaleY、picture\n骨骼：" + string.Join("、", MotionSkeleton.Bones) + "\n片段：" + string.Join("、", document.Graph.Clips.Select(clip => clip.Id)));
    }
}
