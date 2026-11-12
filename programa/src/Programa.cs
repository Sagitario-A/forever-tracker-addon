// Forever Tracker para Windows. Programa sin ventana: sube solo el archivo del addon a la
// web cada vez que el juego lo guarda y mantiene el addon al día. La ventana de estado sale al abrirlo desde el menú Inicio.
//
// Forma de llamarlo:
//   ForeverTracker.exe                 abrirlo (con la ventana de estado); si ya funciona, enseña su ventana
//   ForeverTracker.exe --fondo         al arrancar Windows: sin ventana
//   ForeverTracker.exe --desinstalar   desde «Aplicaciones instaladas» (solo la descarga directa)
//   ForeverTracker.exe --actualizar    lo usa la actualización de la descarga directa
//   ForeverTracker.exe --salir         cierra el que esté funcionando
//   ForeverTracker.exe --pruebas <carpeta> [--web <dirección>] [--captura <png>]
//                                      modo de pruebas: datos en esa carpeta, sin instalar nada ni tocar el registro de
//                                      Windows, con la web que se diga (el entorno de pruebas); --captura guarda la ventana;
//                                      --vincular empieza a vincular aunque vaya sin ventana (pruebas automáticas).
//                                      Con "ProbarActualizacion": true en ajustes.json se actualiza también en pruebas
//                                      (se instala en <carpeta>\programa, sin tocar el registro ni el menú Inicio).
// La descarga directa, si no está en su sitio, se instala sola en %LOCALAPPDATA%\Programs\ForeverTracker (preguntando).
using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ForeverTracker
{
    public static class Programa
    {
        public const string Version = "1.0.3";
        public const string WebReal = "https://forever-tracker.kikoernk.workers.dev";

        [STAThread]
        static void Main(string[] args)
        {
            bool fondo = false, desinstalar = false, actualizar = false, salir = false, vincular = false;
            string pruebas = null, web = null, captura = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--fondo") fondo = true;
                else if (a == "--desinstalar") desinstalar = true;
                else if (a == "--actualizar") actualizar = true;
                else if (a == "--salir") salir = true;
                else if (a == "--vincular") vincular = true;
                else if (a == "--pruebas" && i + 1 < args.Length) pruebas = args[++i];
                else if (a == "--web" && i + 1 < args.Length) web = args[++i];
                else if (a == "--captura" && i + 1 < args.Length) captura = args[++i];
            }
            // Paquete de la Store (desde la 1.0.3): la tarea de inicio de Windows arranca una copia llamada ForeverTrackerFondo.exe
            // (no admite parámetros sin una segunda aplicación oculta, que la Store no deja tener): con ese nombre, sin ventana.
            if (Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetExecutingAssembly().Location).EndsWith("Fondo", StringComparison.OrdinalIgnoreCase)) fondo = true;
            string datos = pruebas != null ? Path.GetFullPath(pruebas) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForeverTracker");
            Directory.CreateDirectory(datos);
            Registro.Archivo = Path.Combine(datos, "registro.txt");
            string id = IdDe(datos);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (salir) { Senal("salir", id); return; }
                bool paquete = Instalacion.EsPaquete();
                if (desinstalar)
                {
                    if (!paquete) Instalacion.Desinstala(datos, id, () =>
                    {
                        string k = Clave.Lee(datos);
                        return k == null || new Web(WebReal).Delete("/api/programa/yo", k).Result.Bien;
                    });
                    return;
                }
                if (pruebas != null && actualizar && !paquete)
                {
                    // Modo de pruebas (desde la 1.0.1): la actualización se instala en <carpeta>\programa, sin tocar Windows.
                    Instalacion.CarpetaPruebas = datos;
                    Instalacion.Instala(true, id);
                    return;
                }
                if (pruebas == null && !paquete && (actualizar || !Instalacion.EstaInstalado()))
                {
                    Instalacion.Instala(actualizar, id);
                    return;
                }
                bool creado;
                using (var m = new Mutex(true, @"Local\ForeverTracker-" + id, out creado))
                {
                    if (!creado)
                    {
                        if (!fondo) Senal("mostrar", id); // ya funciona: que enseñe su ventana
                        return;
                    }
                    var aj = Ajustes.Carga(datos);
                    string url = WebReal;
                    if (pruebas != null)
                    {
                        if (web != null) { aj.Web = web; aj.Guarda(); }
                        url = aj.Web ?? WebReal;
                    }
                    // Arranca siempre con Windows; no es una opción. Se vuelve a apuntar cada vez que arranca, por si
                    // algo lo hubiera quitado. Si alguien lo desactiva desde el propio Windows (Administrador de tareas → Inicio), Windows
                    // lo respeta: eso no se toca. En la Store lo hace la tarea de inicio del paquete.
                    if (pruebas == null && !paquete) Instalacion.ArranqueConWindows(true);
                    Application.Run(new Contexto(datos, aj, url, pruebas != null, !fondo, captura, id, vincular && pruebas != null));
                    GC.KeepAlive(m);
                }
            }
            catch (Exception e)
            {
                Registro.Apunta("Error grave: " + e);
                if (!fondo) MessageBox.Show("Forever Tracker ha tenido un problema:\n\n" + e.Message, "Forever Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Un nombre corto y fijo para cada carpeta de datos: así el de pruebas y el de verdad no se pisan.
        static string IdDe(string datos)
        {
            using (var sha = SHA256.Create()) return Clave.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(datos.ToLowerInvariant()))).Substring(0, 12);
        }

        // Avisar al programa que ya funciona («mostrar» su ventana o «salir»).
        public static void Senal(string que, string id)
        {
            try { using (var e = EventWaitHandle.OpenExisting(@"Local\ForeverTracker-" + que + "-" + id)) e.Set(); }
            catch { }
        }
    }

    // El programa funcionando: el agente de fondo y, cuando se pide, la ventana de estado.
    class Contexto : ApplicationContext
    {
        readonly Agente ag;
        readonly Control marca;
        Ventana v;
        readonly EventWaitHandle mostrar, salir;
        readonly RegisteredWaitHandle esperaMostrar, esperaSalir;
        System.Windows.Forms.Timer tCaptura;

        public Contexto(string datos, Ajustes aj, string web, bool pruebas, bool interactivo, string captura, string id, bool vincular)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            marca = new Control();
            marca.CreateControl();
            mostrar = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ForeverTracker-mostrar-" + id);
            salir = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ForeverTracker-salir-" + id);
            esperaMostrar = ThreadPool.RegisterWaitForSingleObject(mostrar, (s, t) => marca.BeginInvoke((Action)Muestra), null, -1, false);
            esperaSalir = ThreadPool.RegisterWaitForSingleObject(salir, (s, t) => marca.BeginInvoke((Action)Termina), null, -1, false);
            ag = new Agente(datos, aj, web, pruebas);
            ag.Arranca(interactivo && captura == null, vincular);
            if (interactivo && captura == null) Muestra();
            if (captura != null)
            {
                // Pruebas: esperar a que hable con la web, enseñar la ventana, guardarla como imagen y cerrar.
                tCaptura = new System.Windows.Forms.Timer { Interval = 6000 };
                tCaptura.Tick += (s, e) =>
                {
                    tCaptura.Stop();
                    Muestra();
                    Application.DoEvents();
                    v.Captura(captura);
                    Termina();
                };
                tCaptura.Start();
            }
            Agente.Recorta();
        }

        void Muestra()
        {
            if (v == null || v.IsDisposed)
            {
                v = new Ventana(ag, Termina);
                v.FormClosed += (s, e) => { v = null; Agente.Recorta(); };
            }
            v.Show();
            if (v.WindowState == FormWindowState.Minimized) v.WindowState = FormWindowState.Normal;
            v.Activate();
        }

        void Termina()
        {
            Registro.Apunta("Se cierra el programa.");
            if (v != null && !v.IsDisposed) v.Close();
            esperaMostrar.Unregister(null);
            esperaSalir.Unregister(null);
            ExitThread();
        }
    }
}
