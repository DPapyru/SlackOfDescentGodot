using Godot;

namespace SlackOfDescent;

public static class NativeEditorTheme
{
    public static Theme Create()
    {
        var theme = new Theme { DefaultFontSize = 12 };
        StyleBoxFlat Box(string background, string border = "3e3e3e", int padding = 6) => new()
        {
            BgColor = new Color(background), BorderColor = new Color(border),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = 3, ContentMarginBottom = 3
        };
        foreach (string type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetStylebox("normal", type, Box("2b2b2b")); theme.SetStylebox("hover", type, Box("454545"));
            theme.SetStylebox("pressed", type, Box("263e62", "2d7ff9")); theme.SetStylebox("disabled", type, Box("292929"));
            theme.SetColor("font_color", type, new Color("d6d6d6")); theme.SetColor("font_disabled_color", type, new Color("6f6f6f"));
            theme.SetConstant("outline_size", type, 0);
        }
        foreach (string type in new[] { "LineEdit", "TextEdit", "ItemList", "Tree" })
        {
            theme.SetStylebox("normal", type, Box("262626")); theme.SetStylebox("panel", type, Box("2b2b2b"));
            theme.SetStylebox("focus", type, Box("262626", "2d7ff9")); theme.SetStylebox("selected", type, Box("29456d"));
            theme.SetColor("font_color", type, new Color("d6d6d6"));
        }
        theme.SetStylebox("panel", "PanelContainer", Box("323232", "191919"));
        theme.SetStylebox("panel", "PopupMenu", Box("3a3a3a", "191919"));
        theme.SetStylebox("hover", "PopupMenu", Box("2d7ff9"));
        theme.SetStylebox("panel", "TabContainer", Box("323232", "191919", 0));
        theme.SetStylebox("tab_selected", "TabContainer", Box("323232"));
        theme.SetStylebox("tab_unselected", "TabContainer", Box("262626"));
        theme.SetColor("font_color", "Label", new Color("d6d6d6"));
        foreach (string type in new[] { "HBoxContainer", "VBoxContainer" }) theme.SetConstant("separation", type, 4);
        return theme;
    }
}
