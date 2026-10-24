using System.Collections.Specialized;

namespace DialvertDragSource;

/// <summary>
/// Test harness: a small window that is an OLE drag source for the files given on the
/// command line (CF_HDROP, exactly what Explorer offers). Lets Dialvert's drag &amp; drop path
/// be exercised with synthetic mouse input.
/// </summary>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Optional first argument "--at=x,y,w,h" places the window (screen pixels).
        var bounds = new Rectangle(200, 200, 360, 220);
        if (args.Length > 0 && args[0].StartsWith("--at="))
        {
            var n = args[0][5..].Split(',').Select(int.Parse).ToArray();
            bounds = new Rectangle(n[0], n[1], n[2], n[3]);
            args = args[1..];
        }
        var form = new Form
        {
            Text = "Dialvert drag source",
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds,
            TopMost = false, // like Explorer: below Dialvert's topmost overlays
        };
        var names = string.Join("\n", args.Select(Path.GetFileName));
        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11),
            BackColor = Color.LightSteelBlue,
            Text = "Drag from here:\n" + names,
        };
        label.MouseDown += (_, _) =>
        {
            var files = new StringCollection();
            files.AddRange(args);
            var data = new DataObject();
            data.SetFileDropList(files);
            var result = label.DoDragDrop(data, DragDropEffects.Copy);
            label.Text = $"Ergebnis: {result}\n{names}";
        };
        form.Controls.Add(label);
        Application.Run(form);
    }
}
