// El trabajo de fondo: vincular el PC, vigilar la carpeta del juego, subir el archivo del addon cuando cambia, mantener el
// addon (y el programa de descarga directa) al día. Todo se ejecuta en el hilo de la interfaz (los avisos de Windows y los
// temporizadores se pasan a él), así el estado no se pisa; leer archivos y hablar con la web no lo bloquean.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ForeverTracker
{
    public class Agente
    {
        public readonly string Datos;
        public readonly Ajustes A;
        public readonly Web W;
        public readonly bool Pruebas;
        string clave;
        readonly SynchronizationContext ui;

        public event Action Cambio;          // algo ha cambiado: la ventana se repinta
        public Func<string, bool> Pregunta;  // la pone la ventana mientras está abierta (sí/no al usuario)

        // Estado para la ventana
        public bool Vinculado { get { return clave != null; } }
        public string Codigo, UrlVincular, MensajeVincular;
        public bool Vinculando;
        public string Aviso;                 // último problema (sin internet, juego no encontrado…)
        public bool Subiendo;
        public string AddonInstalado, AddonUltimo, MensajeAddon;
        public bool AddonFalta;              // hay que instalar o actualizar y no hay permiso todavía
        public string ProgramaNuevo;

        FileSystemWatcher vigia;
        readonly Dictionary<string, Timer> esperas = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> fallos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> reintentoEn = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> cola = new Queue<string>();
        bool procesando;
        long ultimaSubida;
        int vinculo;                         // número del intento de vincular en curso (para cancelarlo)
        Timer reloj;
        long proximaRevision;

        public Agente(string datos, Ajustes a, string web, bool pruebas)
        {
            Datos = datos; A = a; Pruebas = pruebas;
            W = new Web(web);
            ui = SynchronizationContext.Current;
            clave = Clave.Lee(datos);
        }

        void EnUi(Action f) { ui.Post(_ => { try { f(); } catch (Exception e) { Registro.Apunta("Error: " + e); } }, null); }
        void Avisa() { var c = Cambio; if (c != null) c(); }

        // ───────── arrancar ─────────
        public void Arranca(bool interactivo, bool vincular)
        {
            Registro.Apunta("Arranca la versión " + Programa.Version + (Pruebas ? " (pruebas, " + W.Base + ")" : "") + (interactivo ? "" : " en segundo plano"));
            BuscaJuego();
            reloj = new Timer(_ => EnUi(Tic), null, 30000, 30000);
            var t = Comprueba(interactivo, vincular);
        }

        async Task Comprueba(bool interactivo, bool vincular)
        {
            if (clave != null)
            {
                var r = await W.Get("/api/programa/yo", clave);
                if (r.Anulado) Desvinculado();
                else if (r.Bien) { A.Cuenta = r.Texto("cuenta"); A.Guarda(); }
                Avisa();
            }
            if (clave != null) RevisaTodo(false);
            // Desde la 1.0.2 no se empieza a vincular solo al abrirlo: hay que pulsar «Vincular este PC», con el texto de qué se
            // sube encima (permiso expreso antes de subir nada; norma 10.5.2 de la Microsoft Store). «vincular» es para pruebas.
            else if (vincular) Vincula();
            bool hablo = await RevisaAddon(interactivo);
            await RevisaPrograma();
            // Sin conexión, se vuelve a mirar en 10 minutos; si no, en 6 horas.
            proximaRevision = Tiempo.Ahora() + (hablo ? 6 * 3600 * 1000L : 10 * 60 * 1000L);
            Recorta();
        }

        // Cada 30 s: reintentos pendientes, el juego si no estaba, y cada 6 horas el addon y el programa.
        void Tic()
        {
            long ahora = Tiempo.Ahora();
            if (A.Juego == null || vigia == null) BuscaJuego();
            foreach (var kv in reintentoEn.ToList()) if (kv.Value <= ahora) { reintentoEn.Remove(kv.Key); Encola(kv.Key); }
            if (ahora >= proximaRevision && proximaRevision > 0)
            {
                proximaRevision = ahora + 6 * 3600 * 1000L;
                var t = RevisaAddonYPrograma();
            }
            Avisa(); // «hace X minutos»
        }

        async Task RevisaAddonYPrograma()
        {
            if (!await RevisaAddon(false)) proximaRevision = Tiempo.Ahora() + 10 * 60 * 1000L;
            await RevisaPrograma();
            Recorta();
        }

        // ───────── el juego ─────────
        void BuscaJuego()
        {
            string j = Juego.Valida(A.Juego) ?? (Pruebas ? null : Juego.Busca());
            if (j == null)
            {
                if (A.Juego == null || Aviso == null) Registro.Apunta("No encuentro la carpeta del juego.");
                Aviso = "No encuentro la carpeta de World of Warcraft: Forever. Elígela con «Cambiar carpeta».";
                Avisa();
                return;
            }
            if (!string.Equals(j, A.Juego, StringComparison.OrdinalIgnoreCase)) { A.Juego = j; A.Guarda(); Registro.Apunta("Juego: " + j); }
            if (Aviso != null && Aviso.StartsWith("No encuentro la carpeta")) Aviso = null;
            Vigila();
        }

        public bool CambiaJuego(string carpeta)
        {
            string j = Juego.Valida(carpeta);
            if (j == null) return false;
            A.Juego = j; A.Guarda();
            Registro.Apunta("Carpeta del juego elegida: " + j);
            if (vigia != null) { vigia.Dispose(); vigia = null; }
            BuscaJuego();
            RevisaTodo(false);
            var t = RevisaAddon(true);
            return true;
        }

        // Avisos de Windows en WTF\Account (sin mirar cada poco). Si aún no existe (juego sin estrenar), se mira cada 30 s.
        void Vigila()
        {
            if (vigia != null) return;
            string acc = Path.Combine(A.Juego, "WTF", "Account");
            if (!Directory.Exists(acc)) return;
            try
            {
                vigia = new FileSystemWatcher(acc, "ForeverTracker.lua");
                vigia.IncludeSubdirectories = true;
                vigia.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                vigia.InternalBufferSize = 16384;
                FileSystemEventHandler h = (s, e) => EnUi(() => Cambiado(e.FullPath));
                vigia.Changed += h;
                vigia.Created += h;
                vigia.Renamed += (s, e) => EnUi(() => Cambiado(e.FullPath));
                vigia.Error += (s, e) => EnUi(() => { Registro.Apunta("El aviso de Windows se ha perdido: se revisa todo."); RevisaTodo(false); });
                vigia.EnableRaisingEvents = true;
                Registro.Apunta("Vigilando " + acc);
            }
            catch (Exception e) { Registro.Apunta("No se puede vigilar la carpeta: " + e.Message); vigia = null; }
        }

        // El juego escribe el archivo en varios pasos: se espera a que lleve 2 s sin cambiar.
        void Cambiado(string ruta)
        {
            if (A.Juego == null || !Juego.EsArchivoAddon(A.Juego, ruta)) return;
            Timer t;
            if (esperas.TryGetValue(ruta, out t)) t.Change(2000, Timeout.Infinite);
            else esperas[ruta] = new Timer(_ => EnUi(() => { Timer x; if (esperas.TryGetValue(ruta, out x)) { x.Dispose(); esperas.Remove(ruta); } Encola(ruta); }), null, 2000, Timeout.Infinite);
        }

        // Revisa todos los archivos (al arrancar, al vincular, con «Subir ahora»): sube los que hayan cambiado.
        public void RevisaTodo(bool forzar)
        {
            if (A.Juego == null) return;
            if (forzar) foreach (string f in Juego.ArchivosAddon(A.Juego)) A.Huellas.Remove(f);
            foreach (string f in Juego.ArchivosAddon(A.Juego)) Encola(f);
        }

        // ───────── subir ─────────
        void Encola(string ruta)
        {
            if (!cola.Contains(ruta)) cola.Enqueue(ruta);
            if (!procesando) { var t = Procesa(); }
        }

        async Task Procesa()
        {
            procesando = true;
            try
            {
                while (cola.Count > 0)
                {
                    string ruta = cola.Dequeue();
                    await Sube(ruta);
                }
            }
            finally { procesando = false; Recorta(); }
        }

        async Task Sube(string ruta)
        {
            if (clave == null) return; // se subirá al vincular (RevisaTodo)
            byte[] datos;
            string huella;
            try
            {
                datos = await Task.Run(() => Juego.Lee(ruta));
                if (datos == null || datos.Length == 0) return;
                using (var sha = SHA256.Create()) huella = Clave.Hex(sha.ComputeHash(datos));
            }
            catch (Exception e)
            {
                Registro.Apunta("No se pudo leer " + ruta + ": " + e.Message);
                Reintenta(ruta);
                return;
            }
            string antes;
            if (A.Huellas.TryGetValue(ruta, out antes) && antes == huella) return;
            // La web acepta una subida cada 5 s por cuenta: entre una y otra, 6 s.
            long espera = ultimaSubida + 6000 - Tiempo.Ahora();
            if (espera > 0) await Task.Delay((int)espera);
            Subiendo = true; Avisa();
            Respuesta r = null;
            for (int i = 0; i < 3; i++)
            {
                r = await W.PostArchivo("/api/sync", datos, clave);
                ultimaSubida = Tiempo.Ahora();
                if (r.Estado != 429) break;
                await Task.Delay(7000);
            }
            Subiendo = false;
            string cuenta = Juego.NombreCuenta(ruta);
            if (r.Bien)
            {
                A.Huellas[ruta] = huella;
                A.Subidas[ruta] = Resume(r);
                fallos.Remove(ruta);
                if (Aviso != null && Aviso.StartsWith("No se puede hablar")) Aviso = null;
                Registro.Apunta("Subido " + cuenta + ": " + A.Subidas[ruta].Resumen);
            }
            else if (r.Anulado)
            {
                Desvinculado();
            }
            else if (r.Estado == 0 || r.Estado >= 500 || r.Estado == 429)
            {
                Aviso = r.Error;
                Registro.Apunta("Subida de " + cuenta + " fallida, se reintentará: " + r.Error);
                Reintenta(ruta);
            }
            else
            {
                // El archivo no vale (no es del addon, demasiado grande…): no se vuelve a probar hasta que cambie.
                A.Huellas[ruta] = huella;
                A.Subidas[ruta] = new Subida { Cuando = Tiempo.Ahora(), Bien = false, Resumen = r.Error };
                Registro.Apunta("Subida de " + cuenta + " rechazada: " + r.Error);
            }
            A.Guarda();
            Avisa();
        }

        // Reintentos cada vez más espaciados: 30 s, 1, 2, 5, 10 y 30 minutos.
        void Reintenta(string ruta)
        {
            int n;
            fallos.TryGetValue(ruta, out n);
            fallos[ruta] = n + 1;
            int[] s = { 30, 60, 120, 300, 600, 1800 };
            reintentoEn[ruta] = Tiempo.Ahora() + s[Math.Min(n, s.Length - 1)] * 1000L;
        }

        // Lo que dice la web de cada personaje, en una línea: «Nombre Apellido: Actualizado.»
        static Subida Resume(Respuesta r)
        {
            var partes = new List<string>();
            bool bien = false;
            foreach (object o in J.Lista(r.Json, "results"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string estado = J.Txt(d, "status");
                if (estado == "ok" || estado == "viejo") bien = true;
                partes.Add(J.Txt(d, "name") + ": " + J.Txt(d, "message"));
            }
            return new Subida { Cuando = Tiempo.Ahora(), Bien = bien, Resumen = partes.Count > 0 ? string.Join(" · ", partes) : "Subido." };
        }

        // ───────── vincular ─────────
        public async void Vincula()
        {
            int yo = ++vinculo;
            string nueva = Clave.Nueva();
            Vinculando = true; Codigo = null; MensajeVincular = "Preparando…"; Avisa();
            var r = await W.PostJson("/api/programa/vincular", new Dictionary<string, object> {
                { "huella", Clave.Huella(nueva) }, { "pc", Environment.MachineName }, { "version", Programa.Version } });
            if (yo != vinculo) return;
            if (!r.Bien) { Vinculando = false; MensajeVincular = r.Error; Avisa(); return; }
            string id = r.Texto("id");
            Codigo = r.Texto("codigo");
            UrlVincular = r.Texto("url");
            MensajeVincular = null;
            Registro.Apunta("Vincular: " + UrlVincular + " código " + Codigo);
            AbreNavegador(UrlVincular);
            Avisa();
            long hasta = Tiempo.Ahora() + 11 * 60 * 1000L;
            while (Tiempo.Ahora() < hasta)
            {
                await Task.Delay(3000);
                if (yo != vinculo) return;
                var p = await W.Get("/api/programa/vincular/" + id, nueva);
                if (yo != vinculo) return;
                string estado = p.Texto("estado");
                if (estado == null || estado == "pendiente") continue;
                Vinculando = false; Codigo = null;
                if (estado == "aceptado")
                {
                    clave = nueva;
                    Clave.Guarda(Datos, clave);
                    A.Cuenta = p.Texto("cuenta");
                    A.Huellas.Clear(); // con otra cuenta, todo se vuelve a subir
                    A.Guarda();
                    MensajeVincular = null; Aviso = null;
                    Registro.Apunta("Vinculado a la cuenta " + A.Cuenta);
                    Avisa();
                    RevisaTodo(false);
                    await RevisaAddon(true);
                }
                else
                {
                    MensajeVincular = estado == "rechazado" ? "Has dicho que no en la web. Si fue sin querer, vuelve a pulsar «Vincular este PC»." : "El enlace ha caducado. Vuelve a pulsar «Vincular este PC».";
                    Avisa();
                }
                return;
            }
            Vinculando = false; Codigo = null; MensajeVincular = "El enlace ha caducado. Vuelve a pulsar «Vincular este PC»."; Avisa();
        }

        public void CancelaVincular() { vinculo++; Vinculando = false; Codigo = null; MensajeVincular = null; Avisa(); }

        public async Task Desvincula()
        {
            if (clave != null) { var r = await W.Delete("/api/programa/yo", clave); Registro.Apunta("Desvincular en la web: " + (r.Bien ? "hecho" : r.Error)); }
            clave = null;
            Clave.Borra(Datos);
            A.Cuenta = null; A.Huellas.Clear(); A.Guarda();
            Registro.Apunta("Desvinculado desde el programa.");
            Avisa();
        }

        // La web dice que la clave ya no vale (anulada en Ajustes).
        void Desvinculado()
        {
            Registro.Apunta("La web dice que este PC ya no está vinculado.");
            clave = null;
            Clave.Borra(Datos);
            A.Cuenta = null; A.Huellas.Clear(); A.Guarda();
            MensajeVincular = "Este PC se ha desvinculado desde la web. Para volver a subir tus datos, vincúlalo otra vez.";
            Avisa();
        }

        public static void AbreNavegador(string url)
        {
            if (url == null || !(url.StartsWith("https://") || url.StartsWith("http://127.0.0.1") || url.StartsWith("http://localhost"))) return;
            if (Environment.GetEnvironmentVariable("FT_SIN_NAVEGADOR") == "1") return; // pruebas automáticas
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception e) { Registro.Apunta("No se pudo abrir el navegador: " + e.Message); }
        }

        // ───────── el addon ─────────
        Dictionary<string, object> infoAddon;

        // Devuelve si pudo hablar con la web.
        public async Task<bool> RevisaAddon(bool preguntar)
        {
            AddonInstalado = A.Juego == null ? null : Juego.VersionAddon(A.Juego); // lo que hay en el juego, con o sin conexión
            var r = await W.Get("/api/programa", null);
            if (!r.Bien)
            {
                Registro.Apunta("No se pudo mirar la versión del addon: " + r.Error);
                if (r.Estado == 0 && Aviso == null) Aviso = r.Error + ". Se volverá a intentar.";
                MensajeAddon = "No se ha podido comprobar si hay una versión nueva.";
                Avisa();
                return false;
            }
            if (Aviso != null && Aviso.StartsWith("No se puede hablar")) Aviso = null;
            infoAddon = J.Obj(r.Json, "addon");
            programaInfo = J.Obj(r.Json, "programa");
            AddonUltimo = J.Txt(infoAddon, "version");
            AddonInstalado = A.Juego == null ? null : Juego.VersionAddon(A.Juego);
            AddonFalta = false;
            MensajeAddon = null;
            if (A.Juego == null || AddonUltimo == null) { Avisa(); return true; }
            if (AddonInstalado != null && Versiones.Compara(AddonInstalado, AddonUltimo) >= 0) { Avisa(); return true; }
            // Falta o es viejo: con permiso, se pone solo; la primera vez se pregunta (si la ventana está abierta).
            if (A.AddonPermiso == null && preguntar && Pregunta != null)
            {
                bool si = Pregunta(AddonInstalado == null
                    ? "El addon Forever Tracker no está instalado en tu juego. Es el que guarda tus datos del juego.\n\n¿Lo instalo ahora? A partir de ahora lo mantendré al día yo solo."
                    : "Hay una versión nueva del addon Forever Tracker (" + AddonUltimo + "; tienes la " + AddonInstalado + ").\n\n¿La instalo? A partir de ahora lo mantendré al día yo solo.");
                A.AddonPermiso = si ? "si" : "no";
                A.Guarda();
            }
            if (A.AddonPermiso == "si") await InstalaAddon();
            else { AddonFalta = true; Avisa(); }
            return true;
        }

        public async Task InstalaAddon()
        {
            if (infoAddon == null || A.Juego == null) return;
            string url = J.Txt(infoAddon, "url"), sha = J.Txt(infoAddon, "sha256");
            long tam = J.Num(infoAddon, "size");
            MensajeAddon = "Descargando el addon…"; Avisa();
            byte[] zip = await W.Descarga(url);
            string huella = null;
            if (zip != null) using (var h = SHA256.Create()) huella = Clave.Hex(h.ComputeHash(zip));
            if (zip == null || huella != sha || zip.Length != tam)
            {
                MensajeAddon = "No se pudo descargar el addon (o llegó dañado). Se volverá a intentar más tarde.";
                Registro.Apunta(MensajeAddon);
                Avisa();
                return;
            }
            try
            {
                string juego = A.Juego;
                await Task.Run(() => Juego.InstalaAddon(juego, zip));
                AddonInstalado = Juego.VersionAddon(A.Juego);
                AddonFalta = false;
                MensajeAddon = "Addon " + AddonInstalado + " instalado. Se nota al entrar en el juego o con /reload.";
                Registro.Apunta("Addon " + AddonInstalado + " instalado en " + Juego.CarpetaAddon(A.Juego));
            }
            catch (Exception e)
            {
                MensajeAddon = "No se pudo instalar el addon: " + e.Message;
                Registro.Apunta(MensajeAddon);
            }
            Avisa();
        }

        public void PermiteAddon() { A.AddonPermiso = "si"; A.Guarda(); var t = InstalaAddon(); }

        // ───────── el programa (solo la descarga directa; el de la Store lo pone al día la Store) ─────────
        Dictionary<string, object> programaInfo;

        async Task RevisaPrograma()
        {
            if (programaInfo == null || Instalacion.EsPaquete() || (Pruebas && !A.ProbarActualizacion)) return;
            string v = J.Txt(programaInfo, "version");
            if (v == null || Versiones.Compara(v, Programa.Version) <= 0) return;
            ProgramaNuevo = v; Avisa();
            byte[] exe = await W.Descarga(J.Txt(programaInfo, "url"));
            string huella = null;
            if (exe != null) using (var h = SHA256.Create()) huella = Clave.Hex(h.ComputeHash(exe));
            if (exe == null || huella != J.Txt(programaInfo, "sha256")) { Registro.Apunta("La versión nueva del programa no se pudo descargar bien."); return; }
            string dir = Path.Combine(Datos, "actualizacion");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "ForeverTracker-" + v + ".exe");
            File.WriteAllBytes(f, exe);
            Registro.Apunta("Programa " + v + " descargado: se instala.");
            // El nuevo se instala solo (--actualizar): cierra este, se copia en su sitio y arranca.
            Process.Start(new ProcessStartInfo(f, Pruebas ? "--actualizar --pruebas \"" + Datos + "\"" : "--actualizar") { UseShellExecute = false });
        }

        // ───────── memoria ─────────
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr proceso);

        // Tras cada subida, se devuelve a Windows la memoria que no se usa (el programa se queda en poco más de nada).
        public static void Recorta()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }
    }
}
