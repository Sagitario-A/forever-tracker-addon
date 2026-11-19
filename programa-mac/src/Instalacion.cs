// Instalar, arrancar con el sistema, ponerse al día y desinstalar, en Mac. El paquete descargado («Forever Tracker.app») se copia a
// ~/Applications (la carpeta de aplicaciones del usuario: no pide contraseña) y desde ahí arranca con el sistema (un LaunchAgent del
// usuario, ~/Library/LaunchAgents). La actualización baja el .zip nuevo, lo descomprime y un guion cambia el paquete cuando este
// programa se cierra. Nada de esto toca otras carpetas; en modo de pruebas todo va dentro de la carpeta de pruebas.
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ForeverTracker
{
    public static class Instalacion
    {
        const string Etiqueta = "app.forevertracker.programa";
        public static string CarpetaPruebas; // modo de pruebas: el programa «instalado» va en <carpeta>/programa

        // El paquete .app que contiene este ejecutable (…/Forever Tracker.app/Contents/MacOS/ForeverTracker), o null si no va en uno.
        public static string RutaApp
        {
            get
            {
                try
                {
                    var d = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\'));
                    if (d.Name == "MacOS" && d.Parent != null && d.Parent.Name == "Contents" && d.Parent.Parent != null && d.Parent.Parent.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                        return d.Parent.Parent.FullName;
                }
                catch { }
                return null;
            }
        }

        public static string AppInstalada
        {
            get { return CarpetaPruebas != null ? Path.Combine(CarpetaPruebas, "programa", "Forever Tracker.app") : Path.Combine(Plataforma.Casa, "Applications", "Forever Tracker.app"); }
        }

        public static string Ejecutable(string app) { return Path.Combine(app, "Contents", "MacOS", "ForeverTracker"); }

        public static bool EstaInstalado()
        {
            string r = RutaApp;
            return r != null && string.Equals(Path.GetFullPath(r).TrimEnd('/'), Path.GetFullPath(AppInstalada).TrimEnd('/'), StringComparison.Ordinal);
        }

        // Copia el paquete entero aparte y lo cambia de golpe; al final le quita la marca de cuarentena (el usuario ya lo abrió).
        static void CopiaApp(string origen, string destino)
        {
            string nuevo = destino + ".nuevo", viejo = destino + ".viejo";
            Borra(nuevo); Borra(viejo);
            Directory.CreateDirectory(Path.GetDirectoryName(destino));
            CopiaCarpeta(origen, nuevo);
            Plataforma.HazEjecutable(Ejecutable(nuevo));
            if (Directory.Exists(destino)) Directory.Move(destino, viejo);
            try { Directory.Move(nuevo, destino); }
            catch { if (Directory.Exists(viejo) && !Directory.Exists(destino)) Directory.Move(viejo, destino); throw; }
            Borra(viejo);
            if (Plataforma.EsMac) Plataforma.Ejecuta("xattr", "-dr", "com.apple.quarantine", destino);
        }

        static void CopiaCarpeta(string de, string a)
        {
            Directory.CreateDirectory(a);
            foreach (string f in Directory.GetFiles(de))
            {
                string destino = Path.Combine(a, Path.GetFileName(f));
                File.Copy(f, destino, true);
                if (Plataforma.EsMac) try { File.SetUnixFileMode(destino, File.GetUnixFileMode(f)); } catch { }
            }
            foreach (string d in Directory.GetDirectories(de)) CopiaCarpeta(d, Path.Combine(a, Path.GetFileName(d)));
        }

        static void Borra(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception e) { Registro.Apunta("No se pudo borrar " + d + ": " + e.Message); }
        }

        // La primera vez (abierto desde Descargas): se copia a ~/Applications, se apunta el arranque con el sistema y arranca desde allí.
        public static void Instala(string datos)
        {
            Programa.Senal("salir", datos);
            Thread.Sleep(1500);
            CopiaApp(RutaApp, AppInstalada);
            ArranqueConSistema(true);
            Registro.Apunta("Instalado en " + AppInstalada);
            Abre(AppInstalada, false);
        }

        static void Abre(string app, bool fondo)
        {
            if (CarpetaPruebas != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Ejecutable(app)) { UseShellExecute = false, ArgumentList = { "--fondo", "--pruebas", CarpetaPruebas } });
                return;
            }
            if (fondo) Plataforma.Ejecuta("open", "-a", app, "--args", "--fondo");
            else Plataforma.Ejecuta("open", "-a", app);
        }

        // La versión nueva, ya descargada y comprobada: se descomprime aparte y un guion del sistema cambia el paquete en cuanto este
        // programa se cierra, y lo vuelve a abrir en segundo plano.
        public static void Actualiza(byte[] zip, string version, string datos, bool pruebas)
        {
            string dir = Path.Combine(datos, "actualizacion");
            Borra(dir);
            Directory.CreateDirectory(dir);
            using (var ms = new MemoryStream(zip)) using (var z = new ZipArchive(ms, ZipArchiveMode.Read)) z.ExtractToDirectory(dir);
            string nuevo = null;
            foreach (string d in Directory.GetDirectories(dir)) if (d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) nuevo = d;
            if (nuevo == null) throw new InvalidDataException("El .zip no trae el paquete .app");
            Plataforma.HazEjecutable(Ejecutable(nuevo));
            Registro.Apunta("Programa " + version + " descargado: se instala.");
            if (!Plataforma.EsMac) { Registro.Apunta("(En Windows solo se comprueba la descarga: el paquete de Mac no se instala.)"); return; }
            if (pruebas) CarpetaPruebas = datos;
            string destino = pruebas ? AppInstalada : (RutaApp ?? AppInstalada);
            string guion = Path.Combine(dir, "actualiza.sh");
            string abre = pruebas
                ? "\"" + Ejecutable(destino) + "\" --fondo --pruebas \"" + datos + "\" >/dev/null 2>&1 &"
                : "open -a \"" + destino + "\" --args --fondo";
            File.WriteAllText(guion, string.Join("\n", new[] {
                "#!/bin/sh",
                "while kill -0 " + Environment.ProcessId + " 2>/dev/null; do sleep 1; done",
                "rm -rf \"" + destino + ".viejo\"",
                "if [ -d \"" + destino + "\" ]; then mv \"" + destino + "\" \"" + destino + ".viejo\"; fi",
                "mkdir -p \"" + Path.GetDirectoryName(destino) + "\"",
                "mv \"" + nuevo + "\" \"" + destino + "\" && rm -rf \"" + destino + ".viejo\"",
                "xattr -dr com.apple.quarantine \"" + destino + "\" 2>/dev/null",
                abre, "" }));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, ArgumentList = { guion } });
            Programa.Termina();
        }

        // Arranca con el sistema: un LaunchAgent del usuario (se vuelve a apuntar cada vez que arranca, por si el paquete cambió de sitio).
        static string Plist { get { return Path.Combine(Plataforma.Casa, "Library", "LaunchAgents", Etiqueta + ".plist"); } }

        public static void ArranqueConSistema(bool si)
        {
            if (!Plataforma.EsMac || CarpetaPruebas != null) return;
            try
            {
                if (si)
                {
                    string exe = Ejecutable(RutaApp ?? AppInstalada);
                    string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
                        + "<plist version=\"1.0\"><dict>\n<key>Label</key><string>" + Etiqueta + "</string>\n"
                        + "<key>ProgramArguments</key><array><string>" + exe.Replace("&", "&amp;") + "</string><string>--fondo</string></array>\n"
                        + "<key>RunAtLoad</key><true/>\n<key>KeepAlive</key><false/>\n</dict></plist>\n";
                    Directory.CreateDirectory(Path.GetDirectoryName(Plist));
                    if (!File.Exists(Plist) || File.ReadAllText(Plist) != xml)
                    {
                        File.WriteAllText(Plist, xml);
                        Plataforma.Ejecuta("launchctl", "unload", Plist);
                        Plataforma.Ejecuta("launchctl", "load", "-w", Plist);
                    }
                }
                else
                {
                    Plataforma.Ejecuta("launchctl", "unload", "-w", Plist);
                    if (File.Exists(Plist)) File.Delete(Plist);
                    Registro.Apunta("Quitado el arranque con el sistema.");
                }
            }
            catch (Exception e) { Registro.Apunta("No se pudo cambiar el arranque con el sistema: " + e.Message); }
        }

        // Desinstalar (desde el menú del programa): cierra el que esté funcionando, desvincula el PC en la web, quita el addon del
        // juego, el arranque con el sistema, los datos y el paquete de ~/Applications.
        public static async Task Desinstala(string datos, Func<Task<bool>> desvincula)
        {
            Registro.Apunta("Desinstalar pedido.");
            if (!await Dialogo.Pregunta(null, "¿Desinstalar Forever Tracker?\n\nEste Mac se desvinculará de tu cuenta de la web y se quitará también el addon del juego (Interface/AddOns/ForeverTracker). Lo que el juego guardó de tus personajes no se toca.",
                "Desinstalar", "Cancelar")) return;
            Programa.Senal("salir", datos);
            await Task.Delay(1500);
            try { await desvincula(); } catch { }
            try
            {
                var aj = Ajustes.Carga(datos);
                if (aj.Juego != null && Directory.Exists(Juego.CarpetaAddon(aj.Juego))) { Directory.Delete(Juego.CarpetaAddon(aj.Juego), true); Registro.Apunta("Addon quitado de " + Juego.CarpetaAddon(aj.Juego)); }
            }
            catch (Exception e) { Registro.Apunta("No se pudo quitar el addon: " + e.Message); }
            ArranqueConSistema(false);
            try { Directory.Delete(datos, true); } catch { }
            if (Plataforma.EsMac)
            {
                // El paquete no se puede borrar a sí mismo mientras funciona: lo borra un guion un momento después.
                string app = RutaApp ?? AppInstalada;
                string guion = Path.Combine(Path.GetTempPath(), "forevertracker-desinstala.sh");
                File.WriteAllText(guion, "#!/bin/sh\nwhile kill -0 " + Environment.ProcessId + " 2>/dev/null; do sleep 1; done\nrm -rf \"" + app + "\"\nrm -f \"" + guion + "\"\n");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, ArgumentList = { guion } });
            }
            await Dialogo.Avisa(null, "Forever Tracker se ha desinstalado.");
        }
    }
}
