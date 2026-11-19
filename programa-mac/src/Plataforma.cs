// Lo que cambia entre Windows y Mac, en un solo sitio. Este es el de Mac (desde la 1.1.4); el programa de Windows trae el suyo con
// las mismas funciones, y el resto del código (Agente, Web, Juego, Ajustes, Json) es el mismo en los dos. Para probarlo sin un Mac,
// este programa también arranca en Windows (modo de pruebas): lo que es solo de Mac se salta y lo apunta en el registro.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ForeverTracker
{
    public static class Plataforma
    {
        public const string Nombre = "Mac";
        public static readonly bool EsMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        public static string Arquitectura { get { return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64"; } }
        public static string Casa { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        // La clave del PC se guarda tal cual en la carpeta de datos, que es solo del usuario (permisos 700; Programa.CarpetaDatos).
        // En Mac no hay un DPAPI como el de Windows; el llavero exige diálogos que no queremos. (En Windows, solo para pruebas.)
        public static byte[] ProtegeClave(byte[] b) { return b; }
        public static byte[] DesprotegeClave(byte[] b) { return b; }

        // Dónde puede estar el juego en Mac: Aplicaciones (la carpeta de siempre y la del usuario), y lo que apunte Battle.net.
        public static IEnumerable<string> CandidatasJuego()
        {
            var c = new List<string>();
            if (!EsMac) return c;
            foreach (string f in new[] { Path.Combine(Casa, "Library", "Application Support", "Battle.net", "Battle.net.config"), "/Users/Shared/Battle.net/Agent/product.db" })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    string t = Encoding.UTF8.GetString(File.ReadAllBytes(f));
                    foreach (Match m in Regex.Matches(t, @"/[^\x00-\x1f""<>|?*]{1,200}?World of Warcraft[^\x00-\x1f""<>|?*]{0,100}"))
                        c.Add(m.Value);
                }
                catch (Exception e) { Registro.Apunta(Path.GetFileName(f) + ": " + e.Message); }
            }
            c.Add("/Applications/World of Warcraft");
            c.Add(Path.Combine(Casa, "Applications", "World of Warcraft"));
            c.Add("/Applications/Games/World of Warcraft");
            c.Add("/Applications/Blizzard/World of Warcraft");
            return c;
        }

        public static void AbreNavegador(string url)
        {
            if (EsMac) Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        // El programa se pone al día solo (baja el .zip de su arquitectura y cambia el paquete .app).
        public static bool PuedeActualizarse { get { return true; } }

        // Qué parte de GET /api/programa describe a este programa: «mac» → {version, url, sha256, size, arm64: {url, sha256, size},
        // x64: {…}}. Desde la 1.1.5 el paquete es universal (los dos chips) y va en «url»; «arm64» y «x64» siguen (apuntan al mismo
        // archivo) para la 1.1.4, que leía el de su chip. Se devuelve con la forma de «programa» (version, url, sha256, size) para que
        // el agente lo trate igual que en Windows.
        public static Dictionary<string, object> InfoPrograma(Dictionary<string, object> json)
        {
            var mac = J.Obj(json, "mac");
            if (mac == null) return null;
            var a = J.Txt(mac, "url") != null ? mac : J.Obj(mac, Arquitectura);
            if (a == null) return null;
            return new Dictionary<string, object> { { "version", J.Txt(mac, "version") }, { "url", J.Txt(a, "url") }, { "sha256", J.Txt(a, "sha256") }, { "size", J.Num(a, "size") } };
        }

        public static void InstalaActualizacion(byte[] zip, string version, string datos, bool pruebas)
        {
            Instalacion.Actualiza(zip, version, datos, pruebas);
        }

        // En Mac no hace falta devolver memoria a mano (basta con el recolector).
        public static void Recorta() { }

        // ───────── utilidades de Mac ─────────
        // Ejecuta un programa del sistema y espera a que acabe; devuelve lo que escribió (o null si falló) y apunta los fallos.
        public static string Ejecuta(string programa, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(programa) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (string a in args) psi.ArgumentList.Add(a);
                using (var p = Process.Start(psi))
                {
                    string salida = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0) { Registro.Apunta(programa + " " + string.Join(" ", args) + " → " + p.ExitCode + " " + error.Trim()); return null; }
                    return salida;
                }
            }
            catch (Exception e) { Registro.Apunta(programa + ": " + e.Message); return null; }
        }

        public static void HazEjecutable(string ruta)
        {
            if (!EsMac || !File.Exists(ruta)) return;
            try
            {
                File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception e) { Registro.Apunta("No se pudo marcar como ejecutable " + ruta + ": " + e.Message); }
        }

        // Los ejecutables de un paquete (por si al descomprimir se perdió el permiso): el lanzador y el programa de cada chip.
        public static void HazEjecutables(string app)
        {
            HazEjecutable(Path.Combine(app, "Contents", "MacOS", "ForeverTracker"));
            string helpers = Path.Combine(app, "Contents", "Helpers");
            if (!Directory.Exists(helpers)) return;
            foreach (string d in Directory.GetDirectories(helpers))
            {
                HazEjecutable(Path.Combine(d, "Forever Tracker.app", "Contents", "MacOS", "ForeverTracker"));
                HazEjecutable(Path.Combine(d, "Forever Tracker.app", "Contents", "MacOS", "createdump"));
            }
        }

        // Solo el usuario puede entrar en la carpeta (la clave del PC va dentro).
        public static void SoloUsuario(string carpeta)
        {
            if (!EsMac) return;
            try { File.SetUnixFileMode(carpeta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        }
    }
}
