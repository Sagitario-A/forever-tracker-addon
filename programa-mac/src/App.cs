// La aplicación de Avalonia: tema oscuro con los colores de la web y, al arrancar, el programa (Programa.Arranque).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ForeverTracker
{
    public class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var escritorio = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (escritorio != null)
            {
                // Sin ventana el programa sigue funcionando (sube solo cuando el juego guarda): solo se cierra cuando se le pide.
                escritorio.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Programa.Arranque(escritorio);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
