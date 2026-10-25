using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HbAi.XboxDns;

static class ConnectionMenuTests
{
    static int passed;
    static void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
    [STAThread]static void Main()
    {
        Application.EnableVisualStyles();
        // Compiled with the production DPI-aware manifest. Test the native popup,
        // including its layout after Show, without showing or starting MainForm.
        foreach(float scale in new[]{.5f,.85f,1f,1.25f,1.5f,2f,3f})foreach(bool dark in new[]{true,false}){
            int width=(int)Math.Round(384*scale);float pixels=Math.Max(12,14*scale);
            Color bg=dark?Color.FromArgb(17,17,29):Color.FromArgb(249,248,255),ink=dark?Color.White:Color.FromArgb(20,28,35);
            ConnectionMethod chosen=ConnectionMethod.Windows;
            using(var menu=ConnectionUi.Methods(chosen,false,m=>chosen=m,new Font(FontFamily.GenericSansSerif,pixels,GraphicsUnit.Pixel),bg,ink,Color.DeepSkyBlue,Color.Gray,width)){
                menu.Opacity=0;menu.Show(new Point(-10000,-10000));Application.DoEvents();
                Check(menu.Font.Unit==GraphicsUnit.Pixel&&Math.Abs(menu.Font.Size-pixels)<.01f,"Popup font applies scale once: "+scale+" dark="+dark);
                Check(menu.Width<=width&&menu.Items.Cast<ToolStripItem>().All(i=>i.Bounds.Right<=menu.ClientSize.Width&&i.Bounds.Left>=0),"Native layout stays inside the connection card width: "+scale);
                Check(menu.Items.Cast<ToolStripItem>().All(i=>i.Bounds.Bottom<=menu.ClientSize.Height),"Native popup keeps all choices inside its height: "+scale);
                var items=menu.Items.OfType<ToolStripMenuItem>().ToArray();
                Check(items.Length==3&&items.Count(i=>i.Checked)==1&&items.All(i=>!string.IsNullOrEmpty(i.AccessibleName)&&!string.IsNullOrEmpty(i.AccessibleDescription)),"Choices retain labels, descriptions and selection at "+scale);
                Check(items.Skip(1).Zip(items,(a,b)=>a.Bounds.Top>=b.Bounds.Bottom).All(x=>x),"Wrapped choices never overlap at "+scale);
                foreach(float dpi in new[]{96f,192f,288f})using(var bitmap=new Bitmap(menu.Width,menu.Height)){
                    bitmap.SetResolution(dpi,dpi);menu.DrawToBitmap(bitmap,menu.ClientRectangle);
                    Check(bitmap.GetPixel(menu.Width/2,menu.Height-3).A==255,"Pixel-font popup paints at target DPI "+dpi+" scale="+scale);
                }
                menu.Close();items.Single(i=>(ConnectionMethod)i.Tag==ConnectionMethod.Application).PerformClick();
                Check(chosen==ConnectionMethod.Application,"Wrapped choice keeps its action at "+scale);
            }
            DnsTransport protocol=DnsTransport.Https;
            using(var menu=ConnectionUi.Protocols(protocol,new[]{false,true,true},false,p=>protocol=p,new Font(FontFamily.GenericSansSerif,pixels,GraphicsUnit.Pixel),bg,ink,Color.DeepSkyBlue,Color.Gray,width)){
                menu.Opacity=0;menu.Show(new Point(-10000,-10000));Application.DoEvents();
                Check(menu.Width<=width&&menu.Items.Cast<ToolStripItem>().All(i=>i.Bounds.Right<=width&&i.Bounds.Bottom<=menu.Height),"Advanced encryption popup obeys the same bounds at "+scale);
                var plain=menu.Items.OfType<ToolStripMenuItem>().Single(i=>(DnsTransport)i.Tag==DnsTransport.Plain);plain.PerformClick();
                Check(!plain.Enabled&&protocol==DnsTransport.Https,"Layout change never enables plaintext at "+scale);
                menu.Close();
            }
        }
        using(var menu=ConnectionUi.Methods(ConnectionMethod.Windows,true,m=>{throw new Exception("Locked choice executed");},new Font(FontFamily.GenericSansSerif,14,GraphicsUnit.Pixel),Color.Black,Color.White,Color.Cyan,Color.Gray,300)){
            Check(menu.Items.Count==4&&menu.Items.Cast<ToolStripItem>().All(i=>!i.Enabled),"Locked popup includes a wrapped explanation and no enabled choice");
        }
        Console.WriteLine("All "+passed+" DPI-aware popup checks passed; no DNS workers, settings or network changes.");
    }
}
