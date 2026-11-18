// Ventanitas propias (pregunta sí/no y aviso) con los colores de la web, en vez de las blancas de Windows (desde la 1.1.0), y la
// barra de título oscura para cualquier ventana del programa.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ForeverTracker
{
    public static class Tema
    {
        // Los colores de la web (page.html).
        public static readonly Color Fondo = Color.FromArgb(18, 18, 18), Panel = Color.FromArgb(28, 28, 28), Linea = Color.FromArgb(46, 46, 46),
            Texto = Color.FromArgb(214, 214, 214), Blanco = Color.White, Gris = Color.FromArgb(143, 143, 143), Oro = Color.FromArgb(255, 209, 0),
            Verde = Color.FromArgb(76, 187, 23), Ambar = Color.FromArgb(240, 160, 32), Rojo = Color.FromArgb(224, 80, 58),
            BotonFondo = Color.FromArgb(38, 38, 38), BotonBorde = Color.FromArgb(68, 68, 68), SobreOro = Color.FromArgb(22, 20, 12);
        public const string Letra = "Segoe UI";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Barra de título oscura (Windows 10 desde la 1809; si no se puede, se queda la clara y no pasa nada).
        public static void BarraOscura(Form f)
        {
            try
            {
                int si = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref si, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref si, 4);
            }
            catch { }
        }

        // Centrada en el monitor principal (Windows, por defecto, la centra en el monitor donde está el ratón: decidido el 2026-11-18).
        public static void EnMonitorPrincipal(Form f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Load += (s, e) =>
            {
                var a = Screen.PrimaryScreen.WorkingArea;
                f.Location = new Point(a.Left + (a.Width - f.Width) / 2, a.Top + (a.Height - f.Height) / 2);
            };
        }

        public static Icon IconoPrograma()
        {
            try { return Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { return null; }
        }

        public static Label Etiqueta(string t, Color c, float tam, FontStyle estilo, int anchoMax)
        {
            return new Label { Text = t, ForeColor = c, AutoSize = true, MaximumSize = new Size(anchoMax, 0), Font = new Font(Letra, tam, estilo), Margin = new Padding(0, 2, 0, 2), BackColor = Color.Transparent };
        }

        public static Button Boton(string t, bool principal, EventHandler clic)
        {
            var b = new Button
            {
                Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 8, 8, 0),
                BackColor = principal ? Oro : BotonFondo, ForeColor = principal ? SobreOro : Texto,
                Font = new Font(Letra, 10f, principal ? FontStyle.Bold : FontStyle.Regular), UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = principal ? Oro : BotonBorde;
            b.FlatAppearance.MouseOverBackColor = principal ? Color.FromArgb(255, 224, 80) : Color.FromArgb(50, 50, 50);
            b.Click += clic;
            return b;
        }
    }

    public class Dialogo : Form
    {
        const int Ancho = 440;
        bool si, no, segundo; // «segundo»: el segundo botón es una elección (no un «cancelar»), y se distingue de cerrar la ventana

        // Con «cabecera», la ventanita lleva el icono y el título como la bienvenida, un titular y una nota en gris (la de instalar).
        Dialogo(string texto, string botonSi, string botonNo, bool cabecera = false, string titular = null, string nota = null)
        {
            Text = "Forever Tracker";
            Font = new Font(Tema.Letra, 10f);
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 16, 20, 16);
            Icon = Tema.IconoPrograma();
            // Dock = Fill: así el contenido respeta el margen (Padding) de la ventana; sin él se pega a la esquina.
            var cuerpo = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Tema.Fondo, Dock = DockStyle.Fill };
            int ancho = cabecera ? 520 : Ancho;
            if (cabecera)
            {
                // Con aire: márgenes anchos y espacio entre cada bloque (decidido el 2026-11-18).
                Padding = new Padding(40, 32, 40, 32);
                var cab = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 22), BackColor = Color.Transparent };
                try { if (Icon != null) cab.Controls.Add(new PictureBox { Image = new Icon(Icon, 48, 48).ToBitmap(), Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 2, 12, 0), BackColor = Color.Transparent }); }
                catch { }
                cab.Controls.Add(Tema.Etiqueta("Forever Tracker", Tema.Oro, 19f, FontStyle.Bold, ancho));
                cuerpo.Controls.Add(cab);
                if (titular != null)
                {
                    var t = Tema.Etiqueta(titular, Tema.Blanco, 14.5f, FontStyle.Bold, ancho);
                    t.Margin = new Padding(0, 0, 0, 14);
                    cuerpo.Controls.Add(t);
                }
            }
            var cuerpoTexto = Tema.Etiqueta(texto, Tema.Texto, cabecera ? 11f : 10f, FontStyle.Regular, ancho);
            if (cabecera) cuerpoTexto.Margin = new Padding(0, 0, 0, 30);
            cuerpo.Controls.Add(cuerpoTexto);
            if (nota != null)
            {
                var n = Tema.Etiqueta(nota, Tema.Gris, 9f, FontStyle.Regular, ancho);
                n.Margin = new Padding(0, 6, 0, 2);
                cuerpo.Controls.Add(n);
            }
            // (El margen de abajo de la ventana no cuenta al calcular su alto: se pone como margen de la fila de botones.)
            var fila = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, cabecera ? 30 : 12), BackColor = Color.Transparent };
            var b1 = Tema.Boton(botonSi, true, (s, e) => { si = true; Close(); });
            fila.Controls.Add(b1);
            Button b2 = null;
            if (botonNo != null) fila.Controls.Add(b2 = Tema.Boton(botonNo, false, (s, e) => { no = segundo; Close(); }));
            if (cabecera)
            {
                b1.Padding = new Padding(22, 8, 22, 8); b1.Margin = new Padding(0, 0, 14, 0); b1.Font = new Font(Tema.Letra, 11f, FontStyle.Bold);
                if (b2 != null) { b2.Padding = new Padding(18, 8, 18, 8); b2.Margin = new Padding(0); b2.Font = new Font(Tema.Letra, 11f); }
            }
            cuerpo.Controls.Add(fila);
            Controls.Add(cuerpo);
            AcceptButton = b1;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Tema.BarraOscura(this); }

        // Delante de lo demás al salir (si no, tras el aviso de SmartScreen o desde otro programa se queda detrás; visto el 2026-11-18).
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Owner == null) { TopMost = true; Activate(); TopMost = false; }
        }

        // Pregunta sí/no; con «owner» null, en medio de la pantalla.
        public static bool Pregunta(IWin32Window owner, string texto, string botonSi, string botonNo)
        {
            using (var d = new Dialogo(texto, botonSi, botonNo))
            {
                if (owner == null) Tema.EnMonitorPrincipal(d);
                d.ShowDialog(owner);
                return d.si;
            }
        }

        public static void Avisa(IWin32Window owner, string texto)
        {
            Pregunta(owner, texto, "Vale", null);
        }

        // Elegir entre dos: 1 el primero, 2 el segundo, 0 si se cierra la ventana sin elegir.
        public static int Elige(IWin32Window owner, string texto, string boton1, string boton2)
        {
            using (var d = new Dialogo(texto, boton1, boton2))
            {
                d.segundo = true;
                if (owner == null) Tema.EnMonitorPrincipal(d);
                d.ShowDialog(owner);
                return d.si ? 1 : d.no ? 2 : 0;
            }
        }

        // La de instalar la descarga directa (la primera vez que se abre el .exe). Amable, clara y corta (decidido el 2026-11-18).
        public static bool Instalar()
        {
            using (var d = new Dialogo(
                "A continuación se instalará un pequeño programa que envía los datos de tu addon a la web para mantener tu cuenta al día.",
                "Instalar", "Ahora no", true, "Gracias por usar Forever Tracker"))
            {
                Tema.EnMonitorPrincipal(d);
                d.ShowDialog();
                return d.si;
            }
        }
    }
}
