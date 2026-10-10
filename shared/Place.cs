using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Hub
{
    static class Place
    {
        static double ScaleOf(Screen s) => Native.Scale(s.Bounds);

        static Rectangle Div(Rectangle r, double k) => new Rectangle((int)Math.Round(r.X / k), (int)Math.Round(r.Y / k), (int)Math.Round(r.Width / k), (int)Math.Round(r.Height / k));

        public static double At(Point dip)
        {
            foreach (var s in Screen.AllScreens)
            {
                var k = ScaleOf(s);
                if (Div(s.Bounds, k).Contains(dip)) return k;
            }
            return ScaleOf(Screen.PrimaryScreen);
        }

        public static bool Seen(Rectangle dip) => Screen.AllScreens.Any(s =>
        {
            var wa = Div(s.WorkingArea, ScaleOf(s));
            return dip.X < wa.X + wa.Width && dip.X + dip.Width > wa.X && dip.Y < wa.Y + wa.Height && dip.Y + dip.Height > wa.Y;
        });

        public static object Read(string file)
        {
            try
            {
                return J.Parse(File.ReadAllText(file));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Rectangle Physical(Rectangle dip)
        {
            var k = At(dip.Location);
            return new Rectangle((int)Math.Round(dip.X * k), (int)Math.Round(dip.Y * k), (int)Math.Round(dip.Width * k), (int)Math.Round(dip.Height * k));
        }

        public static Rectangle Dip(Form f)
        {
            var b = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
            return Div(b, Native.Scale(b));
        }

        public static Rectangle Centered(int width, int height)
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var k = ScaleOf(Screen.PrimaryScreen);
            var w = (int)Math.Round(width * k);
            var h = (int)Math.Round(height * k);
            return new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
        }
    }
}
