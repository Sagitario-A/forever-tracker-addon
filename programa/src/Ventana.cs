// La ventana del programa (al abrirlo desde el menú Inicio). Hasta que el PC está vinculado, la bienvenida: qué hace el programa y
// «Vincular este PC» (el permiso expreso antes de subir nada, norma 10.5.2 de la Microsoft Store, es vincular: la página de la web
// explica qué se sube y enlaza la política de privacidad antes de «Sí, vincular»). Vinculado, el estado, sin datos del juego (decidido el
// 2026-11-18): una línea de estado («Vinculado», o lo que falle), la cuenta, la última subida (hace X minutos, o la hora si fue hace
// más de una hora), la carpeta de los archivos del juego con «Detectar carpeta» y «Cambiar carpeta», y la versión del addon. Sin «Subir ahora» (se sube solo cuando el juego guarda), sin «Salir», sin registro (decidido el 2026-11-18). Lo que la web
// contesta de cada subida y la lista de encargos escrita van al registro.txt. Colores de la web; arranca siempre con Windows.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ForeverTracker
{
    public class Ventana : Form
    {
        const int Ancho = 540;
        const int Dentro = Ancho - 2 * 14; // lo que cabe dentro de una tarjeta

        readonly Agente ag;
        readonly TableLayoutPanel cuerpo;
        readonly Timer reloj;
        string firma; // lo último pintado: si no cambia nada, no se vuelve a construir (así no parpadea)

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int WM_SETREDRAW = 0x000B;

        public Ventana(Agente agente)
        {
            ag = agente;
            Text = "Forever Tracker";
            Font = new Font(Tema.Letra, 10f);
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Tema.EnMonitorPrincipal(this);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 16, 20, 18);
            ShowInTaskbar = true;
            DoubleBuffered = true;
            Icon = Tema.IconoPrograma();
            cuerpo = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, BackColor = Tema.Fondo };
            Controls.Add(cuerpo);
            ag.Cambio += Pinta;
            reloj = new Timer { Interval = 30000 };
            reloj.Tick += (s, e) => Pinta();
            reloj.Start();
            FormClosed += (s, e) => { ag.Cambio -= Pinta; reloj.Dispose(); };
            Pinta();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Tema.BarraOscura(this); }

        // ───────── piezas ─────────
        static Label Etiqueta(string t, Color c, float tam = 10f, FontStyle estilo = FontStyle.Regular, int ancho = Dentro)
        {
            return Tema.Etiqueta(t, c, tam, estilo, ancho);
        }

        static Button Boton(string t, bool principal, EventHandler clic) { return Tema.Boton(t, principal, clic); }

        static FlowLayoutPanel Fila(params Control[] c)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Dentro, 0), Margin = new Padding(0), BackColor = Color.Transparent };
            f.Controls.AddRange(c.Where(x => x != null).ToArray());
            return f;
        }

        // Un punto de una lista: símbolo en color y el texto al lado.
        static TableLayoutPanel Punto(string simbolo, Color color, string texto)
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 1, 0, 1), BackColor = Color.Transparent };
            t.Controls.Add(Tema.Etiqueta(simbolo, color, 10.5f, FontStyle.Bold, 30));
            t.Controls.Add(Tema.Etiqueta(texto, Tema.Texto, 10f, FontStyle.Regular, Ancho - 40));
            return t;
        }

        // Una tarjeta con título, como las de Ajustes en la web.
        Panel Tarjeta(string titulo, params Control[] dentro)
        {
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Tema.Panel, Padding = new Padding(14, 10, 14, 12), Margin = new Padding(0, 0, 0, 10), Width = Ancho, MinimumSize = new Size(Ancho, 0), MaximumSize = new Size(Ancho, 0) };
            if (titulo != null) t.Controls.Add(Etiqueta(titulo, Tema.Blanco, 11f, FontStyle.Bold));
            foreach (var c in dentro) if (c != null) t.Controls.Add(c);
            t.Paint += (s, e) => { using (var p = new Pen(Tema.Linea)) e.Graphics.DrawRectangle(p, 0, 0, t.Width - 1, t.Height - 1); };
            return t;
        }

        Control Cabecera()
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6), BackColor = Color.Transparent };
            var icono = Tema.IconoPrograma();
            if (icono != null)
            {
                try { f.Controls.Add(new PictureBox { Image = new Icon(icono, 32, 32).ToBitmap(), Size = new Size(28, 28), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 4, 8, 0), BackColor = Color.Transparent }); }
                catch { }
            }
            f.Controls.Add(Etiqueta("Forever Tracker", Tema.Oro, 16f, FontStyle.Bold));
            var v = Etiqueta("versión " + Programa.Version + (ag.Pruebas && ag.A.CapturaCarpeta == null ? " · PRUEBAS (" + ag.W.Base + ")" : ""), Tema.Gris, 9f);
            v.Margin = new Padding(10, 11, 0, 0);
            f.Controls.Add(v);
            return f;
        }

        // ───────── el estado, en palabras ─────────
        string Carpeta { get { return (ag.Pruebas ? ag.A.CapturaCarpeta : null) ?? ag.A.Juego; } }

        // La última subida de cualquier cuenta de WoW (null si no hay ninguna).
        Subida Ultima()
        {
            Subida u = null;
            foreach (var s in ag.A.Subidas.Values) if (u == null || s.Cuando > u.Cuando) u = s;
            return u;
        }

        // «Última subida hace 5 minutos»; de más de una hora, la hora («a las 17:40»); de otro día, con la fecha delante.
        string UltimaSubida()
        {
            var u = Ultima();
            if (u == null) return "todavía sin subidas (se sube solo al salir del juego o con /reload)";
            long min = (Tiempo.Ahora() - u.Cuando) / 60000;
            if (min < 60) return "última subida " + Tiempo.Hace(u.Cuando);
            var cuando = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(u.Cuando).ToLocalTime();
            return "última subida " + (cuando.Date == DateTime.Now.Date ? "" : "el " + cuando.ToString("d/M") + " ") + "a las " + cuando.ToString("HH:mm");
        }

        // Una línea: lo peor que pase ahora mismo; si nada, «Vinculado».
        void Estado(out string texto, out Color color)
        {
            var u = Ultima();
            if (ag.Aviso != null) { texto = ag.Aviso; color = Tema.Rojo; }
            else if (ag.A.Juego == null) { texto = "No encuentro la carpeta de World of Warcraft: Forever. Elígela con «Cambiar carpeta»."; color = Tema.Rojo; }
            else if (ag.MensajeAddon != null) { texto = "El addon no se ha podido instalar."; color = Tema.Ambar; }
            else if (ag.Subiendo) { texto = "Subiendo tus datos del juego…"; color = Tema.Ambar; }
            else if (ag.AddonInstalando) { texto = "Instalando el addon…"; color = Tema.Ambar; }
            else if (u != null && !u.Bien) { texto = "La última subida no valió: " + u.Resumen; color = Tema.Rojo; }
            else { texto = "Vinculado"; color = Tema.Verde; }
        }

        // Todo lo que se enseña, en un texto: si no cambia, no se vuelve a construir la ventana.
        string Firma()
        {
            string estado; Color color;
            if (ag.Vinculado) Estado(out estado, out color); else { estado = ""; color = Color.Empty; }
            return string.Join("|", ag.Vinculado, ag.Vinculando, ag.Codigo, ag.UrlVincular, ag.MensajeVincular, ag.A.Juego, Carpeta, ag.A.Cuenta, estado, color.ToArgb(), ag.Vinculado ? UltimaSubida() : "",
                ag.AddonInstalando, ag.AddonInstalado, ag.AddonUltimo, ag.MensajeAddon, ag.ProgramaNuevo);
        }

        // ───────── pintar ─────────
        void Pinta()
        {
            if (IsDisposed) return;
            string f = Firma();
            if (f == firma) return;
            firma = f;
            // Mientras se cambia todo, Windows no pinta nada a medias (así no parpadea).
            if (IsHandleCreated) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                cuerpo.SuspendLayout();
                foreach (Control c in cuerpo.Controls.Cast<Control>().ToList()) c.Dispose();
                cuerpo.Controls.Clear();
                cuerpo.Controls.Add(Cabecera());
                if (ag.Vinculado) Vinculado();
                else Bienvenida();
                cuerpo.ResumeLayout(true);
            }
            finally
            {
                if (IsHandleCreated) { SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero); Refresh(); }
            }
        }

        // Hasta que el PC está vinculado (siempre, no solo la primera vez: decidido el 2026-11-18).
        void Bienvenida()
        {
            var gracias = Etiqueta("Gracias por descargar Forever Tracker", Tema.Blanco, 13.5f, FontStyle.Bold, Ancho);
            gracias.Margin = new Padding(0, 8, 0, 6);
            cuerpo.Controls.Add(gracias);
            var que = Etiqueta("Qué hace", Tema.Oro, 10.5f, FontStyle.Bold, Ancho);
            que.Margin = new Padding(0, 6, 0, 2);
            cuerpo.Controls.Add(que);
            cuerpo.Controls.Add(Punto("✓", Tema.Oro, "Instala el addon Forever Tracker en tu juego y lo mantiene al día, sin que tengas que hacer nada."));
            cuerpo.Controls.Add(Punto("✓", Tema.Oro, "Sube tus datos del juego a tu cuenta de la web cada vez que el juego los guarda (al salir o con /reload)."));
            if (ag.A.Juego == null)
            {
                var sin = Etiqueta("No encuentro World of Warcraft: Forever en este PC. Elige su carpeta para que pueda instalar el addon.", Tema.Rojo, 10f, FontStyle.Regular, Ancho);
                sin.Margin = new Padding(0, 10, 0, 0);
                cuerpo.Controls.Add(sin);
                cuerpo.Controls.Add(Fila(Boton("Elegir carpeta", false, (s, e) => EligeCarpeta())));
            }
            if (ag.Vinculando)
            {
                var vinc = Etiqueta(ag.Codigo == null ? (ag.MensajeVincular ?? "Preparando…") : "Se ha abierto la web en tu navegador. Comprueba que enseña este mismo código y pulsa «Sí, vincular»:", Tema.Texto, 10f, FontStyle.Regular, Ancho);
                vinc.Margin = new Padding(0, 14, 0, 2);
                cuerpo.Controls.Add(vinc);
                if (ag.Codigo != null)
                {
                    var codigo = Etiqueta(ag.Codigo, Tema.Oro, 28f, FontStyle.Bold, Ancho);
                    codigo.Margin = new Padding(0, 6, 0, 4);
                    cuerpo.Controls.Add(codigo);
                    cuerpo.Controls.Add(Etiqueta("Esperando a que aceptes en la web…", Tema.Gris, 9f, FontStyle.Regular, Ancho));
                }
                cuerpo.Controls.Add(Fila(ag.UrlVincular != null && ag.Codigo != null ? Boton("Abrir otra vez la página", false, (s, e) => Agente.AbreNavegador(ag.UrlVincular)) : null,
                                         Boton("Cancelar", false, (s, e) => ag.CancelaVincular())));
            }
            else
            {
                var para = Etiqueta("Para empezar, vincula este PC a tu cuenta de la web: se abrirá el navegador con un código. Nada se sube hasta que digas que sí en la web.", Tema.Gris, 10f, FontStyle.Regular, Ancho);
                para.Margin = new Padding(0, 14, 0, 2);
                cuerpo.Controls.Add(para);
                if (ag.MensajeVincular != null) cuerpo.Controls.Add(Etiqueta(ag.MensajeVincular, Tema.Rojo, 10f, FontStyle.Regular, Ancho));
                cuerpo.Controls.Add(Fila(Boton("Vincular este PC", true, (s, e) => ag.Vincula())));
            }
            var nota = Etiqueta("Puedes cerrar esta ventana: el programa sigue en segundo plano y arranca con Windows. Vuelve a abrirlo desde el menú Inicio cuando quieras.", Tema.Gris, 9f, FontStyle.Regular, Ancho);
            nota.Margin = new Padding(0, 16, 0, 0);
            cuerpo.Controls.Add(nota);
        }

        // Vinculado: el estado, sin datos del juego.
        void Vinculado()
        {
            string estado; Color color;
            Estado(out estado, out color);
            var linea = Etiqueta("●  " + estado, color, 10f, FontStyle.Bold, Ancho);
            linea.Margin = new Padding(0, 0, 0, 2);
            cuerpo.Controls.Add(linea);
            var cuenta = Etiqueta("Cuenta «" + (ag.A.Cuenta ?? "?") + "» – " + UltimaSubida(), Tema.Texto, 10f, FontStyle.Regular, Ancho);
            cuenta.Margin = new Padding(0, 0, 0, ag.ProgramaNuevo != null ? 2 : 12);
            cuerpo.Controls.Add(cuenta);
            if (ag.ProgramaNuevo != null)
            {
                var nuevo = Etiqueta("Hay una versión nueva del programa (" + ag.ProgramaNuevo + "): se pone sola.", Tema.Ambar, 9f, FontStyle.Regular, Ancho);
                nuevo.Margin = new Padding(0, 0, 0, 12);
                cuerpo.Controls.Add(nuevo);
            }

            // Los archivos del juego: la carpeta (la encuentra el programa), buscarla otra vez o elegirla a mano.
            cuerpo.Controls.Add(Tarjeta("Archivos del juego",
                Etiqueta(Carpeta ?? "No encuentro la carpeta de World of Warcraft: Forever.", Carpeta != null ? Tema.Texto : Tema.Rojo),
                Fila(Boton("Detectar carpeta", false, (s, e) => DetectaCarpeta()), Boton("Cambiar carpeta", false, (s, e) => EligeCarpeta()))));

            // Addon: solo la versión. Se instala y se pone al día solo.
            Control estadoAddon;
            bool reintentar = false;
            if (ag.A.Juego == null) estadoAddon = Etiqueta("Sin carpeta del juego.", Tema.Gris);
            else if (ag.AddonInstalando) estadoAddon = Etiqueta("Instalando el addon" + (ag.AddonUltimo != null ? " " + ag.AddonUltimo : "") + "…", Tema.Ambar);
            else if (ag.AddonInstalado == null)
            {
                estadoAddon = Etiqueta(ag.AddonUltimo == null ? "Todavía no se ha podido comprobar la versión del addon." : "El addon no está instalado.", ag.AddonUltimo == null ? Tema.Gris : Tema.Rojo);
                reintentar = ag.AddonUltimo != null;
            }
            else if (ag.AddonUltimo != null && Versiones.Compara(ag.AddonInstalado, ag.AddonUltimo) < 0)
            {
                estadoAddon = Etiqueta("Tienes la versión " + ag.AddonInstalado + "; se pone la " + ag.AddonUltimo + ".", Tema.Ambar);
                reintentar = ag.MensajeAddon != null;
            }
            else estadoAddon = Etiqueta("✓ Versión " + ag.AddonInstalado + (ag.AddonUltimo != null ? ", la última." : ".") + " Se mantiene al día solo.", Tema.Verde);
            cuerpo.Controls.Add(Tarjeta("Addon",
                estadoAddon,
                ag.MensajeAddon != null ? Etiqueta(ag.MensajeAddon, Tema.Rojo, 9f) : null,
                reintentar ? Fila(Boton("Volver a intentar", false, (s, e) => ag.ReintentaAddon())) : null));
        }

        void DetectaCarpeta()
        {
            if (!ag.DetectaJuego())
                Dialogo.Avisa(this, "No encuentro World of Warcraft: Forever en este PC (lo busco en Battle.net, en Windows y en las carpetas habituales). Elígela con «Cambiar carpeta».");
        }

        void EligeCarpeta()
        {
            using (var d = new FolderBrowserDialog { Description = "Elige la carpeta de World of Warcraft (o la de _forever_ dentro de ella).", ShowNewFolderButton = false })
            {
                if (ag.A.Juego != null) d.SelectedPath = ag.A.Juego;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!ag.CambiaJuego(d.SelectedPath))
                    Dialogo.Avisa(this, "En esa carpeta no está World of Warcraft: Forever (la carpeta _forever_).");
            }
        }

        // Pruebas: la ventana como imagen.
        public void Captura(string png)
        {
            PerformLayout();
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(png, ImageFormat.Png);
            }
        }
    }
}
