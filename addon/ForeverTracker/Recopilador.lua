-- Forever Tracker: el motor del recopilador (desde la 1.3.0). Diseño: diseno-addon-1.3.md.
--
-- Qué hace: junta en «lotes» lo que el jugador ve jugando (Mundo.lua) y las fichas que la web le encarga pedir al servidor
-- (Encargos.lua), con el contexto de cada cosa (quién, cuándo, dónde, con qué nivel), lo comprime con el compresor del juego
-- y lo deja en ForeverTrackerRecopilador, dentro del mismo archivo ForeverTracker.lua que ya sube el programa de Windows.
-- Nunca escribe en el chat (salvo el aviso de versión, apartado 10 del diseño). Solo lee: no hace nada por el jugador.
--
-- Lo que deja en el archivo:
--   ForeverTrackerRecopilador = { formato = 1, addon = "1.3.0", interruptor = true, versionVista = "1.3.0",
--     lotes = { { id, sesion, clase, inicio, fin, bytes, datos = "<JSON del lote, gzip, base64>" }, … } }
-- Cada lote (descomprimido) es un JSON con la cabecera (juego, quien, sesion…) y la lista «obs»: cada observación es una lista
-- [familia, hora, …campos en el orden de R.CAMPOS[familia]…]. En esas listas, false = no observado (el JSON del juego no
-- admite huecos), lista vacía o "" = observado vacío, "#secreto" = el juego lo tapa; los sí/no van siempre como 1/0 (b01).
local ADDON, FT = ...
local R = {}
FT.R = R

local FORMATO_LOTE = 1
local VERSION = "1.3.2"
do
	local get = (C_AddOns and C_AddOns.GetAddOnMetadata) or GetAddOnMetadata
	local ok, v = pcall(get, ADDON, "Version")
	if ok and type(v) == "string" and v ~= "" then VERSION = v end
end
R.VERSION = VERSION

-- Constantes del diseño (apartados 4, 8 y 10). Las que la web puede cambiar llegan por Encargos.lua.
local ARRANQUE = 60            -- segundos tras entrar antes de empezar los encargos (regla 2)
local CADA = 5                 -- segundos entre tandas
local BLOQUE = 250             -- observaciones por lote (regla 12)
local ESPERA_MAX = 60          -- segundos sin respuesta del servidor → «sin respuesta» (regla 4)
local RETARDO_MISION = 1       -- las misiones se leen un segundo después del aviso (regla 5)
local RITMO_DEFECTO, RITMO_MAX = 25, 50   -- fichas por tanda: 5 por segundo; nunca más de 10 (decisión 1.5)
local POR_SESION_DEFECTO = 200 -- fichas del servidor por sesión (D5.2)
local LOCAL_POR_TANDA = 200    -- lecturas locales (mapas, zonas…) por tanda: no van al servidor
local TOPE_MB_DEFECTO = 6      -- lotes guardados esperando al programa (decidido el 2026-11-17)
local FOTO_DIAS_DEFECTO = 3    -- cada cuántos días se repite una foto que no ha cambiado (decidido el 2026-11-17)
local MAX_FOTOS = 20000        -- huellas de fotos guardadas por personaje
local MAX_ERRORES = 20
local VUELTAS_MAX = 5
local TOPE_MB_MAX = 12

-- ───────── utilidades (las mismas reglas que ForeverTracker.lua) ─────────
local function pack(...) return { n = select("#", ...), ... } end
local function try(f, ...)
	if type(f) ~= "function" then return nil end
	local r = pack(pcall(f, ...))
	if not r[1] then return nil end
	return unpack(r, 2, r.n)
end
local function secreto(v) return issecretvalue and issecretvalue(v) or false end
local function usable(v) return v ~= nil and not secreto(v) end
local function num(v) if usable(v) and type(v) == "number" then return v end end
local function str(v) if usable(v) and type(v) == "string" then return v end end
local function b01(v) if v == nil or secreto(v) then return nil end return v and 1 or 0 end
local function inCombat() return (InCombatLockdown and InCombatLockdown()) or (UnitAffectingCombat and UnitAffectingCombat("player")) or false end
local function now() return try(GetServerTime) or time() end
local function reloj() return try(GetTime) or now() end
R.try, R.num, R.str, R.b01, R.inCombat, R.now, R.pack = try, num, str, b01, inCombat, now, pack
R.SECRETO = "#secreto"

-- Copia limpia para el JSON del juego: sin funciones, sin valores secretos (van como "#secreto"), colores como «rrggbb»,
-- vectores como {x, y}, listas sin huecos (nil → false). Igual que el addon de fichas (Fichas.lua, limpio/todos).
local function limpio(v, prof)
	prof = prof or 0
	if secreto(v) then return R.SECRETO end
	local t = type(v)
	if t == "string" or t == "number" or t == "boolean" then return v end
	if t ~= "table" or prof > 6 then return nil end
	if type(v.r) == "number" and type(v.g) == "number" and type(v.b) == "number" then
		return string.format("%02x%02x%02x", math.floor(v.r * 255 + 0.5), math.floor(v.g * 255 + 0.5), math.floor(v.b * 255 + 0.5))
	end
	if type(v.GetXY) == "function" then
		local ok, x, y = pcall(v.GetXY, v)
		if ok then return { x = x, y = y } end
		return nil
	end
	local r, n = {}, #v
	if n > 0 then
		for i = 1, n do local y = limpio(v[i], prof + 1); if y == nil then y = false end; r[i] = y end
	else
		for k, x in pairs(v) do
			if type(k) == "string" or type(k) == "number" then
				local y = limpio(x, prof + 1)
				if y ~= nil then r[tostring(k)] = y end
			end
		end
	end
	return r
end
local function todos(fn, ...)
	if type(fn) ~= "function" then return nil end
	local r = pack(pcall(fn, ...))
	if not r[1] or r.n < 2 then return nil end
	local l = {}
	for i = 2, r.n do
		local y = limpio(r[i])
		if y == nil then y = false end
		l[i - 1] = y
	end
	return l
end
local function uno(fn, ...)
	if type(fn) ~= "function" then return nil end
	local ok, v = pcall(fn, ...)
	if ok then return limpio(v) end
end
local function recuadro(fn, ...)
	if type(fn) ~= "function" then return nil end
	local ok, d = pcall(fn, ...)
	if not ok or type(d) ~= "table" or type(d.lines) ~= "table" then return nil end
	local l = {}
	for i, linea in ipairs(d.lines) do l[i] = limpio(linea) or false end
	return l
end
R.limpio, R.todos, R.uno, R.recuadro = limpio, todos, uno, recuadro

-- Tramos «1-5,8,20-22» → lista de números (como Lista.lua del addon de fichas).
local function tramos(texto)
	local l = {}
	for parte in string.gmatch(texto or "", "[^,]+") do
		local a, b = parte:match("^%s*(%d+)%s*%-%s*(%d+)%s*$")
		if a then for i = tonumber(a), tonumber(b) do l[#l + 1] = i end
		else local n = tonumber(parte); if n then l[#l + 1] = n end end
	end
	return l
end
R.tramos = tramos

-- Serialización fija (claves ordenadas) y huella corta, para saber si una foto ha cambiado.
local function ser(v, buf)
	local t = type(v)
	if t == "table" then
		local keys = {}
		for k in pairs(v) do keys[#keys + 1] = k end
		table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
		buf[#buf + 1] = "{"
		for _, k in ipairs(keys) do buf[#buf + 1] = tostring(k) .. "="; ser(v[k], buf); buf[#buf + 1] = ";" end
		buf[#buf + 1] = "}"
	else buf[#buf + 1] = tostring(v) end
end
local function huella(v)
	local buf = {}
	ser(v, buf)
	local s = table.concat(buf)
	local h = 5381
	for i = 1, #s do h = (h * 33 + s:byte(i)) % 4294967296 end
	-- En dos mitades de 16 bits: vale igual en el Lua 5.1 del juego y en el simulador (fengari, enteros de 32 bits).
	return string.format("%04x%04x", math.floor(h / 65536), math.floor(h % 65536))
end
R.huella = huella

-- ───────── estado del addon ─────────
local DB                 -- ForeverTrackerRecopilador
local CH                 -- ForeverTrackerChar (fotos, cronología)
local E = {}             -- FT.ENCARGOS o vacío
local hex, guid          -- tu número en hexadecimal y tu GUID (solo viaja hasta la web)
local sesion             -- identificador de la sesión del juego
local cabecera = {}      -- juego y quien, para cada lote
local buffers = {}       -- por clase: { obs = {}, n = 0, inicio = hora }
local contador = 0
local errores = {}
local medidor = nil
local arrancado, listo = false, false
local estadoUltimo = {}  -- último «estado» emitido (serializado), por clase

local function ajuste(nombre, defecto, min, max)
	local v = num(E[nombre])
	if not v then return defecto end
	if min and v < min then v = min end
	if max and v > max then v = max end
	return v
end

function R.error(donde, e)
	errores[#errores + 1] = { hora = now(), donde = tostring(donde), error = tostring(e):sub(1, 200) }
	if #errores > MAX_ERRORES then table.remove(errores, 1) end
end
-- Llama a fn protegido; un fallo se apunta y no para nada.
function R.segura(donde, fn, ...)
	local ok, e = pcall(fn, ...)
	if not ok then R.error(donde, e) end
	return ok
end

-- El interruptor (decisión 1.2): el de /ft y el de la cuenta en la web (Encargos.lua, recoger).
function R.activo()
	if not DB or DB.interruptor == false then return false end
	if E.recoger == false then return false end
	return true
end
function R.setInterruptor(v)
	if DB then DB.interruptor = v and true or false end
end
function R.interruptor() return DB and DB.interruptor ~= false end

-- ───────── contexto: cabecera, posición, estado ─────────
local function quienSoy()
	local _, clase = try(UnitClass, "player")
	local _, raza = try(UnitRace, "player")
	local faccion = try(UnitFactionGroup, "player")
	local sexo = num(try(UnitSex, "player"))
	return { guid = guid, clase = str(clase), raza = str(raza), faccion = str(faccion),
		genero = sexo == 3 and "femenino" or sexo == 2 and "masculino" or nil, nivel = num(try(UnitLevel, "player")) }
end
local function juego()
	local version, build = try(GetBuildInfo)
	return { version = str(version), build = tonumber(build), idioma = str(try(GetLocale)), region = str(try(GetCurrentRegionName)),
		reino = str(try(GetRealmName)), modo = C_GameRules and num(try(C_GameRules.GetActiveGameMode)) or nil }
end
function R.idioma() return cabecera.juego and cabecera.juego.idioma or str(try(GetLocale)) end
function R.build() return cabecera.juego and cabecera.juego.build end
function R.nivel() return num(try(UnitLevel, "player")) end

-- Posición del jugador: mapa de la zona, 0-1, metros y subzona, con el «cómo» (saquear, hablar, senalar, placa, raton, propia).
-- Dentro de mazmorras el juego no da posición: todo nil salvo el cómo.
function R.pos(como)
	local p = { como = como }
	local mapa = C_Map and num(try(C_Map.GetBestMapForUnit, "player"))
	if mapa then
		p.mapa = mapa
		local v = try(C_Map.GetPlayerMapPosition, mapa, "player")
		if v then
			local ok, x, y = pcall(v.GetXY, v)
			if ok then p.x, p.y = num(x), num(y) end
		end
	end
	local my, mx = try(UnitPosition, "player")
	p.mx, p.my = num(mx), num(my)
	p.subzona = str(try(GetSubZoneText))
	if p.subzona == "" then p.subzona = nil end
	return p
end

-- Estado del jugador que cambia el dato (apartado 3.2): nivel, grupo, banda, descanso, efectos propios, hora del servidor,
-- tiempo atmosférico y misiones que lleva. Se emite al abrir cada lote y cuando cambia (como mucho cada 10 s).
local function efectosPropios()
	if inCombat() or not (C_UnitAuras and C_UnitAuras.GetAuraDataByIndex) then return nil end
	local l = {}
	for i = 1, 40 do
		local a = try(C_UnitAuras.GetAuraDataByIndex, "player", i, "HELPFUL")
		if not a or secreto(a) then break end
		local id = num(a.spellId)
		if id then l[#l + 1] = id end
	end
	table.sort(l)
	return l
end
local function misionesEnCurso()
	if not (C_QuestLog and C_QuestLog.GetNumQuestLogEntries) then return nil end
	local l = {}
	for i = 1, (num(try(C_QuestLog.GetNumQuestLogEntries)) or 0) do
		local q = try(C_QuestLog.GetInfo, i)
		if q and not q.isHeader and num(q.questID) and q.questID > 0 then l[#l + 1] = q.questID end
	end
	table.sort(l)
	return l
end
local function estadoActual()
	local n = num(try(GetNumGroupMembers)) or 0
	local h, m = try(GetGameTime)
	local tiempo = C_Weather and uno(C_Weather.GetCurrentWeather) or nil
	local descanso = num(try(GetXPExhaustion))
	return {
		nivel = R.nivel(), grupo_n = n, banda = b01(try(IsInRaid)), descanso = descanso and descanso > 0 and 1 or 0,
		efectos_jugador = efectosPropios(), hora_servidor = (num(h) and num(m)) and string.format("%02d:%02d", h, m) or nil,
		tiempo = tiempo, misiones = misionesEnCurso(),
	}
end
R.estadoActual = estadoActual

-- ───────── observaciones ─────────
-- Las familias y el orden de sus campos (apartado 8.1.3 del diseño; el lector de la base usa el mismo orden).
R.CAMPOS = {
	estado = { "nivel", "grupo_n", "banda", "descanso", "efectos_jugador", "hora_servidor", "tiempo", "misiones" },
	dato = { "tipo", "id", "campo", "valor", "idioma", "mapa", "x", "y", "mx", "my", "subzona", "como" },
	posicion = { "tipo", "id", "nombre", "guid", "mapa", "x", "y", "mx", "my", "subzona", "como" },
	ejemplar = { "tipo", "id", "guid", "mapa", "x", "y", "mx", "my", "como" },
	botin = { "origen_guid", "origen_nombre", "como", "hechizo", "objetos", "dinero", "dificultad", "grupo_n", "habilidad", "mapa", "x", "y" },
	combate = { "guid", "nombre", "nivel_pnj", "clasificacion", "dano_hecho", "dano_recibido", "duracion", "resultado", "golpes" },
	ganancia = { "clase_ganancia", "cantidad", "de_tipo", "de_id", "de_nombre", "faccion_rep" },
	precio = { "casa", "objeto", "sufijo", "nivel_objeto", "precio", "cantidad", "de", "segundos", "pnj_guid", "reputacion" },
	efecto = { "hechizo", "nombre", "de_tipo", "de_id", "de_nombre", "cifras", "duracion", "mapa", "x", "y" },
	texto = { "tipo", "id", "nombre", "guid", "clase_texto", "texto", "idioma", "mapa", "x", "y" },
	progreso = { "habilidad", "de", "a", "con_tipo", "con_id", "receta", "resultado", "cantidad" },
	viaje = { "clase_viaje", "de_nodo", "a_nodo", "coste", "duracion" },
	mazmorra = { "mazmorra", "nombre", "dificultad", "tamano", "jefe", "jefe_nombre", "ganado", "duracion" },
	servidor = { "tipo", "id", "tiene_datos", "origen" },
	ficha = { "tipo", "id", "origen", "nivel", "datos" },
	cementerio = { "mapa", "x", "y", "mapa_cuerpo", "cx", "cy" },
}

local cierra -- se define abajo
local function buffer(clase)
	local b = buffers[clase]
	if not b then b = { obs = {}, n = 0, inicio = nil }; buffers[clase] = b end
	return b
end
local function empuja(b, fila)
	b.n = b.n + 1
	b.obs[b.n] = fila
	if b.n >= BLOQUE then cierra(b) end
end
local function fila(familia, t)
	local campos = R.CAMPOS[familia]
	local f = { familia, now() }
	for i, c in ipairs(campos) do
		local v = t[c]
		-- nil o false = no observado; true = 1 (los sí/no de verdad van como 1/0 con b01); tablas, limpias.
		if v == nil then v = false
		elseif v == true then v = 1
		elseif type(v) == "table" then v = limpio(v) or false end
		f[i + 2] = v
	end
	return f
end
local function abre(b, clase)
	b.inicio = now()
	b.clase = clase
	local est = estadoActual()
	estadoUltimo[clase] = huella(est)
	b.n = b.n + 1
	b.obs[b.n] = fila("estado", est)
end
-- Apunta una observación. t = tabla con los campos de la familia; clase = "juego" (por defecto) o "subasta".
function R.obs(familia, t, clase)
	if not listo or not R.activo() or not R.CAMPOS[familia] then return false end
	clase = clase or "juego"
	local b = buffer(clase)
	if b.n == 0 then abre(b, clase) end
	empuja(b, fila(familia, t))
	return true
end
-- Si el estado ha cambiado, lo emite (en cada clase que tenga lote abierto). Como mucho cada 10 s.
local estadoPendiente = false
local function emiteEstado()
	estadoPendiente = false
	if not listo or not R.activo() then return end
	local est = estadoActual()
	local h = huella(est)
	for clase, b in pairs(buffers) do
		if b.n > 0 and estadoUltimo[clase] ~= h then
			estadoUltimo[clase] = h
			empuja(b, fila("estado", est))
		end
	end
end
function R.estado()
	if estadoPendiente then return end
	estadoPendiente = true
	C_Timer.After(10, function() R.segura("estado", emiteEstado) end)
end

-- Añade la posición a una tabla de campos (mapa, x, y, mx, my, subzona, como).
function R.conPos(t, como)
	local p = R.pos(como)
	for k, v in pairs(p) do if t[k] == nil then t[k] = v end end
	return t
end

-- Foto de un campo de una cosa (apartado 5, regla general): una vez por personaje, versión y valor; otra vez si cambia,
-- si cambia la versión del juego o si han pasado fotoDias días. Devuelve true si se ha apuntado.
function R.foto(tipo, id, campo, valor, como, idioma)
	if not listo or not R.activo() or id == nil then return false end
	CH.fotos = CH.fotos or {}
	local clave = tipo .. ":" .. tostring(id) .. ":" .. campo
	local h = huella(valor)
	local build = tostring(R.build() or "")
	local dias = ajuste("fotoDias", FOTO_DIAS_DEFECTO, 1, 90)
	local vieja = CH.fotos[clave]
	if vieja then
		local b, hora, hv = vieja:match("^([^|]*)|(%d+)|(%x+)$")
		if b == build and hv == h and (now() - (tonumber(hora) or 0)) < dias * 86400 then return false end
	end
	CH.fotos[clave] = build .. "|" .. now() .. "|" .. h
	-- idioma: nil = el del cliente; false = no aplica (números); un texto = ese.
	if idioma == nil then idioma = R.idioma() elseif idioma == false then idioma = nil end
	local t = { tipo = tipo, id = id, campo = campo, valor = valor, idioma = idioma }
	if como then R.conPos(t, como) end
	return R.obs("dato", t)
end

-- ───────── cerrar lotes: JSON → gzip → base64 (regla 12) ─────────
local function comprime(tabla)
	local E2 = C_EncodingUtil
	if not E2 then return nil, "sin C_EncodingUtil" end
	local okJ, json = pcall(E2.SerializeJSON, tabla)
	if not okJ or type(json) ~= "string" then return nil, "json: " .. tostring(json) end
	local okC, comp = pcall(E2.CompressString, json, Enum and Enum.CompressionMethod and Enum.CompressionMethod.Gzip or 2)
	if not okC or type(comp) ~= "string" then return nil, "gzip: " .. tostring(comp) end
	local okB, b64 = pcall(E2.EncodeBase64, comp)
	if not okB or type(b64) ~= "string" then return nil, "base64: " .. tostring(b64) end
	return b64, #json
end
local function recorta()
	local tope = ajuste("topeMB", TOPE_MB_DEFECTO, 1, TOPE_MB_MAX) * 1048576
	local total = 0
	for _, l in ipairs(DB.lotes) do total = total + (l.bytes or 0) end
	if total <= tope then return end
	local recibidos = {}
	if type(E.recibidos) == "string" then for id in E.recibidos:gmatch("[^,%s]+") do recibidos[id] = true end
	elseif type(E.recibidos) == "table" then for _, id in ipairs(E.recibidos) do recibidos[tostring(id)] = true end end
	-- Primero los que la web ya tiene; después, los más viejos (de más de 30 días primero, luego sin más).
	local function quita(cond)
		for i = 1, #DB.lotes do
			if total <= tope then return end
			local l = DB.lotes[i]
			if l and cond(l) then total = total - (l.bytes or 0); table.remove(DB.lotes, i); return quita(cond) end
		end
	end
	quita(function(l) return recibidos[l.id] == true end)
	quita(function(l) return (now() - (l.fin or 0)) > 30 * 86400 end)
	while total > tope and #DB.lotes > 0 do total = total - (DB.lotes[1].bytes or 0); table.remove(DB.lotes, 1) end
end
cierra = function(b)
	if b.n == 0 then return end
	contador = contador + 1
	local id = (hex or "0") .. "-" .. tostring(sesion and sesion:match("%-(%d+)$") or 0) .. "-" .. contador
	local lote = { formato = FORMATO_LOTE, addon = VERSION, lote = id, sesion = sesion, clase = b.clase, inicio = b.inicio, fin = now(),
		juego = cabecera.juego, quien = cabecera.quien, encargos = R.encargoResumen and R.encargoResumen() or nil,
		medidor = medidor, errores = #errores > 0 and errores or nil, obs = b.obs }
	local datos, tam = comprime(lote)
	local fila2 = { id = id, sesion = sesion, clase = b.clase, inicio = b.inicio, fin = lote.fin }
	if datos then
		fila2.bytes = #datos
		fila2.datos = datos
		fila2.n = b.n
	else
		-- Si el compresor fallara, se guarda tal cual (ocupa más, pero no se pierde nada; la web lo entiende igual).
		fila2.bytes = 0
		fila2.crudo = lote
		R.error("comprimir", tam)
	end
	DB.lotes[#DB.lotes + 1] = fila2
	b.obs, b.n, b.inicio = {}, 0, nil
	estadoUltimo[b.clase] = nil
	recorta()
end
function R.cierraTodo() for _, b in pairs(buffers) do cierra(b) end end

-- Resumen para el JSON del personaje (Ajustes de la web) y el panel de /ft.
function R.resumen()
	if not DB then return nil end
	local bytes, pendientes = 0, 0
	local recibidos = {}
	if type(E.recibidos) == "string" then for id in E.recibidos:gmatch("[^,%s]+") do recibidos[id] = true end end
	for _, l in ipairs(DB.lotes) do bytes = bytes + (l.bytes or 0); if not recibidos[l.id] then pendientes = pendientes + 1 end end
	local abiertos = 0
	for _, b in pairs(buffers) do abiertos = abiertos + b.n end
	return { formato = FORMATO_LOTE, interruptor = DB.interruptor ~= false, lotes = #DB.lotes, bytes = bytes, pendientes = pendientes,
		enMemoria = abiertos, sesion = sesion, encargos = R.encargoResumen and R.encargoResumen() or nil, lista = E.lista }
end

-- ───────── medidor del coste (C_AddOnProfiler, cada 10 minutos) ─────────
local function mide()
	if not (C_AddOnProfiler and C_AddOnProfiler.GetAddOnMetric) then return end
	local m = { hora = now() }
	for k, nombre in pairs({ [0] = "media", [1] = "reciente", [4] = "pico", [5] = "mas1ms", [6] = "mas5ms", [7] = "mas10ms" }) do
		local ok, v = pcall(C_AddOnProfiler.GetAddOnMetric, ADDON, k)
		m[nombre] = ok and limpio(v) or nil
	end
	local okT, t = pcall(C_AddOnProfiler.GetOverallMetric, 1)
	m.todos = okT and limpio(t) or nil
	medidor = m
end

-- ───────── el único aviso en el chat: la versión (apartado 10.7) ─────────
local function versionNum(v)
	local a, b, c = tostring(v or ""):match("^(%d+)%.(%d+)%.?(%d*)")
	if not a then return 0 end
	return tonumber(a) * 1000000 + tonumber(b) * 1000 + (tonumber(c) or 0)
end
local function avisoVersion()
	local ultima = str(E.addonUltimo)
	if ultima and versionNum(ultima) > versionNum(VERSION) then
		print("|cffffd100Forever Tracker|r: hay una versión nueva (" .. ultima .. "). El programa de Windows la instala solo; si no lo tienes, descárgala en la web.")
	elseif DB.versionVista and versionNum(DB.versionVista) < versionNum(VERSION) then
		print("|cffffd100Forever Tracker|r: actualizado a la versión " .. VERSION .. ".")
	end
	DB.versionVista = VERSION
end

-- ═════════ Encargos: fichas que la web pide al servidor a través de los jugadores (apartado 4) ═════════
-- Lo que se lee de cada ficha es lo mismo que el addon de fichas de desarrollo (Fichas.lua, LEE.* y LOCAL.*), para que la base lo
-- importe por el mismo camino.
local LEE = {}
function LEE.mision(id)
	local r = { t = uno(C_QuestLog.GetTitleForQuestID, id), nv = uno(C_QuestLog.GetQuestDifficultyLevel, id),
		obj = uno(C_QuestLog.GetQuestObjectives, id), xp = todos(GetQuestLogRewardXP, id), din = uno(GetQuestLogRewardMoney, id),
		tag = uno(C_QuestLog.GetQuestTagInfo, id), grupo = uno(C_QuestLog.GetSuggestedGroupSize, id),
		monedas = uno(C_QuestLog.GetQuestRewardCurrencies, id), hech = uno(C_QuestInfoSystem and C_QuestInfoSystem.GetQuestRewardSpells, id),
		rec = recuadro(C_TooltipInfo and C_TooltipInfo.GetHyperlink, "quest:" .. id) }
	local nf = uno(GetNumQuestLogRewards, id)
	if type(nf) == "number" and nf > 0 then
		r.fijas = {}
		for i = 1, nf do r.fijas[i] = todos(GetQuestLogRewardInfo, i, id) or false end
	end
	-- Regla 15: con 0 opciones no se escribe nada (no observado); solo cuentan las lecturas con opciones.
	local ne = uno(GetNumQuestLogChoices, id)
	if type(ne) == "number" and ne > 0 then
		r.elegir = {}
		for i = 1, ne do r.elegir[i] = todos(GetQuestLogChoiceInfo, i, id) or false end
	end
	return r
end
function LEE.objeto(id)
	return { info = todos(C_Item.GetItemInfo, id), stats = uno(C_Item.GetItemStats, "item:" .. id), hech = todos(C_Item.GetItemSpell, id),
		inv = uno(C_Item.GetItemInventoryTypeByID, id), rec = recuadro(C_TooltipInfo and C_TooltipInfo.GetItemByID, id) }
end
function LEE.hechizo(id)
	return { info = uno(C_Spell.GetSpellInfo, id), desc = uno(C_Spell.GetSpellDescription, id), sub = uno(C_Spell.GetSpellSubtext, id),
		coste = uno(C_Spell.GetSpellPowerCost, id), rec = recuadro(C_TooltipInfo and C_TooltipInfo.GetSpellByID, id) }
end
local LOCAL = {}
function LOCAL.mapa(id) return { info = uno(C_Map.GetMapInfo, id), vuelos = uno(C_TaxiMap and C_TaxiMap.GetTaxiNodesForMap, id) } end
function LOCAL.zona(id) return { n = uno(C_Map.GetAreaInfo, id) } end
function LOCAL.logro(id)
	local a = todos(GetAchievementInfo, id)
	if not a then return nil end
	local r = { n = a[2], puntos = a[3], desc = a[8], marcas = a[9], icono = a[10], premio = a[11], estadistica = a[15] }
	local nc = uno(GetAchievementNumCriteria, id)
	if type(nc) == "number" and nc > 0 then
		r.criterios = {}
		for i = 1, nc do
			local c = todos(GetAchievementCriteriaInfo, id, i)
			r.criterios[i] = c and { texto = c[1], tipo = c[2], pide = c[5], cosa = c[8], id = c[10] } or false
		end
	end
	return r
end
function LOCAL.faccion(id)
	local d = uno(C_Reputation.GetFactionDataByID, id)
	if type(d) ~= "table" then return nil end
	return { n = d.name, desc = d.description, cabecera = d.isHeader, hija = d.isChild }
end
function LOCAL.moneda(id)
	local d = uno(C_CurrencyInfo.GetCurrencyInfo, id)
	if type(d) ~= "table" then return nil end
	return { n = d.name, desc = d.description, icono = d.iconFileID, max = d.maxQuantity, calidad = d.quality }
end
function LOCAL.montura(id)
	local a = todos(C_MountJournal and C_MountJournal.GetMountInfoByID, id)
	if not a then return nil end
	return { n = a[1], hechizo = a[2], icono = a[3], tipoFuente = a[6], deFaccion = a[8], faccion = a[9], oculta = a[10],
		extra = todos(C_MountJournal.GetMountInfoExtraByID, id) }
end
function LOCAL.titulo(id) return { n = uno(GetTitleName, id) } end
R.LEE, R.LOCAL = LEE, LOCAL

-- Claves de los grupos de Encargos.lua → tipo de ficha.
local SERVIDOR = { misiones = "mision", objetos = "objeto", hechizos = "hechizo" }
local LOCALES = { mapas = "mapa", zonas = "zona", logros = "logro", facciones = "faccion", monedas = "moneda", monturas = "montura", titulos = "titulo" }
local YA = {
	objeto = function(id) return C_Item.IsItemDataCachedByID and C_Item.IsItemDataCachedByID(id) end,
	hechizo = function(id) return C_Spell.IsSpellDataCached and C_Spell.IsSpellDataCached(id) end,
	mision = function(id) return HaveQuestData and HaveQuestData(id) end,
}
local PIDE = {
	mision = function(id) C_QuestLog.RequestLoadQuestByID(id) end,
	objeto = function(id) C_Item.RequestLoadItemDataByID(id) end,
	hechizo = function(id) C_Spell.RequestLoadSpellData(id) end,
}

local EN = { plan = {}, pos = 0, locales = {}, posLocal = 0, pendientes = {}, nPendientes = 0, diferidas = {}, nDiferidas = 0,
	sinRespuesta = {}, sinDatos = {}, rondas = 0, hechas = 0, hechasLocal = 0, grupos = {}, terminado = false, empezado = false }

local function cumple(g)
	local function en(lista, valor)
		if type(lista) ~= "string" or lista == "" or not valor then return true end
		for v in lista:gmatch("[^,%s]+") do if v == valor then return true end end
		return false
	end
	local q = cabecera.quien or {}
	if not en(g.idiomas, R.idioma()) then return false end
	if not en(g.clases, q.clase) then return false end
	-- La condición de facción es «faccion» (1.3.1). En la 1.3.0 era «facciones», que choca con la lista local de facciones (números):
	-- se sigue admitiendo, pero solo si trae nombres de facción (Alliance, Horde, Neutral), no números.
	if not en(g.faccion, q.faccion) then return false end
	if type(g.facciones) == "string" and g.facciones:find("%a") and not en(g.facciones, q.faccion) then return false end
	if not en(g.regiones, cabecera.juego and cabecera.juego.region) then return false end
	if type(g.niveles) == "string" and g.niveles ~= "" then
		local a, b = g.niveles:match("^%s*(%d+)%s*%-%s*(%d+)%s*$")
		local n = R.nivel() or 0
		if a and (n < tonumber(a) or n > tonumber(b)) then return false end
	end
	return true
end

-- Elige los grupos de esta sesión: al azar con los pesos, hasta llenar porSesion fichas del servidor (apartado 4.2.2).
local function planifica()
	local porSesion = ajuste("porSesion", POR_SESION_DEFECTO, 0, 2000)
	local grupos = {}
	for _, g in ipairs(type(E.grupos) == "table" and E.grupos or {}) do
		if type(g) == "table" and cumple(g) then grupos[#grupos + 1] = g end
	end
	local semilla = (now() % 1000000) + math.floor((reloj() * 1000) % 1000000)
	pcall(math.randomseed, semilla)
	local plan, locales, total = {}, {}, 0
	while #grupos > 0 and total < porSesion do
		local suma = 0
		for _, g in ipairs(grupos) do suma = suma + math.max(num(g.peso) or 1, 0.01) end
		local r, elegido, idx = math.random() * suma, nil, nil
		for i, g in ipairs(grupos) do
			r = r - math.max(num(g.peso) or 1, 0.01)
			if r <= 0 then elegido, idx = g, i; break end
		end
		if not elegido then elegido, idx = grupos[#grupos], #grupos end
		table.remove(grupos, idx)
		EN.grupos[#EN.grupos + 1] = elegido.id
		local ids = {}
		for clave, tipo in pairs(SERVIDOR) do for _, id in ipairs(tramos(elegido[clave])) do ids[#ids + 1] = { tipo, id } end end
		if #ids > 0 then
			-- Si no caben todas, se empieza por un punto al azar del grupo (así entre muchos jugadores se cubre entero).
			local hueco = porSesion - total
			local desde = #ids > hueco and math.random(1, #ids) or 1
			for k = 0, math.min(#ids, hueco) - 1 do plan[#plan + 1] = ids[((desde - 1 + k) % #ids) + 1] end
			total = total + math.min(#ids, hueco)
		end
		for clave, tipo in pairs(LOCALES) do for _, id in ipairs(tramos(elegido[clave])) do locales[#locales + 1] = { tipo, id } end end
	end
	-- Lo local no gasta el cupo del servidor: los grupos que cumplen y solo traen lecturas locales entran todos.
	for _, g in ipairs(grupos) do
		local soloLocal, tieneLocal = true, false
		for clave in pairs(SERVIDOR) do if type(g[clave]) == "string" and g[clave] ~= "" then soloLocal = false end end
		for clave in pairs(LOCALES) do if type(g[clave]) == "string" and g[clave] ~= "" then tieneLocal = true end end
		if soloLocal and tieneLocal then
			EN.grupos[#EN.grupos + 1] = g.id
			for clave, tipo in pairs(LOCALES) do for _, id in ipairs(tramos(g[clave])) do locales[#locales + 1] = { tipo, id } end end
		end
	end
	EN.plan, EN.locales = plan, locales
end

local function ficha(tipo, id, origen, datos)
	R.obs("ficha", { tipo = tipo, id = id, origen = origen, nivel = R.nivel(), datos = datos })
end
local function lee(tipo, id, origen)
	local ok, datos = pcall(LEE[tipo], id)
	if ok then
		ficha(tipo, id, origen, datos)
		R.obs("servidor", { tipo = tipo, id = id, tiene_datos = 1, origen = origen })
		EN.hechas = EN.hechas + 1
	else R.error("leer " .. tipo .. " " .. tostring(id), datos) end
end
local function contesta(tipo, id, bien)
	local k = tipo .. id
	local p = EN.pendientes[k]
	if not p then return end
	EN.pendientes[k] = nil
	EN.nPendientes = EN.nPendientes - 1
	if bien then
		if tipo == "mision" and not EN.diferidas[k] then
			EN.diferidas[k] = true
			EN.nDiferidas = EN.nDiferidas + 1
			C_Timer.After(RETARDO_MISION, function()
				if not EN.diferidas[k] then return end
				EN.diferidas[k] = nil
				EN.nDiferidas = EN.nDiferidas - 1
				R.segura("leer misión", lee, tipo, id, "servidor")
			end)
			return
		end
		lee(tipo, id, "servidor")
	else
		EN.sinDatos[k] = true
		R.obs("servidor", { tipo = tipo, id = id, tiene_datos = 0, origen = "servidor" })
	end
end
local function pide(tipo, id)
	local k = tipo .. id
	if EN.pendientes[k] or EN.sinDatos[k] then return end
	local okY, ya = pcall(YA[tipo], id)
	if okY and ya == true then lee(tipo, id, "cache"); return end
	EN.pendientes[k] = { tipo, id, reloj() }
	EN.nPendientes = EN.nPendientes + 1
	if not pcall(PIDE[tipo], id) then EN.pendientes[k] = nil; EN.nPendientes = EN.nPendientes - 1 end
end
local function caducadas()
	local ahora = reloj()
	for k, x in pairs(EN.pendientes) do
		if ahora - x[3] > ESPERA_MAX then
			EN.pendientes[k] = nil
			EN.nPendientes = EN.nPendientes - 1
			EN.sinRespuesta[#EN.sinRespuesta + 1] = { x[1], x[2] }
		end
	end
end
-- Un paso cada 5 s: primero lo que quedó sin contestar al salir la vez anterior, luego el plan, luego lo local, luego las vueltas.
local function paso()
	if not listo or not R.activo() or EN.terminado or not EN.empezado then return end
	if inCombat() then return end
	caducadas()
	local ritmo = ajuste("ritmo", RITMO_DEFECTO, 1, RITMO_MAX)
	if EN.nPendientes >= ritmo * 4 then return end
	if type(DB.reintentar) == "table" and #DB.reintentar > 0 then
		for _ = 1, ritmo do
			local x = table.remove(DB.reintentar)
			if not x then break end
			if PIDE[x[1]] then pide(x[1], x[2]) end
		end
		return
	end
	if EN.pos < #EN.plan then
		for _ = 1, ritmo do
			EN.pos = EN.pos + 1
			local x = EN.plan[EN.pos]
			if not x then break end
			pide(x[1], x[2])
		end
		return
	end
	if EN.posLocal < #EN.locales then
		for _ = 1, LOCAL_POR_TANDA do
			EN.posLocal = EN.posLocal + 1
			local x = EN.locales[EN.posLocal]
			if not x then break end
			local ok, d = pcall(LOCAL[x[1]], x[2])
			if ok and d ~= nil then ficha(x[1], x[2], "local", d); EN.hechasLocal = EN.hechasLocal + 1 end
		end
		return
	end
	if EN.nPendientes > 0 or EN.nDiferidas > 0 then return end
	if #EN.sinRespuesta > 0 and EN.rondas < VUELTAS_MAX then
		EN.rondas = EN.rondas + 1
		local l = EN.sinRespuesta
		EN.sinRespuesta = {}
		for i = 1, math.min(#l, ritmo) do pide(l[i][1], l[i][2]) end
		for i = ritmo + 1, #l do EN.sinRespuesta[#EN.sinRespuesta + 1] = l[i] end
		return
	end
	EN.terminado = true
	DB.ultimoEncargo = { lista = E.lista, hora = now(), fichas = EN.hechas, locales = EN.hechasLocal, grupos = EN.grupos, sinRespuesta = #EN.sinRespuesta }
end
function R.encargoResumen()
	if not EN.empezado then return nil end
	return { lista = E.lista, grupos = EN.grupos, plan = #EN.plan, hechas = EN.hechas, locales = EN.hechasLocal, terminado = EN.terminado }
end
local function empiezaEncargos()
	if EN.empezado or not R.activo() then return end
	EN.empezado = true
	R.segura("planificar", planifica)
end
-- Al salir: lo pedido sin contestar y las misiones a medio leer se vuelven a pedir al volver (regla 4).
local function guardaPendientes()
	DB.reintentar = type(DB.reintentar) == "table" and DB.reintentar or {}
	for _, x in pairs(EN.pendientes) do DB.reintentar[#DB.reintentar + 1] = { x[1], x[2] } end
	for k in pairs(EN.diferidas) do DB.reintentar[#DB.reintentar + 1] = { "mision", tonumber(k:sub(7)) } end
	for _, x in ipairs(EN.sinRespuesta) do DB.reintentar[#DB.reintentar + 1] = x end
	while #DB.reintentar > 2000 do table.remove(DB.reintentar, 1) end
end

-- ───────── limpieza de las huellas de fotos ─────────
local function recortaFotos()
	if not CH or type(CH.fotos) ~= "table" then return end
	local n = 0
	for _ in pairs(CH.fotos) do n = n + 1 end
	if n <= MAX_FOTOS then return end
	local l = {}
	for k, v in pairs(CH.fotos) do l[#l + 1] = { k, tonumber(v:match("|(%d+)|")) or 0 } end
	table.sort(l, function(a, b) return a[2] < b[2] end)
	for i = 1, n - MAX_FOTOS do CH.fotos[l[i][1]] = nil end
end

-- ───────── avisos del juego ─────────
local frame = CreateFrame("Frame")
local function register(ev)
	if C_EventUtils and C_EventUtils.IsEventValid and not C_EventUtils.IsEventValid(ev) then return end
	pcall(frame.RegisterEvent, frame, ev)
end
for _, ev in ipairs({ "ADDON_LOADED", "PLAYER_ENTERING_WORLD", "PLAYER_LOGOUT", "QUEST_DATA_LOAD_RESULT", "ITEM_DATA_LOAD_RESULT", "SPELL_DATA_LOAD_RESULT",
	"PLAYER_LEVEL_UP", "GROUP_ROSTER_UPDATE", "PLAYER_XP_UPDATE", "QUEST_LOG_UPDATE", "UNIT_AURA", "PLAYER_REGEN_ENABLED" }) do register(ev) end

frame:SetScript("OnEvent", function(_, ev, a1, a2)
	if ev == "ADDON_LOADED" then
		if a1 ~= ADDON then return end
		if type(ForeverTrackerRecopilador) ~= "table" or ForeverTrackerRecopilador.formato ~= FORMATO_LOTE or type(ForeverTrackerRecopilador.lotes) ~= "table" then
			-- Se conserva el interruptor si ya existía (apagado o encendido); si no, queda sin valor y abajo se enciende.
			local viejo = nil
			if type(ForeverTrackerRecopilador) == "table" and ForeverTrackerRecopilador.interruptor ~= nil then viejo = ForeverTrackerRecopilador.interruptor end
			ForeverTrackerRecopilador = { formato = FORMATO_LOTE, lotes = {}, interruptor = viejo }
		end
		DB = ForeverTrackerRecopilador
		DB.addon = VERSION
		if DB.interruptor == nil then DB.interruptor = true end
		if type(ForeverTrackerChar) ~= "table" then ForeverTrackerChar = {} end
		CH = ForeverTrackerChar
		E = type(FT.ENCARGOS) == "table" and FT.ENCARGOS or {}
		return
	end
	if not DB then return end
	if ev == "PLAYER_ENTERING_WORLD" then
		if arrancado then R.estado(); return end
		arrancado = true
		R.segura("entrar", function()
			guid = str(try(UnitGUID, "player"))
			hex = guid and guid:match("^Player%-%d+%-(%x+)$")
			sesion = (hex or "0") .. "-" .. now() .. "-" .. math.random(1000, 9999)
			cabecera.juego = juego()
			cabecera.quien = quienSoy()
			listo = true
			avisoVersion()
		end)
		C_Timer.After(ARRANQUE, function() R.segura("encargos", empiezaEncargos) end)
		C_Timer.NewTicker(CADA, function() R.segura("paso", paso) end)
		C_Timer.NewTicker(600, function() R.segura("medidor", mide) end)
		C_Timer.After(5, function() R.segura("medidor", mide) end)
		return
	end
	if ev == "PLAYER_LOGOUT" then
		R.segura("salir", guardaPendientes)
		R.segura("salir", R.cierraTodo)
		R.segura("salir", recortaFotos)
		return
	end
	if ev == "QUEST_DATA_LOAD_RESULT" then R.segura("respuesta", contesta, "mision", a1, a2)
	elseif ev == "ITEM_DATA_LOAD_RESULT" then R.segura("respuesta", contesta, "objeto", a1, a2)
	elseif ev == "SPELL_DATA_LOAD_RESULT" then R.segura("respuesta", contesta, "hechizo", a1, a2)
	elseif ev == "UNIT_AURA" then if a1 == "player" then R.estado() end
	else R.estado() end
end)

-- Para las pruebas y el panel de /ft.
R.ENCARGO = EN
function R.lotes() return DB and DB.lotes end
function R.sesion() return sesion end
