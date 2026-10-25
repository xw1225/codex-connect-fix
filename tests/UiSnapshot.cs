using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using RemoteHelper;
public static class UiSnapshot {
    private static IEnumerable<Control> Descendants(Control parent) { foreach (Control c in parent.Controls) { yield return c; foreach (Control nested in Descendants(c)) yield return nested; } }
    private static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    private static void Verify(Control parent) {
        var children=parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
        foreach (Control control in children) {
            Check(!control.Text.Contains("非官方") && !control.Text.Contains("非 OpenAI"),"unwanted label remains");
            if (control is Label) Check(control.Height >= control.Font.Height,"clipped label height: " + control.Name);
            if (control is Button) {
                int textWidth=TextRenderer.MeasureText(control.Text,control.Font).Width;
                Check(control.Width >= textWidth+8 && control.Height >= control.Font.Height+8,"clipped button: " + control.Name);
            }
            if (parent.Name == "remoteCard" || parent.Name == "reconnectCard") Check(parent.ClientRectangle.Contains(control.Bounds),"card child outside parent: " + control.Name);
            foreach (Control other in children.Where(c => c != control)) Check(!control.Bounds.IntersectsWith(other.Bounds),"overlap: " + control.Name + " / " + other.Name);
            Verify(control);
        }
    }
    [STAThread] public static int Main(string[] args) {
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.GetType().Name + ": " + e.Message); return 1; }
    }
    private static int Run(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        bool live=args.Length > 1 && args[1] == "live";
        using (var form=new MainWindow(live)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-2400,-2400);
            if (args.Length > 1 && args[1].StartsWith("narrow")) form.Size=form.MinimumSize;
            form.Show(); Application.DoEvents();
            var controls=Descendants(form).ToArray();
            if (live) {
                var refresh=controls.Single(c => c.Name == "refresh"); var deadline=DateTime.UtcNow.AddSeconds(60);
                while (!refresh.Enabled) { Check(DateTime.UtcNow < deadline,"live diagnostics timed out"); Application.DoEvents(); System.Threading.Thread.Sleep(25); }
            }
            Check(controls.Single(c => c.Name == "remoteHeading").Text == "解决无法添加远程控制设备问题","first section missing");
            Check(controls.Single(c => c.Name == "retryHeading").Text == "解决每次使用codex重新连接5次问题","second section missing");
            Check(controls.Where(c => c.Name == "remoteCard" || c.Name == "reconnectCard").All(c => c.Controls.OfType<Button>().Count() == 2),"each card must have two action buttons");
            Check(!controls.Single(c => c.Name == "proxyPort").Visible && !controls.Single(c => c.Name == "diagnosticDetails").Visible,"advanced fields should start collapsed");
            if (args.Length > 1 && args[1].Contains("expanded")) {
                foreach (string name in new[] {"proxySettings","detailsToggle"}) {
                    var link=(LinkLabel)controls.Single(c => c.Name == name);
                    typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(link,new object[] {new LinkLabelLinkClickedEventArgs(link.Links[0])});
                }
                Application.DoEvents();
                var viewport=(Panel)controls.Single(c => c.Name == "viewport");
                var detail=(TextBox)controls.Single(c => c.Name == "diagnosticDetails");
                Check(viewport.VerticalScroll.Visible,"expanded small window must offer a scrollbar");
                viewport.ScrollControlIntoView(detail); Application.DoEvents();
                Check(viewport.ClientRectangle.IntersectsWith(detail.Bounds),"expanded diagnostics cannot be reached by scrolling");
                Check(detail.ReadOnly,"diagnostics should not look like editable input");
            }
            Verify(form.Controls[0]);
            using (var image=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.GetFullPath(args[0])); }
            using (var graphics=form.CreateGraphics()) Console.WriteLine("PASS real DPI " + graphics.DpiX + ": no overlaps/clipped buttons; " + (args.Length > 1 ? args[1] : "default") + " layout");
            form.Close();
        }
        return 0;
    }
}
