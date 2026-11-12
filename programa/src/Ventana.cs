// Ventana de estado (al abrir el programa desde el menú Inicio): cuenta vinculada, carpeta del juego, últimas subidas y
// fallos, el addon; «Subir ahora», «Cambiar carpeta», «Desvincular». Arranca siempre con Windows (sin casilla). Colores de la web.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ForeverTracker
{
    public class Ventana : Form
    {
        static readonly Color Fondo = Color.FromArgb(18, 18, 18), Panel_ = Color.FromArgb(28, 28, 28), Linea = Color.FromArgb(46, 46, 46),
            Texto = Color.FromArgb(214, 214, 214), Blanco = Color.White, Gris = Color.FromArgb(143, 143, 143), Oro = Color.FromArgb(255, 209, 0),
            Verde = Color.FromArgb(110, 200, 90), Rojo = Color.FromArgb(255, 107, 107);
        const int Ancho = 500;
        // Qué se sube al vincular (el permiso se da en la web, con «Sí, vincular»; ahí está también la política de privacidad).
        const string QueSeSube = "Al vincular, este PC subirá a tu cuenta de la web, cada vez que el juego lo guarde, el archivo del addon: lo que lee de tus personajes y de los jugadores que ves. Nunca el chat, tu cuenta de WoW ni nada más de tu PC. En la web eliges quién ve cada cosa.";

        readonly Agente ag;
        readonly Action salirPrograma;
        readonly TableLayoutPanel cuerpo;
        readonly Timer reloj;

        public Ventana(Agente agente, Action salir)
        {
            ag = agente;
            salirPrograma = salir;
            Text = "Forever Tracker";
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Fondo;
            ForeColor = Texto;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16, 12, 16, 14);
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }
            cuerpo = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, BackColor = Fondo };
            Controls.Add(cuerpo);
            ag.Cambio += Pinta;
            ag.Pregunta = q => MessageBox.Show(this, q, "Forever Tracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            reloj = new Timer { Interval = 30000 };
            reloj.Tick += (s, e) => Pinta();
            reloj.Start();
            FormClosed += (s, e) => { ag.Cambio -= Pinta; ag.Pregunta = null; reloj.Dispose(); };
            Pinta();
        }

        // ───────── piezas ─────────
        static Label Etiqueta(string t, Color c, float tam = 9.75f, FontStyle estilo = FontStyle.Regular)
        {
            return new Label { Text = t, ForeColor = c, AutoSize = true, MaximumSize = new Size(Ancho - 28, 0), Font = new Font("Segoe UI", tam, estilo), Margin = new Padding(0, 2, 0, 2), BackColor = Color.Transparent };
        }

        static Button Boton(string t, bool principal, EventHandler clic)
        {
            var b = new Button
            {
                Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 6, 8, 0),
                BackColor = principal ? Oro : Color.FromArgb(38, 38, 38), ForeColor = principal ? Color.FromArgb(22, 20, 12) : Texto,
                Font = new Font("Segoe UI", 9.75f, principal ? FontStyle.Bold : FontStyle.Regular),
            };
            b.FlatAppearance.BorderColor = principal ? Oro : Color.FromArgb(68, 68, 68);
            b.Click += clic;
            return b;
        }

        static FlowLayoutPanel Fila(params Control[] c)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ancho - 28, 0), Margin = new Padding(0), BackColor = Color.Transparent };
            f.Controls.AddRange(c.Where(x => x != null).ToArray());
            return f;
        }

        // Una tarjeta con título, como las de Ajustes en la web.
        Panel Tarjeta(string titulo, params Control[] dentro)
        {
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Panel_, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0, 0, 0, 10), Width = Ancho, MinimumSize = new Size(Ancho, 0), MaximumSize = new Size(Ancho, 0) };
            t.Controls.Add(Etiqueta(titulo, Blanco, 10.5f, FontStyle.Bold));
            foreach (var c in dentro) if (c != null) t.Controls.Add(c);
            t.Paint += (s, e) => { using (var p = new Pen(Linea)) e.Graphics.DrawRectangle(p, 0, 0, t.Width - 1, t.Height - 1); };
            return t;
        }

        // ───────── pintar el estado ─────────
        void Pinta()
        {
            if (IsDisposed) return;
            cuerpo.SuspendLayout();
            foreach (Control c in cuerpo.Controls.Cast<Control>().ToList()) c.Dispose();
            cuerpo.Controls.Clear();

            var cab = Fila(Etiqueta("Forever Tracker", Oro, 15f, FontStyle.Bold), Etiqueta("  versión " + Programa.Version + (ag.Pruebas ? " · PRUEBAS (" + ag.W.Base + ")" : ""), Gris, 9f));
            cab.Margin = new Padding(0, 0, 0, 8);
            cuerpo.Controls.Add(cab);

            // Cuenta
            if (ag.Vinculado)
                cuerpo.Controls.Add(Tarjeta("Cuenta",
                    Etiqueta("✓ Vinculado a la cuenta «" + (ag.A.Cuenta ?? "?") + "» de la web. Este PC: " + Environment.MachineName + ".", Verde),
                    Fila(Boton("Abrir la web", false, (s, e) => Agente.AbreNavegador(ag.W.Base)),
                         Boton("Desvincular", false, async (s, e) =>
                         {
                             if (MessageBox.Show(this, "¿Desvincular este PC de tu cuenta? Dejará de subir tus datos del juego hasta que lo vuelvas a vincular.", "Forever Tracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                                 await ag.Desvincula();
                         }))));
            else if (ag.Vinculando)
            {
                var codigo = Etiqueta(ag.Codigo ?? "…", Oro, 26f, FontStyle.Bold);
                codigo.Margin = new Padding(0, 6, 0, 6);
                cuerpo.Controls.Add(Tarjeta("Vincular este PC",
                    Etiqueta(ag.Codigo == null ? (ag.MensajeVincular ?? "Preparando…") : "Se ha abierto la web en tu navegador. Comprueba que enseña este mismo código y pulsa «Sí, vincular»:", Texto),
                    ag.Codigo != null ? codigo : null,
                    ag.Codigo != null ? Etiqueta("Esperando a que aceptes en la web…", Gris, 9f) : null,
                    Etiqueta(QueSeSube, Gris, 9f),
                    Fila(ag.UrlVincular != null && ag.Codigo != null ? Boton("Abrir otra vez la página", false, (s, e) => Agente.AbreNavegador(ag.UrlVincular)) : null,
                         Boton("Cancelar", false, (s, e) => ag.CancelaVincular()))));
            }
            else
                cuerpo.Controls.Add(Tarjeta("Cuenta",
                    Etiqueta("Este PC no está vinculado a tu cuenta de la web. Vincúlalo para que suba tus datos del juego solo.", Texto),
                    Etiqueta(QueSeSube, Gris, 9f),
                    ag.MensajeVincular != null ? Etiqueta(ag.MensajeVincular, Rojo) : null,
                    Fila(Boton("Vincular este PC", true, (s, e) => ag.Vincula()),
                         Boton("Política de privacidad", false, (s, e) => Agente.AbreNavegador(ag.W.Base + "/privacidad")))));

            // Juego y subidas
            var subidas = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 4, 0, 0), BackColor = Color.Transparent };
            var archivos = ag.A.Juego == null ? new System.Collections.Generic.List<string>() : Juego.ArchivosAddon(ag.A.Juego);
            foreach (string f in archivos)
            {
                Subida s;
                ag.A.Subidas.TryGetValue(f, out s);
                subidas.Controls.Add(Etiqueta("Cuenta de WoW " + Juego.NombreCuenta(f) + ": " + (s == null ? "sin subir todavía." : "subido " + Tiempo.Hace(s.Cuando) + "."), Blanco));
                if (s != null) subidas.Controls.Add(Etiqueta(s.Resumen, s.Bien ? Gris : Rojo, 9f));
            }
            if (archivos.Count == 0 && ag.A.Juego != null)
                subidas.Controls.Add(Etiqueta("Todavía no hay datos del addon. Entra en el juego con el addon puesto y sal del juego (o escribe /reload).", Gris));
            cuerpo.Controls.Add(Tarjeta("Juego y subidas",
                Etiqueta(ag.A.Juego ?? "No encuentro la carpeta de World of Warcraft: Forever.", ag.A.Juego != null ? Gris : Rojo, 9f),
                subidas,
                ag.Subiendo ? Etiqueta("Subiendo…", Oro) : null,
                ag.Aviso != null ? Etiqueta(ag.Aviso, Rojo) : null,
                Fila(ag.Vinculado && ag.A.Juego != null ? Boton("Subir ahora", true, (s, e) => ag.RevisaTodo(true)) : null,
                     Boton("Cambiar carpeta", false, (s, e) => EligeCarpeta()))));

            // Addon
            Control estadoAddon;
            Button botonAddon = null;
            if (ag.A.Juego == null) estadoAddon = Etiqueta("Sin carpeta del juego.", Gris);
            else if (ag.AddonInstalado == null)
            {
                estadoAddon = Etiqueta("El addon no está instalado.", ag.AddonUltimo != null ? Rojo : Gris);
                if (ag.AddonUltimo != null) botonAddon = Boton("Instalar el addon", true, (s, e) => ag.PermiteAddon());
            }
            else if (ag.AddonUltimo != null && Versiones.Compara(ag.AddonInstalado, ag.AddonUltimo) < 0)
            {
                estadoAddon = Etiqueta("Tienes la versión " + ag.AddonInstalado + "; hay una nueva: " + ag.AddonUltimo + ".", Oro);
                botonAddon = Boton("Actualizar el addon", true, (s, e) => ag.PermiteAddon());
            }
            else estadoAddon = Etiqueta("✓ Versión " + ag.AddonInstalado + (ag.AddonUltimo != null ? " (la última)." : "."), Verde);
            cuerpo.Controls.Add(Tarjeta("Addon",
                estadoAddon,
                ag.MensajeAddon != null ? Etiqueta(ag.MensajeAddon, Gris, 9f) : null,
                ag.A.AddonPermiso == "si" ? Etiqueta("Se mantiene al día solo.", Gris, 9f) : null,
                botonAddon != null ? Fila(botonAddon) : null));

            // Programa. Arranca siempre con Windows: no es una opción (si no, la gente no se acuerda de abrirlo).
            var salir = new LinkLabel { Text = "Salir del programa", AutoSize = true, LinkColor = Gris, ActiveLinkColor = Oro, Margin = new Padding(0, 8, 0, 0) };
            salir.LinkClicked += (s, e) =>
            {
                if (MessageBox.Show(this, "Si sales, no se subirá nada hasta que vuelvas a abrir el programa o reinicies Windows.\n\n¿Salir?", "Forever Tracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    salirPrograma();
            };
            cuerpo.Controls.Add(Tarjeta("Programa",
                Etiqueta("Funciona en segundo plano y arranca con Windows: al cerrar esta ventana sigue subiendo tus datos.", Gris, 9f),
                ag.ProgramaNuevo != null ? Etiqueta("Hay una versión nueva del programa (" + ag.ProgramaNuevo + "): se pone sola.", Oro, 9f) : null,
                ag.Pruebas ? Etiqueta("En modo de pruebas no se toca el arranque con Windows.", Gris, 9f) : null,
                Fila(Boton("Cerrar esta ventana", false, (s, e) => Close()), Boton("Ver el registro", false, (s, e) => AbreRegistro())),
                salir));
            cuerpo.ResumeLayout(true);
        }

        void EligeCarpeta()
        {
            using (var d = new FolderBrowserDialog { Description = "Elige la carpeta de World of Warcraft (o la de _forever_ dentro de ella).", ShowNewFolderButton = false })
            {
                if (ag.A.Juego != null) d.SelectedPath = ag.A.Juego;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!ag.CambiaJuego(d.SelectedPath))
                    MessageBox.Show(this, "En esa carpeta no está World of Warcraft: Forever (la carpeta _forever_).", "Forever Tracker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void AbreRegistro()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + Registro.Archivo + "\"") { UseShellExecute = false }); } catch { }
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
