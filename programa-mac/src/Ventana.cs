// La ventana del programa en Mac: la misma que en Windows (bienvenida hasta que el Mac está vinculado; después el estado sin datos
// del juego: una línea de estado, la cuenta y la última subida, la carpeta del juego con «Detectar» y «Cambiar», y el addon).
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ForeverTracker
{
    public class Ventana : Window
    {
        const double Ancho = 540;
        const double Dentro = Ancho - 2 * 20 - 2 * 14;

        readonly Agente ag;
        readonly StackPanel cuerpo;
        readonly Border fondo;
        readonly DispatcherTimer reloj;
        string firma;

        public Ventana(Agente agente)
        {
            ag = agente;
            Title = "Forever Tracker";
            Width = Ancho;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            Background = Tema.Pincel(Tema.Fondo);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Icon = Tema.Icono();
            cuerpo = new StackPanel { Orientation = Orientation.Vertical };
            fondo = new Border { Background = Tema.Pincel(Tema.Fondo), Padding = new Thickness(20, 16, 20, 18), Child = cuerpo };
            Content = fondo;
            ag.Cambio += () => Dispatcher.UIThread.Post(Pinta);
            reloj = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            reloj.Tick += (s, e) => Pinta();
            reloj.Start();
            Closed += (s, e) => reloj.Stop();
            Pinta();
        }

        static TextBlock Etiqueta(string t, Color c, double tam = 13, bool negrita = false, double ancho = Dentro) { return Tema.Etiqueta(t, c, tam, negrita, ancho); }

        Control Cabecera()
        {
            var f = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            try
            {
                var s = typeof(Tema).Assembly.GetManifestResourceStream("icono.png");
                if (s != null) f.Children.Add(new Image { Source = new Bitmap(s), Width = 28, Height = 28, Margin = new Thickness(0, 4, 8, 0) });
            }
            catch { }
            f.Children.Add(Etiqueta("Forever Tracker", Tema.Oro, 21, true, Ancho));
            var v = Etiqueta("versión " + Programa.Version + (ag.Pruebas && ag.A.CapturaCarpeta == null ? " · PRUEBAS (" + ag.W.Base + ")" : ""), Tema.Gris, 11, false, Ancho - 40 - 230);
            v.Margin = new Thickness(10, 12, 0, 0);
            f.Children.Add(v);
            return f;
        }

        string Carpeta { get { return (ag.Pruebas ? ag.A.CapturaCarpeta : null) ?? ag.A.Juego; } }

        Subida Ultima()
        {
            Subida u = null;
            foreach (var s in ag.A.Subidas.Values) if (u == null || s.Cuando > u.Cuando) u = s;
            return u;
        }

        string UltimaSubida()
        {
            var u = Ultima();
            if (u == null) return "todavía sin subidas (se sube solo al salir del juego o con /reload)";
            long min = (Tiempo.Ahora() - u.Cuando) / 60000;
            if (min < 60) return "última subida " + Tiempo.Hace(u.Cuando);
            var cuando = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(u.Cuando).ToLocalTime();
            return "última subida " + (cuando.Date == DateTime.Now.Date ? "" : "el " + cuando.ToString("d/M") + " ") + "a las " + cuando.ToString("HH:mm");
        }

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

        string Firma()
        {
            string estado = ""; Color color = Colors.Transparent;
            if (ag.Vinculado) Estado(out estado, out color);
            return string.Join("|", ag.Vinculado, ag.Vinculando, ag.Codigo, ag.UrlVincular, ag.MensajeVincular, ag.A.Juego, Carpeta, ag.A.Cuenta, estado, color.ToUInt32(),
                ag.Vinculado ? UltimaSubida() : "", ag.AddonInstalando, ag.AddonInstalado, ag.AddonUltimo, ag.MensajeAddon, ag.ProgramaNuevo);
        }

        void Pinta()
        {
            string f = Firma();
            if (f == firma) return;
            firma = f;
            cuerpo.Children.Clear();
            cuerpo.Children.Add(Cabecera());
            if (ag.Vinculado) Vinculado();
            else Bienvenida();
        }

        void Bienvenida()
        {
            var gracias = Etiqueta("Gracias por descargar Forever Tracker", Tema.Blanco, 18, true, Ancho);
            gracias.Margin = new Thickness(0, 8, 0, 6);
            cuerpo.Children.Add(gracias);
            var que = Etiqueta("Qué hace", Tema.Oro, 14, true, Ancho);
            que.Margin = new Thickness(0, 6, 0, 2);
            cuerpo.Children.Add(que);
            cuerpo.Children.Add(Tema.Punto("✓", Tema.Oro, "Instala el addon Forever Tracker en tu juego y lo mantiene al día, sin que tengas que hacer nada.", Ancho - 40));
            cuerpo.Children.Add(Tema.Punto("✓", Tema.Oro, "Sube tus datos del juego a tu cuenta de la web cada vez que el juego los guarda (al salir o con /reload).", Ancho - 40));
            if (ag.A.Juego == null)
            {
                var sin = Etiqueta("No encuentro World of Warcraft: Forever en este Mac. Elige su carpeta para que pueda instalar el addon.", Tema.Rojo, 13, false, Ancho - 40);
                sin.Margin = new Thickness(0, 10, 0, 0);
                cuerpo.Children.Add(sin);
                cuerpo.Children.Add(Tema.Fila(Tema.Boton("Elegir carpeta", false, () => EligeCarpeta())));
            }
            if (ag.Vinculando)
            {
                var vinc = Etiqueta(ag.Codigo == null ? (ag.MensajeVincular ?? "Preparando…") : "Se ha abierto la web en tu navegador. Comprueba que enseña este mismo código y pulsa «Sí, vincular»:", Tema.Texto, 13, false, Ancho - 40);
                vinc.Margin = new Thickness(0, 14, 0, 2);
                cuerpo.Children.Add(vinc);
                if (ag.Codigo != null)
                {
                    var codigo = Etiqueta(ag.Codigo, Tema.Oro, 37, true, Ancho - 40);
                    codigo.Margin = new Thickness(0, 6, 0, 4);
                    cuerpo.Children.Add(codigo);
                    cuerpo.Children.Add(Etiqueta("Esperando a que aceptes en la web…", Tema.Gris, 12, false, Ancho - 40));
                }
                cuerpo.Children.Add(Tema.Fila(ag.UrlVincular != null && ag.Codigo != null ? Tema.Boton("Abrir otra vez la página", false, () => Agente.AbreNavegador(ag.UrlVincular)) : null,
                                              Tema.Boton("Cancelar", false, () => ag.CancelaVincular())));
            }
            else
            {
                var para = Etiqueta("Para empezar, vincula este Mac a tu cuenta de la web: se abrirá el navegador con un código. Nada se sube hasta que digas que sí en la web.", Tema.Gris, 13, false, Ancho - 40);
                para.Margin = new Thickness(0, 14, 0, 2);
                cuerpo.Children.Add(para);
                if (ag.MensajeVincular != null) cuerpo.Children.Add(Etiqueta(ag.MensajeVincular, Tema.Rojo, 13, false, Ancho - 40));
                cuerpo.Children.Add(Tema.Fila(Tema.Boton("Vincular este Mac", true, () => ag.Vincula())));
            }
            var nota = Etiqueta("Puedes cerrar esta ventana: el programa sigue en segundo plano y arranca con el Mac. Vuelve a abrirlo desde Aplicaciones cuando quieras.", Tema.Gris, 12, false, Ancho - 40);
            nota.Margin = new Thickness(0, 16, 0, 0);
            cuerpo.Children.Add(nota);
            Desinstalar();
        }

        // El botón de desinstalar (desde la 1.1.5): en Mac el programa no tiene barra de menús (va sin icono en el Dock), así que va aquí.
        void Desinstalar()
        {
            if (!Plataforma.EsMac && Environment.GetEnvironmentVariable("FT_VENTANA_MAC") == null) return; // en Windows solo para la captura de pruebas
            var b = Tema.Boton("Desinstalar Forever Tracker…", false, async () => { await Programa.Desinstalar(); });
            b.Margin = new Thickness(0, 14, 8, 0);
            cuerpo.Children.Add(Tema.Fila(b));
        }

        void Vinculado()
        {
            string estado; Color color;
            Estado(out estado, out color);
            var linea = Etiqueta("●  " + estado, color, 13, true, Ancho - 40);
            linea.Margin = new Thickness(0, 0, 0, 2);
            cuerpo.Children.Add(linea);
            var cuenta = Etiqueta("Cuenta «" + (ag.A.Cuenta ?? "?") + "» – " + UltimaSubida(), Tema.Texto, 13, false, Ancho - 40);
            cuenta.Margin = new Thickness(0, 0, 0, ag.ProgramaNuevo != null ? 2 : 12);
            cuerpo.Children.Add(cuenta);
            if (ag.ProgramaNuevo != null)
            {
                var nuevo = Etiqueta("Hay una versión nueva del programa (" + ag.ProgramaNuevo + "): se pone sola.", Tema.Ambar, 12, false, Ancho - 40);
                nuevo.Margin = new Thickness(0, 0, 0, 12);
                cuerpo.Children.Add(nuevo);
            }
            cuerpo.Children.Add(Tema.Tarjeta("Archivos del juego",
                Etiqueta(Carpeta ?? "No encuentro la carpeta de World of Warcraft: Forever.", Carpeta != null ? Tema.Texto : Tema.Rojo),
                Tema.Fila(Tema.Boton("Detectar carpeta", false, () => DetectaCarpeta()), Tema.Boton("Cambiar carpeta", false, () => EligeCarpeta()))));
            Control estadoAddon;
            bool reintentar = false;
            if (ag.A.Juego == null) estadoAddon = Etiqueta("Sin carpeta del juego.", Tema.Gris);
            else if (ag.AddonInstalando) estadoAddon = Etiqueta("Instalando el addon" + (ag.AddonUltimo != null ? " " + ag.AddonUltimo : "") + "…", Tema.Ambar);
            else if (ag.AddonInstalado == null)
            {
                estadoAddon = Etiqueta(ag.AddonUltimo == null ? "Todavía no se ha podido comprobar la versión del addon." : "El addon no está instalado.", ag.AddonUltimo == null ? Tema.Gris : Tema.Ambar);
                reintentar = ag.AddonUltimo != null;
            }
            else if (ag.AddonUltimo != null && Versiones.Compara(ag.AddonInstalado, ag.AddonUltimo) < 0)
            {
                estadoAddon = Etiqueta("Tienes la versión " + ag.AddonInstalado + "; se pone la " + ag.AddonUltimo + ".", Tema.Ambar);
                reintentar = ag.MensajeAddon != null;
            }
            else estadoAddon = Etiqueta("✓ Versión " + ag.AddonInstalado + (ag.AddonUltimo != null ? ", la última." : ".") + " Se mantiene al día solo.", Tema.Verde);
            cuerpo.Children.Add(Tema.Tarjeta("Addon",
                estadoAddon,
                ag.MensajeAddon != null ? Etiqueta(ag.MensajeAddon, Tema.Rojo, 12) : null,
                reintentar ? Tema.Fila(Tema.Boton("Volver a intentar", false, () => ag.ReintentaAddon())) : null));
            Desinstalar();
        }

        async void DetectaCarpeta()
        {
            if (!ag.DetectaJuego())
                await Dialogo.Avisa(this, "No encuentro World of Warcraft: Forever en este Mac (lo busco en Aplicaciones y en Battle.net). Elígela con «Cambiar carpeta».");
        }

        async void EligeCarpeta()
        {
            try
            {
                var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Elige la carpeta de World of Warcraft (o la de _forever_ dentro de ella).", AllowMultiple = false });
                if (r == null || r.Count == 0) return;
                string ruta = r[0].TryGetLocalPath();
                if (ruta == null || !ag.CambiaJuego(ruta))
                    await Dialogo.Avisa(this, "En esa carpeta no está World of Warcraft: Forever (la carpeta _forever_).");
            }
            catch (Exception e) { Registro.Apunta("Elegir carpeta: " + e.Message); }
        }

        // Pruebas: la ventana como imagen.
        public void Captura(string png)
        {
            fondo.Measure(new Size(Ancho, double.PositiveInfinity));
            fondo.Arrange(new Rect(fondo.DesiredSize));
            var tam = new PixelSize((int)Math.Ceiling(fondo.Bounds.Width), (int)Math.Ceiling(fondo.Bounds.Height));
            using (var rtb = new RenderTargetBitmap(tam))
            {
                rtb.Render(fondo);
                rtb.Save(png);
            }
        }
    }
}
