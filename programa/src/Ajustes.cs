// Ajustes del programa, la clave cifrada y el registro de lo que pasa. Todo en la carpeta de datos del usuario
// (%LOCALAPPDATA%\ForeverTracker en Windows, ~/Library/Application Support/ForeverTracker en Mac, o la de --pruebas). C# 5 (el
// compilador que trae Windows): sin $"", ?. ni nameof. Desde la 1.1.4 se compila también en el programa de Mac.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ForeverTracker
{
    // Lo último que se subió de un archivo del addon.
    public class Subida
    {
        public long Cuando;        // milisegundos desde 1970 (UTC)
        public bool Bien;
        public string Resumen;     // en palabras, para la ventana
    }

    public class Ajustes
    {
        public string Juego;                 // carpeta _forever_ del juego
        public string Web;                   // dirección de la web (solo en modo de pruebas)
        public string Cuenta;                // nombre de la cuenta vinculada, para enseñarlo
        // (Hasta la 1.0.3 había «AddonPermiso»: desde la 1.1.0 el addon se instala y se pone al día solo, sin preguntar. Si un
        // ajustes.json viejo lo trae, se ignora.)
        public bool ProbarActualizacion;     // solo en modo de pruebas (desde la 1.0.1): actualizarse también contra la web de pruebas
        public string CapturaCarpeta;        // solo en modo de pruebas, para las capturas de la Store: la carpeta que se enseña y sin la marca de pruebas
        public bool InicioDecidido;          // ya no se usa (1.0.0 sin publicar: arranca siempre con Windows); se deja por ajustes viejos
        public Dictionary<string, string> Huellas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Subida> Subidas = new Dictionary<string, Subida>(StringComparer.OrdinalIgnoreCase);

        public string Archivo;

        public static Ajustes Carga(string carpeta)
        {
            string f = Path.Combine(carpeta, "ajustes.json");
            Ajustes a = null;
            try
            {
                if (File.Exists(f)) a = Desde(Json.Objeto(File.ReadAllText(f, Encoding.UTF8)));
            }
            catch (Exception e) { Registro.Apunta("Ajustes ilegibles, se empiezan de nuevo: " + e.Message); }
            if (a == null) a = new Ajustes();
            if (a.Huellas == null) a.Huellas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            else a.Huellas = new Dictionary<string, string>(a.Huellas, StringComparer.OrdinalIgnoreCase);
            if (a.Subidas == null) a.Subidas = new Dictionary<string, Subida>(StringComparer.OrdinalIgnoreCase);
            else a.Subidas = new Dictionary<string, Subida>(a.Subidas, StringComparer.OrdinalIgnoreCase);
            a.Archivo = f;
            return a;
        }

        // Los mismos nombres de campo que escribía la 1.0.0 (un ajustes.json viejo se lee igual).
        Dictionary<string, object> Hacia()
        {
            var subidas = new Dictionary<string, object>();
            foreach (var kv in Subidas)
                subidas[kv.Key] = new Dictionary<string, object> { { "Cuando", kv.Value.Cuando }, { "Bien", kv.Value.Bien }, { "Resumen", kv.Value.Resumen } };
            return new Dictionary<string, object> {
                { "Juego", Juego }, { "Web", Web }, { "Cuenta", Cuenta }, { "ProbarActualizacion", ProbarActualizacion },
                { "CapturaCarpeta", CapturaCarpeta }, { "InicioDecidido", InicioDecidido },
                { "Huellas", new Dictionary<string, string>(Huellas) }, { "Subidas", subidas } };
        }

        static Ajustes Desde(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var a = new Ajustes();
            a.Juego = J.Txt(d, "Juego"); a.Web = J.Txt(d, "Web"); a.Cuenta = J.Txt(d, "Cuenta");
            a.ProbarActualizacion = J.Si(d, "ProbarActualizacion"); a.CapturaCarpeta = J.Txt(d, "CapturaCarpeta"); a.InicioDecidido = J.Si(d, "InicioDecidido");
            var h = J.Obj(d, "Huellas");
            if (h != null) foreach (var kv in h) if (kv.Value is string) a.Huellas[kv.Key] = (string)kv.Value;
            var s = J.Obj(d, "Subidas");
            if (s != null)
                foreach (var kv in s)
                {
                    var x = kv.Value as Dictionary<string, object>;
                    if (x != null) a.Subidas[kv.Key] = new Subida { Cuando = J.Num(x, "Cuando"), Bien = J.Si(x, "Bien"), Resumen = J.Txt(x, "Resumen") };
                }
            return a;
        }

        public void Guarda()
        {
            lock (this)
            {
                try
                {
                    string tmp = Archivo + ".nuevo";
                    File.WriteAllText(tmp, Json.Escribe(Hacia()), new UTF8Encoding(false));
                    if (File.Exists(Archivo)) File.Replace(tmp, Archivo, null);
                    else File.Move(tmp, Archivo);
                }
                catch (Exception e) { Registro.Apunta("No se pudieron guardar los ajustes: " + e.Message); }
            }
        }
    }

    // La clave del PC: la crea el programa (32 bytes al azar) y se guarda protegida como diga la plataforma (en Windows cifrada con
    // DPAPI, solo para este usuario en este PC; en Mac, solo legible por el usuario). La web solo conoce su huella.
    public static class Clave
    {
        public static string Nueva()
        {
            byte[] b = new byte[32];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static string Huella(string clave)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.ASCII.GetBytes(clave)));
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static string Lee(string carpeta)
        {
            string f = Path.Combine(carpeta, "clave.bin");
            try
            {
                if (!File.Exists(f)) return null;
                byte[] b = Plataforma.DesprotegeClave(File.ReadAllBytes(f));
                string k = Encoding.ASCII.GetString(b);
                return k.Length == 43 ? k : null;
            }
            catch (Exception e) { Registro.Apunta("No se pudo leer la clave: " + e.Message); return null; }
        }

        public static void Guarda(string carpeta, string clave)
        {
            string f = Path.Combine(carpeta, "clave.bin");
            File.WriteAllBytes(f, Plataforma.ProtegeClave(Encoding.ASCII.GetBytes(clave)));
        }

        public static void Borra(string carpeta)
        {
            try { File.Delete(Path.Combine(carpeta, "clave.bin")); } catch { }
        }
    }

    // Registro de lo que pasa (registro.txt en la carpeta de datos; como mucho 256 KB y el anterior en registro.txt.1).
    public static class Registro
    {
        static readonly object Cerrojo = new object();
        public static string Archivo;

        public static void Apunta(string msg)
        {
            if (Archivo == null) return;
            try
            {
                lock (Cerrojo)
                {
                    var fi = new FileInfo(Archivo);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string viejo = Archivo + ".1";
                        if (File.Exists(viejo)) File.Delete(viejo);
                        File.Move(Archivo, viejo);
                    }
                    File.AppendAllText(Archivo, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    public static class Tiempo
    {
        static readonly DateTime Origen = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long Ahora() { return (long)(DateTime.UtcNow - Origen).TotalMilliseconds; }

        // «hace 5 minutos», como en la web.
        public static string Hace(long ms)
        {
            long m = Math.Max(0, (Ahora() - ms) / 60000);
            if (m < 1) return "hace un momento";
            if (m < 60) return "hace " + m + (m == 1 ? " minuto" : " minutos");
            long h = (m + 30) / 60;
            if (h < 24) return "hace " + h + (h == 1 ? " hora" : " horas");
            long d = (h + 12) / 24;
            return "hace " + d + (d == 1 ? " día" : " días");
        }
    }
}
