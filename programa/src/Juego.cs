// Encontrar la carpeta de Forever, sus archivos del addon e instalar o poner al día el addon.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForeverTracker
{
    public static class Juego
    {
        public const string Sub = "_forever_";

        // La carpeta _forever_ a partir de lo que elija el usuario (vale también la de World of Warcraft, o una de dentro).
        public static string Valida(string carpeta)
        {
            if (string.IsNullOrWhiteSpace(carpeta)) return null;
            try
            {
                var d = new DirectoryInfo(carpeta.Trim().Trim('"'));
                for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                {
                    if (!d.Exists) continue;
                    if (d.Name.Equals(Sub, StringComparison.OrdinalIgnoreCase) && Parece(d.FullName)) return d.FullName;
                    string dentro = Path.Combine(d.FullName, Sub);
                    if (Directory.Exists(dentro) && Parece(dentro)) return dentro;
                }
            }
            catch { }
            return null;
        }

        // Una carpeta de juego tiene Interface, WTF o un .exe.
        static bool Parece(string f)
        {
            try
            {
                return Directory.Exists(Path.Combine(f, "WTF")) || Directory.Exists(Path.Combine(f, "Interface")) || Directory.GetFiles(f, "*.exe").Length > 0;
            }
            catch { return false; }
        }

        // Busca el juego: Battle.net (product.db), el registro de Windows y las carpetas de siempre en cada disco.
        public static string Busca()
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
            foreach (string c in candidatas)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                string v = Valida(c);
                if (v != null) return v;
            }
            return null;
        }

        // Los archivos del addon de todas las cuentas: WTF\Account\<cuenta>\SavedVariables\ForeverTracker.lua (el de cada
        // personaje, más abajo, tiene el mismo nombre pero es otro: no se sube).
        public static bool EsArchivoAddon(string juego, string ruta)
        {
            if (!Path.GetFileName(ruta).Equals("ForeverTracker.lua", StringComparison.OrdinalIgnoreCase)) return false;
            string sv = Path.GetDirectoryName(ruta);
            if (sv == null || !Path.GetFileName(sv).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) return false;
            string cuenta = Path.GetDirectoryName(sv);
            string account = cuenta == null ? null : Path.GetDirectoryName(cuenta);
            return account != null && string.Equals(Path.GetFullPath(account).TrimEnd('\\'), Path.GetFullPath(Path.Combine(juego, "WTF", "Account")).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> ArchivosAddon(string juego)
        {
            var out_ = new List<string>();
            try
            {
                string acc = Path.Combine(juego, "WTF", "Account");
                if (!Directory.Exists(acc)) return out_;
                foreach (string cuenta in Directory.GetDirectories(acc))
                {
                    string f = Path.Combine(cuenta, "SavedVariables", "ForeverTracker.lua");
                    if (File.Exists(f)) out_.Add(f);
                }
            }
            catch (Exception e) { Registro.Apunta("No se pudo mirar WTF\\Account: " + e.Message); }
            return out_;
        }

        // Nombre corto para la ventana: la cuenta de WoW de la carpeta («ABC123#1»).
        public static string NombreCuenta(string archivo)
        {
            try { return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(archivo))); } catch { return archivo; }
        }

        // Leer el archivo dejando que el juego lo siga escribiendo o lo cambie (compartido para leer, escribir y borrar).
        public static byte[] Lee(string ruta)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var ms = new MemoryStream();
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (FileNotFoundException) { return null; }
                catch (IOException)
                {
                    if (i >= 5) throw;
                    System.Threading.Thread.Sleep(400);
                }
            }
        }

        // ───────── el addon ─────────
        public static string CarpetaAddon(string juego) { return Path.Combine(juego, "Interface", "AddOns", "ForeverTracker"); }

        public static string VersionAddon(string juego)
        {
            try
            {
                string toc = Path.Combine(CarpetaAddon(juego), "ForeverTracker.toc");
                if (!File.Exists(toc)) return null;
                var m = Regex.Match(File.ReadAllText(toc), @"^## Version:\s*(\d+\.\d+\.\d+)", RegexOptions.Multiline);
                return m.Success ? m.Groups[1].Value : "0.0.0";
            }
            catch { return null; }
        }

        // Instala el addon del .zip (que trae la carpeta ForeverTracker/). Se descomprime aparte y se cambia de golpe: si algo
        // falla, el addon que había se queda como estaba.
        public static void InstalaAddon(string juego, byte[] zip)
        {
            string addons = Path.Combine(juego, "Interface", "AddOns");
            Directory.CreateDirectory(addons);
            string nuevo = Path.Combine(addons, ".ForeverTracker.nuevo");
            string viejo = Path.Combine(addons, ".ForeverTracker.viejo");
            string destino = CarpetaAddon(juego);
            BorraCarpeta(nuevo);
            BorraCarpeta(viejo);
            Directory.CreateDirectory(nuevo);
            using (var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                foreach (var e in z.Entries)
                {
                    if (!e.FullName.StartsWith("ForeverTracker/", StringComparison.Ordinal) || e.FullName.EndsWith("/")) continue;
                    string rel = e.FullName.Substring("ForeverTracker/".Length);
                    if (rel.Contains("..") || rel.Contains(":") || rel.StartsWith("/") || rel.StartsWith("\\")) throw new InvalidDataException("Ruta rara en el .zip: " + e.FullName);
                    string f = Path.Combine(nuevo, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    e.ExtractToFile(f, true);
                }
            }
            if (!File.Exists(Path.Combine(nuevo, "ForeverTracker.toc"))) { BorraCarpeta(nuevo); throw new InvalidDataException("El .zip no trae ForeverTracker.toc"); }
            if (Directory.Exists(destino)) Directory.Move(destino, viejo);
            try { Directory.Move(nuevo, destino); }
            catch
            {
                if (Directory.Exists(viejo) && !Directory.Exists(destino)) Directory.Move(viejo, destino);
                throw;
            }
            BorraCarpeta(viejo);
        }

        static void BorraCarpeta(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception e) { Registro.Apunta("No se pudo borrar " + d + ": " + e.Message); }
        }
    }
}
