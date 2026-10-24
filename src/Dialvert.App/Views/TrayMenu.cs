using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Dialvert.App.Views;

/// <summary>The tray icon's WinForms menu, painted in the app's graphite palette instead of system white.</summary>
static class TrayMenu
{
    static readonly Drawing.Color Ink = Drawing.Color.FromArgb(0x12, 0x12, 0x10);
    static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x29, 0x28, 0x25);
    static readonly Drawing.Color Stroke = Drawing.Color.FromArgb(0x48, 0x46, 0x3F);
    static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xEF, 0xEC, 0xE4);

    public static Forms.ContextMenuStrip Create()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Renderer(),
            BackColor = Ink,
            ForeColor = Text,
            ShowImageMargin = false,
            Font = new Drawing.Font("Bahnschrift", 10f),
            Padding = new Forms.Padding(2, 4, 2, 4),
        };
        menu.ItemAdded += (_, e) => { e.Item!.ForeColor = Text; e.Item.Padding = new Forms.Padding(4, 3, 18, 3); };
        return menu;
    }

    sealed class Renderer() : Forms.ToolStripProfessionalRenderer(new Colors())
    {
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Stroke);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
        }
    }

    sealed class Colors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Ink;
        public override Drawing.Color MenuBorder => Stroke;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Ink;
        public override Drawing.Color ImageMarginGradientMiddle => Ink;
        public override Drawing.Color ImageMarginGradientEnd => Ink;
    }
}
