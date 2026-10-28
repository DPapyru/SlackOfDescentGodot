namespace SlackOfDescent;

public partial class GameHost
{
    private void OpenModMenuEntry(int entry)
    {
        switch(entry)
        {
            case 0:OpenMapEditor();break;
            case 1:OpenCharacterEditor();break;
            case 2:OpenMods();break;
            case 3:OpenActionEditor();break;
        }
    }
    private static string MenuLanguage(bool change)
    {
        if(change)NativeLocalization.SetLanguage(NativeLocalization.IsChinese?"en":"zh_CN");
        return NativeLocalization.IsChinese?"简体中文":"English";
    }
}
