using System;
using System.Drawing;
using System.Windows.Forms;

static class Stub
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new Form { Text = "", BackColor = Color.FromArgb(18, 18, 18), Size = new Size(900, 600), StartPosition = FormStartPosition.CenterScreen });
    }
}
