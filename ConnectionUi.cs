using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    enum ConnectionMethod { Windows, Application, Google }
    // Presentation only. Choosing a connection method never starts a DNS worker.
    static class ConnectionUi
    {
        internal static string Name(ConnectionMethod method)
        {
            switch(method){case ConnectionMethod.Windows:return "Способ подключения 1 · Через Windows";case ConnectionMethod.Application:return "Способ подключения 2 · Через приложение";case ConnectionMethod.Google:return "Способ подключения 3 · Gemini / NotebookLM · beta";default:throw new ArgumentOutOfRangeException("method");}
        }
        internal static string Hint(ConnectionMethod method)
        {
            switch(method){case ConnectionMethod.Windows:return "Настройки DNS сохраняются после выхода";case ConnectionMethod.Application:return "Шифрует DNS · работает, пока приложение открыто";case ConnectionMethod.Google:return "Экспериментальный DNS-адаптер · DNS всего ПК";default:throw new ArgumentOutOfRangeException("method");}
        }
        internal static string CompactName(ConnectionMethod method)
        {switch(method){case ConnectionMethod.Windows:return "1 · Через Windows";case ConnectionMethod.Application:return "2 · Через приложение";case ConnectionMethod.Google:return "3 · Gemini / NotebookLM";default:throw new ArgumentOutOfRangeException("method");}}
        internal static string ProtocolName(DnsTransport protocol)
        {return protocol==DnsTransport.Https?"DoH · HTTPS":protocol==DnsTransport.Tls?"DoT · TLS":"Без шифрования";}
        static ContextMenuStrip Menu(Font font,Color surface,Color ink,Color accent,Color border,int width)
        {
            // Geometry is already in screen pixels. Point fonts multiply it by the
            // desktop DPI again, so both drawing and measurement use pixel fonts.
            if(font.Unit!=GraphicsUnit.Pixel){var pixels=new Font(font.FontFamily,font.SizeInPoints*96f/72f,GraphicsUnit.Pixel);font.Dispose();font=pixels;}
            var menu=new ContextMenuStrip{AutoSize=false,Width=Math.Max(160,width),Padding=new Padding(2),ShowImageMargin=false,ShowCheckMargin=false,BackColor=surface,ForeColor=ink,Font=font,Renderer=new ToolStripProfessionalRenderer(new MenuColors(surface,accent,border))};
            // The owning form releases the menu after it has finished closing.
            menu.Disposed+=(s,e)=>font.Dispose();return menu;
        }
        static ContextMenuStrip Finish(ContextMenuStrip menu)
        {menu.Height=menu.Padding.Vertical+menu.Items.Cast<ToolStripItem>().Sum(i=>i.Height);return menu;}
        static void Choice(ContextMenuStrip menu,object value,string title,string hint,bool selected,bool enabled,Action choose)
        {
            var item=new ChoiceItem(menu,title,hint){Tag=value,Checked=selected,Enabled=enabled};
            item.Click+=(s,e)=>choose();menu.Items.Add(item);
        }
        static void Locked(ContextMenuStrip menu,string hint)
        {menu.Items.Add(new ChoiceItem(menu,hint,""){Enabled=false});}
        internal static ContextMenuStrip Methods(ConnectionMethod selected,bool locked,Action<ConnectionMethod> choose,Font font,Color surface,Color ink,Color accent,Color border,int width=360)
        {
            var menu=Menu(font,surface,ink,accent,border,width);
            if(locked)Locked(menu,"Для смены сначала выключите DNS");
            foreach(ConnectionMethod method in Enum.GetValues(typeof(ConnectionMethod))){var value=method;Choice(menu,value,Name(value),Hint(value),value==selected,!locked,()=>choose(value));}
            return Finish(menu);
        }
        internal static ContextMenuStrip Protocols(DnsTransport selected,bool[] available,bool locked,Action<DnsTransport> choose,Font font,Color surface,Color ink,Color accent,Color border,int width=360,Func<DnsTransport,string> unavailableHint=null)
        {
            var menu=Menu(font,surface,ink,accent,border,width);
            if(locked)Locked(menu,"Сначала выключите DNS для изменения");
            foreach(var value in new[]{DnsTransport.Https,DnsTransport.Tls,DnsTransport.Plain}){
                var protocol=value;string hint=value==DnsTransport.Https?"DNS через HTTPS · порт 443. Рекомендуемый вариант.":value==DnsTransport.Tls?"DNS через TLS · порт 853. Попробуйте, если DoH недоступен.":"Открытый DNS · порт 53. Только способ 1 · через Windows.";
                if(!available[(int)value])hint=unavailableHint==null?"Недоступно для выбранного способа или DNS":unavailableHint(value);
                Choice(menu,value,ProtocolName(value),hint,value==selected,!locked&&available[(int)value],()=>choose(protocol));
            }
            return Finish(menu);
        }
        sealed class ChoiceItem : ToolStripMenuItem
        {
            readonly string title,hint;readonly Font titleFont,hintFont;readonly Color surface,ink,accent,muted;
            readonly int inset,textLeft,textWidth,titleHeight,hintHeight;
            internal ChoiceItem(ContextMenuStrip menu,string title,string hint):base(title+(hint.Length>0?"\n"+hint:""))
            {
                this.title=title;this.hint=hint;titleFont=menu.Font;hintFont=new Font(menu.Font.FontFamily,menu.Font.Size*.9f,GraphicsUnit.Pixel);
                surface=menu.BackColor;ink=menu.ForeColor;accent=((MenuColors)((ToolStripProfessionalRenderer)menu.Renderer).ColorTable).Accent;
                muted=Color.FromArgb((ink.R+surface.R)/2,(ink.G+surface.G)/2,(ink.B+surface.B)/2);
                inset=Math.Max(7,(int)Math.Ceiling(titleFont.Size*.55f));textLeft=inset*3+4;textWidth=menu.Width-4-textLeft-inset;
                using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap))using(var format=Format()){
                    titleHeight=(int)Math.Ceiling(g.MeasureString(title,titleFont,textWidth,format).Height);
                    hintHeight=hint.Length==0?0:(int)Math.Ceiling(g.MeasureString(hint,hintFont,textWidth,format).Height);
                }
                AutoSize=false;Size=new Size(menu.Width-4,inset*2+titleHeight+(hintHeight==0?0:hintHeight+inset/2));
                Margin=Padding.Empty;Padding=Padding.Empty;ShowShortcutKeys=false;AccessibleName=title;AccessibleDescription=hint;
            }
            static StringFormat Format(){return new StringFormat{Trimming=StringTrimming.None};}
            protected override void OnPaint(PaintEventArgs e)
            {
                var g=e.Graphics;Color background=Enabled&&Selected?Color.FromArgb((surface.R*4+accent.R)/5,(surface.G*4+accent.G)/5,(surface.B*4+accent.B)/5):surface;
                using(var brush=new SolidBrush(background))g.FillRectangle(brush,new Rectangle(Point.Empty,Size));
                using(var format=Format())using(var text=new SolidBrush(Enabled?ink:muted))using(var description=new SolidBrush(muted)){
                    g.DrawString(title,titleFont,text,new RectangleF(textLeft,inset,textWidth,titleHeight+1),format);
                    if(hintHeight>0)g.DrawString(hint,hintFont,description,new RectangleF(textLeft,inset+titleHeight+inset/2,textWidth,hintHeight+1),format);
                }
                if(Checked)using(var pen=new Pen(Enabled?accent:muted,Math.Max(1.5f,titleFont.Size/8f))){float x=inset,y=Height/2f,s=inset;g.DrawLines(pen,new[]{new PointF(x,y),new PointF(x+s*.4f,y+s*.4f),new PointF(x+s*1.2f,y-s*.6f)});}
            }
            protected override void Dispose(bool disposing){if(disposing)hintFont.Dispose();base.Dispose(disposing);}
        }
        sealed class MenuColors : ProfessionalColorTable
        {
            readonly Color surface,accent,border;
            internal Color Accent {get{return accent;}}
            internal MenuColors(Color surface,Color accent,Color border){this.surface=surface;this.accent=accent;this.border=border;UseSystemColors=false;}
            public override Color ToolStripDropDownBackground{get{return surface;}}
            public override Color MenuBorder{get{return border;}}
            public override Color MenuItemSelected{get{return Color.FromArgb((surface.R*4+accent.R)/5,(surface.G*4+accent.G)/5,(surface.B*4+accent.B)/5);}}
            public override Color MenuItemBorder{get{return accent;}}
            public override Color ImageMarginGradientBegin{get{return surface;}}
            public override Color ImageMarginGradientMiddle{get{return surface;}}
            public override Color ImageMarginGradientEnd{get{return surface;}}
            public override Color CheckBackground{get{return MenuItemSelected;}}
            public override Color CheckSelectedBackground{get{return MenuItemSelected;}}
        }
    }
}
