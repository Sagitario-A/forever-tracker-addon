// Los colores de la web y las piezas de la ventana (etiquetas, botones, tarjetas), con Avalonia. Lo mismo que Tema en Windows.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ForeverTracker
{
    public static class Tema
    {
        public static readonly Color Fondo = Color.FromRgb(18, 18, 18), Panel = Color.FromRgb(28, 28, 28), Linea = Color.FromRgb(46, 46, 46),
            Texto = Color.FromRgb(214, 214, 214), Blanco = Colors.White, Gris = Color.FromRgb(143, 143, 143), Oro = Color.FromRgb(255, 209, 0),
            Verde = Color.FromRgb(76, 187, 23), Ambar = Color.FromRgb(240, 160, 32), Rojo = Color.FromRgb(224, 80, 58),
            BotonFondo = Color.FromRgb(38, 38, 38), BotonBorde = Color.FromRgb(68, 68, 68), SobreOro = Color.FromRgb(22, 20, 12);

        public static IBrush Pincel(Color c) { return new SolidColorBrush(c); }

        public static TextBlock Etiqueta(string t, Color c, double tam = 13, bool negrita = false, double anchoMax = 512)
        {
            return new TextBlock
            {
                Text = t, Foreground = Pincel(c), FontSize = tam, FontWeight = negrita ? FontWeight.Bold : FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap, MaxWidth = anchoMax, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left,
            };
        }

        public static Button Boton(string t, bool principal, Action clic)
        {
            var b = new Button
            {
                Content = t, Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 8, 0),
                Background = Pincel(principal ? Oro : BotonFondo), Foreground = Pincel(principal ? SobreOro : Texto),
                BorderBrush = Pincel(principal ? Oro : BotonBorde), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                FontSize = 13, FontWeight = principal ? FontWeight.Bold : FontWeight.Normal,
            };
            b.Click += (s, e) => { try { clic(); } catch (Exception ex) { Registro.Apunta("Error en la ventana: " + ex); } };
            return b;
        }

        // Varios controles en fila, que saltan de línea si no caben.
        public static WrapPanel Fila(params Control[] c)
        {
            var f = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
            foreach (var x in c) if (x != null) f.Children.Add(x);
            return f;
        }

        // Un punto de una lista: símbolo en color y el texto al lado.
        public static Control Punto(string simbolo, Color color, string texto, double ancho)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*"), Margin = new Thickness(0, 1, 0, 1) };
            var s = Etiqueta(simbolo, color, 13.5, true, 30);
            var t = Etiqueta(texto, Texto, 13, false, ancho - 40);
            Grid.SetColumn(s, 0); Grid.SetColumn(t, 1);
            g.Children.Add(s); g.Children.Add(t);
            return g;
        }

        // Una tarjeta con título, como las de Ajustes en la web.
        public static Control Tarjeta(string titulo, params Control[] dentro)
        {
            var p = new StackPanel { Orientation = Orientation.Vertical };
            if (titulo != null) p.Children.Add(Etiqueta(titulo, Blanco, 14, true));
            foreach (var c in dentro) if (c != null) p.Children.Add(c);
            return new Border
            {
                Background = Pincel(Panel), BorderBrush = Pincel(Linea), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = p,
            };
        }

        public static WindowIcon Icono()
        {
            try
            {
                var s = typeof(Tema).Assembly.GetManifestResourceStream("icono.png");
                return s == null ? null : new WindowIcon(s);
            }
            catch { return null; }
        }
    }
}
