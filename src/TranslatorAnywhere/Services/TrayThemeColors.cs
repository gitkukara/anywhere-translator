using System.Drawing;
using System.Windows.Forms;

namespace TranslatorAnywhere.Services;

/// <summary>Use the same surfaces in the WinForms tray menu and the WPF windows.</summary>
public sealed class TrayThemeColors : ProfessionalColorTable
{
    private readonly Color _surface;
    private readonly Color _hover;
    private readonly Color _border;
    public TrayThemeColors(Color surface, Color hover, Color border)
    {
        _surface = surface; _hover = hover; _border = border;
        UseSystemColors = false;
    }
    public override Color ToolStripDropDownBackground => _surface;
    public override Color ImageMarginGradientBegin => _surface;
    public override Color ImageMarginGradientMiddle => _surface;
    public override Color ImageMarginGradientEnd => _surface;
    public override Color MenuBorder => _border;
    public override Color MenuItemBorder => _hover;
    public override Color MenuItemSelected => _hover;
    public override Color MenuItemSelectedGradientBegin => _hover;
    public override Color MenuItemSelectedGradientEnd => _hover;
    public override Color MenuItemPressedGradientBegin => _hover;
    public override Color MenuItemPressedGradientMiddle => _hover;
    public override Color MenuItemPressedGradientEnd => _hover;
    public override Color SeparatorDark => _border;
    public override Color SeparatorLight => _surface;
}