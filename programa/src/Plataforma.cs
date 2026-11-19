// Lo que cambia entre Windows y Mac, en un solo sitio (desde la 1.1.4). Este es el de Windows; el programa de Mac trae el suyo con
// las mismas funciones, y el resto del código (Agente, Web, Juego, Ajustes, Json) es el mismo en los dos.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForeverTracker
{
    public static class Plataforma
    {
        public const string Nombre = "Windows";

        // La clave del PC se guarda cifrada con Windows (DPAPI: solo este usuario en este PC).
        static readonly byte[] Extra = Encoding.ASCII.GetBytes("ForeverTracker-clave-1");
        public static byte[] ProtegeClave(byte[] b) { return ProtectedData.Protect(b, Extra, DataProtectionScope.CurrentUser); }
        public static byte[] DesprotegeClave(byte[] b) { return ProtectedData.Unprotect(b, Extra, DataProtectionScope.CurrentUser); }

        // Dónde puede estar el juego: Battle.net (product.db), el registro de Windows y las carpetas de siempre en cada disco.
        public static IEnumerable<string> CandidatasJuego()
        {
            var candidatas = new List<string>();
            try
            {
                string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent", "product.db");
                if (File.Exists(db))
                {
                    // product.db es un formato binario de Blizzard; basta con sacar los textos que parecen carpetas.
                    string t = Encoding.UTF8.GetString(File.ReadAllBytes(db));
                    foreach (Match m in Regex.Matches(t, @"[A-Za-z]:[\\/][^\x00-\x1f""<>|?*]{1,200}"))
                        candidatas.Add(m.Value.Replace('/', '\\'));
                }
            }
            catch (Exception e) { Registro.Apunta("product.db: " + e.Message); }
            foreach (var vista in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, vista))
                    {
                        using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft"))
                            if (k != null) candidatas.Add(k.GetValue("InstallLocation") as string);
                        using (var k = hk.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\World of Warcraft"))
                            if (k != null) candidatas.Add(k.GetValue("InstallPath") as string);
                    }
                }
                catch { }
            }
            foreach (var disco in DriveInfo.GetDrives())
            {
                try
                {
                    if (disco.DriveType != DriveType.Fixed || !disco.IsReady) continue;
                    string r = disco.RootDirectory.FullName;
                    candidatas.Add(Path.Combine(r, @"Program Files (x86)\World of Warcraft"));
                    candidatas.Add(Path.Combine(r, @"Program Files\World of Warcraft"));
                    candidatas.Add(Path.Combine(r, "World of Warcraft"));
                    candidatas.Add(Path.Combine(r, @"Games\World of Warcraft"));
                    candidatas.Add(Path.Combine(r, @"Juegos\World of Warcraft"));
                    candidatas.Add(Path.Combine(r, @"Battle.net\World of Warcraft"));
                }
                catch { }
            }
            return candidatas;
        }

        public static void AbreNavegador(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        // El programa se pone al día solo (solo la descarga directa; el de la Store lo pone al día la Store).
        public static bool PuedeActualizarse { get { return !Instalacion.EsPaquete(); } }

        // Qué parte de GET /api/programa describe a este programa (en Windows, «programa»; en Mac, «mac»).
        public static Dictionary<string, object> InfoPrograma(Dictionary<string, object> json) { return J.Obj(json, "programa"); }

        // Con la versión nueva ya descargada y comprobada: el nuevo se instala solo (--actualizar), cierra este, se copia en su sitio y arranca.
        public static void InstalaActualizacion(byte[] archivo, string version, string datos, bool pruebas)
        {
            string dir = Path.Combine(datos, "actualizacion");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "ForeverTracker-" + version + ".exe");
            File.WriteAllBytes(f, archivo);
            Registro.Apunta("Programa " + version + " descargado: se instala.");
            Process.Start(new ProcessStartInfo(f, pruebas ? "--actualizar --pruebas \"" + datos + "\"" : "--actualizar") { UseShellExecute = false });
        }

        // Tras cada subida, se devuelve a Windows la memoria que no se usa (el programa se queda en poco más de nada).
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr proceso);
        public static void Recorta()
        {
            try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
        }
    }
}
