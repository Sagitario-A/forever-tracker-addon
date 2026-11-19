// Forever Tracker para Mac (desde la 1.1.4; paquete universal con lanzador desde la 1.1.5, ver Instalacion.RutaApp). Lo mismo que el de Windows: sube solo el archivo del addon a la web cada vez que el
// juego lo guarda, instala y mantiene el addon al día y escribe en el addon la lista de encargos. Sin ventana; la de estado sale al
// abrirlo desde Aplicaciones (hasta que el Mac está vinculado, la bienvenida). La lógica es la de ../programa/src; aquí, la caja.
//
// Forma de llamarlo (igual que en Windows):
//   ForeverTracker                     abrirlo (con la ventana de estado); si ya funciona, enseña su ventana
//   ForeverTracker --fondo             al arrancar el sistema: sin ventana (el LaunchAgent)
//   ForeverTracker --desinstalar       (también con el botón «Desinstalar…» de la ventana)
//   ForeverTracker --salir             cierra el que esté funcionando
//   ForeverTracker --pruebas <carpeta> [--web <dirección>] [--captura <png>] [--vincular]
//                                      modo de pruebas: datos en esa carpeta, sin instalar nada, con la web que se diga; funciona
//                                      también en Windows (para probar la lógica sin un Mac). "ProbarActualizacion": true en
//                                      ajustes.json: se actualiza también en pruebas (en <carpeta>/programa).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace ForeverTracker
{
    public static partial class Programa
    {
        static string datos, web, captura, id;
        static bool fondo, vincular, pruebas, desinstalar;
        static IClassicDesktopStyleApplicationLifetime escritorio;
        static Agente ag;
        static Ventana v;
        static FileStream cerrojo;
        static FileSystemWatcher senales;
        static PosixSignalRegistration sigterm;

        [STAThread]
        public static int Main(string[] args)
        {
            bool salir = false;
            string carpetaPruebas = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--fondo") fondo = true;
                else if (a == "--desinstalar") desinstalar = true;
                else if (a == "--salir") salir = true;
                else if (a == "--vincular") vincular = true;
                else if (a == "--pruebas" && i + 1 < args.Length) carpetaPruebas = args[++i];
                else if (a == "--web" && i + 1 < args.Length) web = args[++i];
                else if (a == "--captura" && i + 1 < args.Length) captura = args[++i];
            }
            pruebas = carpetaPruebas != null;
            datos = pruebas ? Path.GetFullPath(carpetaPruebas) : CarpetaDatos();
            Directory.CreateDirectory(datos);
            Plataforma.SoloUsuario(datos);
            Registro.Archivo = Path.Combine(datos, "registro.txt");
            id = IdDe(datos);
            if (pruebas) Instalacion.CarpetaPruebas = datos;
            try
            {
                if (salir) { Senal("salir", datos); return 0; }
                if (!desinstalar && !TomaCerrojo())
                {
                    // Ya funciona: que enseñe su ventana.
                    if (!fondo) Senal("mostrar", datos);
                    return 0;
                }
                AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
                return 0;
            }
            catch (Exception e)
            {
                Registro.Apunta("Error grave: " + e);
                return 1;
            }
        }

        // Con Avalonia ya en marcha (App.OnFrameworkInitializationCompleted).
        public static async void Arranque(IClassicDesktopStyleApplicationLifetime d)
        {
            escritorio = d;
            try
            {
                if (desinstalar)
                {
                    await Instalacion.Desinstala(datos, async () =>
                    {
                        string k = Clave.Lee(datos);
                        return k == null || (await new Web(WebReal).Delete("/api/programa/yo", k)).Bien;
                    });
                    Termina();
                    return;
                }
                // Abierto desde Descargas (o desde cualquier sitio que no sea ~/Applications): se instala, preguntando, como en Windows.
                if (Plataforma.EsMac && !pruebas && Instalacion.RutaApp != null && !Instalacion.EstaInstalado())
                {
                    if (await Dialogo.Instalar()) Instalacion.Instala(datos);
                    Termina();
                    return;
                }
                var aj = Ajustes.Carga(datos);
                string url = WebReal;
                if (pruebas)
                {
                    if (web != null) { aj.Web = web; aj.Guarda(); }
                    url = aj.Web ?? WebReal;
                }
                // Arranca siempre con el sistema; se vuelve a apuntar cada vez por si el paquete cambió de sitio.
                Instalacion.ArranqueConSistema(true);
                VigilaSenales();
                if (!OperatingSystem.IsWindows()) sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; Dispatcher.UIThread.Post(Termina); });
                d.ShutdownRequested += (s, e) => { e.Cancel = true; if (v != null) v.Close(); }; // Cmd+Q cierra la ventana, no el programa
                var activable = Application.Current.TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime;
                if (activable != null) activable.Activated += (s, e) => { if (e.Kind == ActivationKind.Reopen) Muestra(); };
                if (Plataforma.EsMac) MenuNativo();
                ag = new Agente(datos, aj, url, pruebas);
                ag.Arranca(!fondo && captura == null, vincular && pruebas);
                if (!fondo && captura == null) Muestra();
                if (captura != null)
                {
                    // Pruebas: esperar a que hable con la web, enseñar la ventana, guardarla como imagen y cerrar.
                    await Task.Delay(6000);
                    Muestra();
                    await Task.Delay(800);
                    v.Captura(captura);
                    Termina();
                }
            }
            catch (Exception e)
            {
                Registro.Apunta("Error grave: " + e);
                if (!fondo) await Dialogo.Avisa(null, "Forever Tracker ha tenido un problema:\n\n" + e.Message);
                Termina();
            }
        }

        // Desinstalar (el botón «Desinstalar…» de la ventana, desde la 1.1.5; también el menú del programa, por si el Mac lo enseña).
        public static async Task Desinstalar()
        {
            await Instalacion.Desinstala(datos, async () => { string k = Clave.Lee(datos); return k == null || (await new Web(WebReal).Delete("/api/programa/yo", k)).Bien; });
        }

        // El menú del programa en Mac (la barra de arriba): desinstalar. Con LSUIElement (sin icono en el Dock) macOS no enseña la barra
        // de menús del programa, así que desde la 1.1.5 la ventana lleva el botón «Desinstalar…»; el menú se deja por si acaso.
        static void MenuNativo()
        {
            try
            {
                var menu = new NativeMenu();
                var item = new NativeMenuItem("Desinstalar Forever Tracker…");
                item.Click += async (s, e) => { await Desinstalar(); };
                menu.Items.Add(item);
                NativeMenu.SetMenu(Application.Current, menu);
            }
            catch (Exception e) { Registro.Apunta("Menú: " + e.Message); }
        }

        static void Muestra()
        {
            if (v == null)
            {
                v = new Ventana(ag);
                v.Closed += (s, e) => { v = null; Agente.Recorta(); };
            }
            v.Show();
            if (v.WindowState == WindowState.Minimized) v.WindowState = WindowState.Normal;
            v.Activate();
            v.Topmost = true;
            v.Topmost = false;
        }

        public static void Termina()
        {
            Registro.Apunta("Se cierra el programa.");
            try { if (senales != null) senales.Dispose(); } catch { }
            try { if (v != null) v.Close(); } catch { }
            try { if (cerrojo != null) cerrojo.Dispose(); } catch { }
            try { if (sigterm != null) sigterm.Dispose(); } catch { }
            if (escritorio != null) escritorio.Shutdown();
            else Environment.Exit(0);
        }

        // ───────── una sola copia y señales entre copias ─────────
        // Un archivo abierto en exclusiva en la carpeta de datos: la segunda copia no lo puede abrir y sabe que ya hay una.
        static bool TomaCerrojo()
        {
            try
            {
                cerrojo = new FileStream(Path.Combine(datos, "en-marcha.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }
        }

        // «mostrar» o «salir»: un archivo en la carpeta de datos que la copia que funciona vigila (vale en Mac y en Windows).
        public static void Senal(string que, string carpeta)
        {
            try { File.WriteAllText(Path.Combine(carpeta, que + ".senal"), DateTime.UtcNow.ToString("o")); } catch { }
        }

        static void VigilaSenales()
        {
            foreach (string f in Directory.GetFiles(datos, "*.senal")) try { File.Delete(f); } catch { }
            senales = new FileSystemWatcher(datos, "*.senal");
            FileSystemEventHandler h = (s, e) => Dispatcher.UIThread.Post(() => Senalado(e.FullPath));
            senales.Created += h;
            senales.Changed += h;
            senales.EnableRaisingEvents = true;
        }

        static void Senalado(string f)
        {
            string que = Path.GetFileNameWithoutExtension(f);
            try { File.Delete(f); } catch { }
            if (que == "mostrar") Muestra();
            else if (que == "salir") Termina();
        }

        // ~/Library/Application Support/ForeverTracker en Mac; en Windows (solo pruebas), %LOCALAPPDATA%\ForeverTrackerMac.
        static string CarpetaDatos()
        {
            return Plataforma.EsMac
                ? Path.Combine(Plataforma.Casa, "Library", "Application Support", "ForeverTracker")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForeverTrackerMac");
        }

        // Un nombre corto y fijo para cada carpeta de datos (el mismo cálculo que en Windows).
        static string IdDe(string carpeta)
        {
            using (var sha = SHA256.Create()) return Clave.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(carpeta.ToLowerInvariant()))).Substring(0, 12);
        }
    }
}
