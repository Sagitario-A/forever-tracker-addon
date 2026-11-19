// El trabajo de fondo: vincular el PC, vigilar la carpeta del juego, subir el archivo del addon cuando cambia, mantener el
// addon (y el programa de descarga directa) al día. Todo se ejecuta en el hilo de la interfaz (los avisos de Windows y los
// temporizadores se pasan a él), así el estado no se pisa; leer archivos y hablar con la web no lo bloquean.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // Estado para la ventana
        public bool Vinculado { get { return clave != null; } }
        public string Codigo, UrlVincular, MensajeVincular;
        public bool Vinculando;
        public string Aviso;                 // último problema (sin internet, juego no encontrado…)
        public bool Subiendo;
        public string AddonInstalado, AddonUltimo;
        public string MensajeAddon;          // por qué no se ha podido instalar o poner al día (null si va bien)
        public bool AddonInstalando;
        public string ProgramaNuevo;
        public string EncargosLista;         // la última lista de encargos escrita en el addon y cuándo (para el registro)
        public long EncargosEscrito;

        // Desde la 1.1.0 el addon se instala y se pone al día solo, sin preguntar: en cuanto el PC está vinculado o se ha pulsado
        // «Vincular este PC» en la bienvenida (que dice que lo instala). Antes de eso no se toca la carpeta del juego.
        bool consentimiento;
        bool PuedeAddon { get { return clave != null || consentimiento; } }
        long proximosEncargos;               // cuándo toca volver a bajar la lista de encargos (cada hora)
        bool bajandoEncargos, encargosOtraVez;

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
            bool hablo = await RevisaAddon();
            await BajaEncargos();
            await RevisaPrograma();
            // Sin conexión, se vuelve a mirar en 10 minutos; si no, en 6 horas.
            proximaRevision = Tiempo.Ahora() + (hablo ? 6 * 3600 * 1000L : 10 * 60 * 1000L);
            Recorta();
        }

        // Cada 30 s: reintentos pendientes, el juego si no estaba, cada hora la lista de encargos, y cada 6 horas el addon y el programa.
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
            else if (clave != null && ahora >= proximosEncargos && proximosEncargos > 0)
            {
                var t = BajaEncargos();
            }
            Avisa(); // «hace X minutos»
        }

        async Task RevisaAddonYPrograma()
        {
            if (!await RevisaAddon()) proximaRevision = Tiempo.Ahora() + 10 * 60 * 1000L;
            await BajaEncargos();
            await RevisaPrograma();
            Recorta();
        }

        async Task RevisaAddonYEncargos()
        {
            await RevisaAddon();
            await BajaEncargos();
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

        // Botón «Detectar carpeta»: busca el juego como al arrancar (Battle.net, registro de Windows, carpetas habituales).
        public bool DetectaJuego()
        {
            string j = Juego.Busca();
            return j != null && CambiaJuego(j);
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
            var t = RevisaAddonYEncargos();
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
                // Lo que la web dice de los lotes de la base común (campo «recopilador», desde el 2026-11-18; null si el archivo no trae lotes).
                var rec = J.Obj(r.Json, "recopilador");
                if (rec != null)
                    Registro.Apunta(J.Txt(rec, "error") != null ? "Base común: " + J.Txt(rec, "error")
                        : "Base común: " + J.Num(rec, "lotes") + " lotes en el archivo, " + J.Num(rec, "guardados") + " guardados, " + J.Num(rec, "repetidos") + " que la web ya tenía, "
                          + J.Num(rec, "descartados") + " descartados, " + J.Num(rec, "errores") + " con error" + (J.Si(rec, "recoger") ? "" : " (el interruptor de la cuenta está apagado)") + ".");
                A.Guarda();
                Avisa();
                // Con la subida hecha, la web ya tiene esos lotes: se vuelve a escribir Encargos.lua con «recibidos» al día. Solo si
                // guardó alguno nuevo (desde la 1.1.3): si todo eran repetidos, «recibidos» no cambia (antes se escribía dos veces al vincular).
                if (rec != null && J.Num(rec, "guardados") > 0) await BajaEncargos(true);
                return;
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
            // Pulsar «Vincular este PC» en la bienvenida es aceptar lo que dice: desde ahí el addon se instala solo.
            if (!consentimiento) { consentimiento = true; var t = RevisaAddon(); }
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
                    await RevisaAddonYEncargos();
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
            try { Plataforma.AbreNavegador(url); } catch (Exception e) { Registro.Apunta("No se pudo abrir el navegador: " + e.Message); }
        }

        // ───────── el addon ─────────
        Dictionary<string, object> infoAddon;

        // Mira la versión publicada y, si falta o es vieja, instala o pone al día el addon solo (sin preguntar, desde la 1.1.0).
        // Devuelve si pudo hablar con la web.
        public async Task<bool> RevisaAddon()
        {
            AddonInstalado = A.Juego == null ? null : Juego.VersionAddon(A.Juego); // lo que hay en el juego, con o sin conexión
            var r = await W.Get("/api/programa", null);
            if (!r.Bien)
            {
                Registro.Apunta("No se pudo mirar la versión del addon: " + r.Error);
                if (r.Estado == 0 && Aviso == null) Aviso = r.Error + ". Se volverá a intentar.";
                Avisa();
                return false;
            }
            if (Aviso != null && Aviso.StartsWith("No se puede hablar")) Aviso = null;
            infoAddon = J.Obj(r.Json, "addon");
            programaInfo = Plataforma.InfoPrograma(r.Json);
            AddonUltimo = J.Txt(infoAddon, "version");
            AddonInstalado = A.Juego == null ? null : Juego.VersionAddon(A.Juego);
            if (A.Juego == null || AddonUltimo == null || !PuedeAddon) { Avisa(); return true; }
            if (AddonInstalado != null && Versiones.Compara(AddonInstalado, AddonUltimo) >= 0) { MensajeAddon = null; Avisa(); return true; }
            // Falta o es viejo: se pone solo. Al cambiar la carpeta del addon se pierde Encargos.lua (el .zip trae el de por defecto):
            // quien llama a esto llama después a BajaEncargos y lo vuelve a escribir con la lista de la web.
            await InstalaAddon();
            return true;
        }

        // Devuelve si quedó instalado.
        async Task<bool> InstalaAddon()
        {
            if (infoAddon == null || A.Juego == null || AddonInstalando) return false;
            string url = J.Txt(infoAddon, "url"), sha = J.Txt(infoAddon, "sha256");
            long tam = J.Num(infoAddon, "size");
            AddonInstalando = true; MensajeAddon = null; Avisa();
            try
            {
                byte[] zip = await W.Descarga(url);
                string huella = null;
                if (zip != null) using (var h = SHA256.Create()) huella = Clave.Hex(h.ComputeHash(zip));
                if (zip == null || huella != sha || zip.Length != tam)
                {
                    MensajeAddon = "No se pudo descargar el addon (o llegó dañado). Se volverá a intentar más tarde.";
                    Registro.Apunta(MensajeAddon);
                    return false;
                }
                string juego = A.Juego;
                await Task.Run(() => Juego.InstalaAddon(juego, zip));
                AddonInstalado = Juego.VersionAddon(A.Juego);
                Registro.Apunta("Addon " + AddonInstalado + " instalado en " + Juego.CarpetaAddon(A.Juego) + " (entra en vigor al entrar en el juego o con /reload).");
                return true;
            }
            catch (Exception e)
            {
                MensajeAddon = "No se pudo instalar el addon: " + e.Message;
                Registro.Apunta(MensajeAddon);
                return false;
            }
            finally { AddonInstalando = false; Avisa(); }
        }

        // Botón «Volver a intentar» de la ventana cuando el addon no se pudo instalar.
        public void ReintentaAddon() { var t = RevisaAddonYEncargos(); }

        // ───────── los encargos ─────────
        // La lista de encargos de la cuenta (GET /api/encargos, con la clave; trae «lua», el archivo entero) se escribe en
        // Interface\AddOns\ForeverTracker\Encargos.lua: al arrancar, cada hora, después de instalar o poner al día el addon (la
        // actualización pisa ese archivo con el de por defecto) y después de cada subida que vaya bien (así «recibidos» está al día y
        // el addon borra del archivo los lotes que la web ya tiene; desde la 1.1.3 solo si guardó lotes nuevos). Solo con el PC vinculado y el
        // addon en su carpeta.
        public async Task BajaEncargos(bool trasSubida = false)
        {
            if (clave == null || A.Juego == null || !Directory.Exists(Juego.CarpetaAddon(A.Juego))) return;
            // Ya hay una en marcha: solo se repite al acabar si la pide una subida (la lista pudo cambiar); si no, con la que va basta.
            if (bajandoEncargos) { if (trasSubida) encargosOtraVez = true; return; }
            bajandoEncargos = true;
            try
            {
                do
                {
                    encargosOtraVez = false;
                    var r = await W.Get("/api/encargos", clave);
                    if (r.Anulado) { Desvinculado(); return; }
                    string lua = r.Bien ? r.Texto("lua") : null;
                    if (lua == null)
                    {
                        Registro.Apunta("No se pudo bajar la lista de encargos: " + r.Error + ". Se volverá a intentar en 10 minutos.");
                        proximosEncargos = Tiempo.Ahora() + 10 * 60 * 1000L;
                        return;
                    }
                    try
                    {
                        string juego = A.Juego;
                        await Task.Run(() => Juego.EscribeEncargos(juego, lua));
                        EncargosLista = r.Texto("lista"); EncargosEscrito = Tiempo.Ahora();
                        string rec = r.Texto("recibidos") ?? "";
                        Registro.Apunta("Encargos.lua escrito: lista " + EncargosLista + ", " + J.Lista(r.Json, "grupos").Length + " grupos, " + (rec.Length == 0 ? 0 : rec.Split(',').Length)
                            + " lotes que la web ya tiene, recoger " + (J.Si(r.Json, "recoger") ? "sí" : "no") + ", addon publicado " + r.Texto("addonUltimo") + ".");
                    }
                    catch (Exception e)
                    {
                        Registro.Apunta("No se pudo escribir Encargos.lua: " + e.Message + ". Se volverá a intentar en 10 minutos.");
                        proximosEncargos = Tiempo.Ahora() + 10 * 60 * 1000L;
                        return;
                    }
                    proximosEncargos = Tiempo.Ahora() + 3600 * 1000L;
                } while (encargosOtraVez);
            }
            finally { bajandoEncargos = false; Avisa(); }
        }

        // ───────── el programa (la descarga directa de Windows y el de Mac; el de la Store lo pone al día la Store) ─────────
        Dictionary<string, object> programaInfo;

        async Task RevisaPrograma()
        {
            if (programaInfo == null || !Plataforma.PuedeActualizarse || (Pruebas && !A.ProbarActualizacion)) return;
            string v = J.Txt(programaInfo, "version");
            if (v == null || Versiones.Compara(v, Programa.Version) <= 0) return;
            ProgramaNuevo = v; Avisa();
            byte[] archivo = await W.Descarga(J.Txt(programaInfo, "url"));
            string huella = null;
            if (archivo != null) using (var h = SHA256.Create()) huella = Clave.Hex(h.ComputeHash(archivo));
            if (archivo == null || huella != J.Txt(programaInfo, "sha256")) { Registro.Apunta("La versión nueva del programa no se pudo descargar bien."); return; }
            try { Plataforma.InstalaActualizacion(archivo, v, Datos, Pruebas); }
            catch (Exception e) { Registro.Apunta("No se pudo instalar la versión nueva: " + e.Message); }
        }

        // ───────── memoria ─────────
        // Tras cada subida, se devuelve al sistema la memoria que no se usa (el programa se queda en poco más de nada).
        public static void Recorta()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Plataforma.Recorta();
            }
            catch { }
        }
    }
}
