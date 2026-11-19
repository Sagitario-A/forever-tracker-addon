// Ventanitas propias (pregunta sí/no, aviso, la de instalar) con los colores de la web. Las mismas que en Windows, con Avalonia.
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace ForeverTracker
{
    public class Dialogo : Window
    {
        bool si, no, segundo;
        readonly TaskCompletionSource<bool> cerrado = new TaskCompletionSource<bool>();

        Dialogo(string texto, string botonSi, string botonNo, bool cabecera = false, string titular = null, string nota = null)
        {
            Title = "Forever Tracker";
            Background = Tema.Pincel(Tema.Fondo);
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Icon = Tema.Icono();
            double ancho = cabecera ? 520 : 440;
            var cuerpo = new StackPanel { Orientation = Orientation.Vertical, Margin = cabecera ? new Thickness(40, 32, 40, 32) : new Thickness(20, 16, 20, 16) };
            if (cabecera)
            {
                var cab = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
                var icono = Tema.Icono();
                if (icono != null)
                {
                    try { cab.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(typeof(Tema).Assembly.GetManifestResourceStream("icono.png")), Width = 36, Height = 36, Margin = new Thickness(0, 2, 10, 0) }); }
                    catch { }
                }
                cab.Children.Add(Tema.Etiqueta("Forever Tracker", Tema.Oro, 24, true, ancho));
                cuerpo.Children.Add(cab);
                if (titular != null)
                {
                    var t = Tema.Etiqueta(titular, Tema.Blanco, 19, true, ancho);
                    t.Margin = new Thickness(0, 0, 0, 14);
                    cuerpo.Children.Add(t);
                }
            }
            var cuerpoTexto = Tema.Etiqueta(texto, Tema.Texto, cabecera ? 14.5 : 13, false, ancho);
            if (cabecera) cuerpoTexto.Margin = new Thickness(0, 0, 0, 24);
            cuerpo.Children.Add(cuerpoTexto);
            if (nota != null)
            {
                var n = Tema.Etiqueta(nota, Tema.Gris, 12, false, ancho);
                n.Margin = new Thickness(0, 6, 0, 2);
                cuerpo.Children.Add(n);
            }
            var b1 = Tema.Boton(botonSi, true, () => { si = true; Close(); });
            Button b2 = null;
            if (botonNo != null) b2 = Tema.Boton(botonNo, false, () => { no = segundo; Close(); });
            if (cabecera)
            {
                b1.Padding = new Thickness(22, 8, 22, 8); b1.Margin = new Thickness(0, 6, 14, 0); b1.FontSize = 14.5;
                if (b2 != null) { b2.Padding = new Thickness(18, 8, 18, 8); b2.Margin = new Thickness(0, 6, 0, 0); b2.FontSize = 14.5; }
            }
            cuerpo.Children.Add(Tema.Fila(b1, b2));
            Content = cuerpo;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key == Key.Enter) { si = true; Close(); } };
            Closed += (s, e) => cerrado.TrySetResult(true);
            Opened += (s, e) => { Activate(); Topmost = true; Topmost = false; };
        }

        async Task<bool> Muestra(Window owner)
        {
            if (owner != null) await ShowDialog(owner);
            else { Show(); await cerrado.Task; }
            return si;
        }

        // Pregunta sí/no; con «owner» null, en medio de la pantalla.
        public static Task<bool> Pregunta(Window owner, string texto, string botonSi, string botonNo)
        {
            return new Dialogo(texto, botonSi, botonNo).Muestra(owner);
        }

        public static Task Avisa(Window owner, string texto)
        {
            return Pregunta(owner, texto, "Vale", null);
        }

        // Elegir entre dos: 1 el primero, 2 el segundo, 0 si se cierra la ventana sin elegir.
        public static async Task<int> Elige(Window owner, string texto, string boton1, string boton2)
        {
            var d = new Dialogo(texto, boton1, boton2) { segundo = true };
            await d.Muestra(owner);
            return d.si ? 1 : d.no ? 2 : 0;
        }

        // La de instalar (la primera vez que se abre el programa descargado). Amable, clara y corta, como en Windows.
        public static Task<bool> Instalar()
        {
            return new Dialogo(
                "A continuación se instalará un pequeño programa que envía los datos de tu addon a la web para mantener tu cuenta al día.",
                "Instalar", "Ahora no", true, "Gracias por usar Forever Tracker").Muestra(null);
        }
    }
}
