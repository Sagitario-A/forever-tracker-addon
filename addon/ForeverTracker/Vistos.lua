-- Forever Tracker: los demás jugadores que ves (desde la 1.1.0).
--
-- Apunta, solo fuera de combate (en combate el juego oculta quién es cada uno), a los jugadores que tienes delante: placas
-- de nombre, a quien seleccionas o pasas el ratón por encima, tu grupo y tu banda, tu lista de amigos y lo que sale en
-- /quién cuando lo escribes tú. De cada uno, lo mismo que enseña la armería de Blizzard (nombre, apellido, nivel, clase,
-- raza, facción y hermandad) y, para ti, tus encuentros: cuántas veces te lo has cruzado (uno nuevo si llevabas más de 10
-- minutos sin verlo) y dónde y cuándo fueron los 5 últimos. Nunca el chat, nunca nada automático (no lanza /quién).
-- La web lo usa como pista: lo confirma con la API de Blizzard antes de guardarlo; los encuentros solo los ves tú.
--
-- Se guarda en ForeverTrackerVistos (archivo de la cuenta, el mismo ForeverTracker.lua), en textos sencillos para que la
-- web no tenga que entender Lua:
--   region = "eu"
--   jugadores = { ["v:<GUID>"] = "nombre|apellido|nivel|clase|raza|facción|hermandad|encuentros|primero|último|recientes|porPersonaje" }
--     recientes = "fecha;mapa;zona;subzona;<tu número en hexadecimal>" separados por ^
--     porPersonaje = "<tu número en hexadecimal>:<encuentros>" separados por comas (cuántas veces con cada personaje tuyo)
--   quien = { ["q:<nombre completo>"] = "hermandad|nivel|clase|fecha" }
local ADDON, FT = ...

local MAX = 5000 -- jugadores guardados como mucho (al salir se quitan los más antiguos)
local QUIEN_MAX = 1000
local GAP = 600 -- segundos sin verlo para contar un encuentro nuevo
local RECENT = 5 -- encuentros recientes que se guardan de cada uno
local REGIONS = { [1] = "us", [2] = "kr", [3] = "eu", [4] = "tw" }
local VALID_REGION = { eu = true, us = true, kr = true, tw = true }

-- ───────── utilidades (las mismas reglas que ForeverTracker.lua) ─────────
local function pack(...) return { n = select("#", ...), ... } end
local function try(f, ...)
	if type(f) ~= "function" then return nil end
	local r = pack(pcall(f, ...))
	if not r[1] then return nil end
	return unpack(r, 2, r.n)
end
local function usable(v)
	if v == nil then return false end
	if issecretvalue and issecretvalue(v) then return false end
	return true
end
local function num(v) if usable(v) and type(v) == "number" then return v end end
local function str(v) if usable(v) and type(v) == "string" and v ~= "" then return v end end
local function inCombat()
	return (InCombatLockdown and InCombatLockdown()) or (UnitAffectingCombat and UnitAffectingCombat("player")) or false
end
local function now() return try(GetServerTime) or time() end
-- Texto limpio: sin los separadores del formato ni caracteres de control.
local function clean(v, max)
	local s = str(v)
	if not s then return "" end
	s = s:gsub("[|;%^%c]", " ")
	return (s:sub(1, max or 60))
end
-- Ajuste del juego (solo leer). En Forever es C_CVar.GetCVar; GetCVar suelto ya no existe (visto el 2026-11-11).
local function cvar(name) return str(try(C_CVar and C_CVar.GetCVar or GetCVar, name)) end
local function split(s)
	local out = {}
	for v in (s .. "|"):gmatch("([^|]*)|") do out[#out + 1] = v end
	return out
end

local DB -- ForeverTrackerVistos
local count, quienCount = 0, 0
local myHex -- tu número en hexadecimal (parte final de tu GUID)
local session = {} -- [GUID] = última vez que lo has visto en esta sesión
local ready = false

-- ───────── apuntar ─────────
-- met: si es un encuentro (lo tienes delante) o solo un dato (lista de amigos).
local function record(guid, name, surname, level, classFile, raceFile, faction, guild, met)
	local key = "v:" .. guid
	local old = DB.jugadores[key]
	local t = now()
	local times, first, last, recent, mine = 0, 0, 0, "", ""
	if old then
		local f = split(old)
		times, first, last, recent, mine = tonumber(f[8]) or 0, tonumber(f[9]) or 0, tonumber(f[10]) or 0, f[11] or "", f[12] or ""
		if not met and old ~= "" then
			-- Amigo ya apuntado: solo se ponen al día nivel y hermandad si llegan.
			level = level or tonumber(f[3]); guild = guild or f[7]
		end
	elseif count >= MAX * 1.2 then
		return -- lleno hasta la próxima limpieza (al salir del juego)
	end
	if met then
		if t - last < GAP then return end -- sigue siendo el mismo encuentro (por ejemplo, tras un /reload)
		times = times + 1
		if first == 0 then first = t end
		last = t
		local map = C_Map and num(try(C_Map.GetBestMapForUnit, "player")) or 0
		-- Zona: el nombre del mapa (GetZoneText da el del edificio si estás dentro, por ejemplo «Posada de Villa del Lago»,
		-- visto en el juego el 2026-11-11). Subzona: la del juego o, si no hay, la del minimapa.
		local info = map > 0 and C_Map.GetMapInfo and try(C_Map.GetMapInfo, map)
		local zone = clean(info and info.name, 60)
		if zone == "" then zone = clean(try(GetZoneText), 60) end
		local sub = clean(try(GetSubZoneText), 60)
		if sub == "" then sub = clean(try(GetMinimapZoneText), 60) end
		local parts = { table.concat({ t, map, zone, sub, myHex or "" }, ";") }
		for e in recent:gmatch("[^%^]+") do
			if #parts < RECENT then parts[#parts + 1] = e end
		end
		recent = table.concat(parts, "^")
		-- Encuentros de cada uno de tus personajes: «tu número:veces» separados por comas.
		if myHex then
			local byChar, found = {}, false
			for hex, n in mine:gmatch("(%x+):(%d+)") do
				if hex == myHex then n = tonumber(n) + 1; found = true end
				byChar[#byChar + 1] = hex .. ":" .. n
			end
			if not found then byChar[#byChar + 1] = myHex .. ":1" end
			mine = table.concat(byChar, ",")
		end
	end
	if not old then count = count + 1 end
	DB.jugadores[key] = table.concat({ clean(name, 40), clean(surname, 40), level or "", clean(classFile, 20), clean(raceFile, 30), clean(faction, 10),
		clean(guild, 60), times, first, last, recent, mine }, "|")
end

-- Contadores de esta sesión, para saber qué ve el addon y por qué descarta (los enseña /ft y quedan en el archivo).
local diag = { placas = 0, seleccion = 0, raton = 0, grupo = 0, combate = 0, noJugador = 0, oculto = 0, sinNombre = 0, mismo = 0, apuntados = 0 }
local function seeUnit(unit, source)
	if not try(UnitExists, unit) then return end
	if source then diag[source] = diag[source] + 1 end
	if not ready or inCombat() then diag.combate = diag.combate + 1; return end
	local isPlayer = try(UnitIsPlayer, unit)
	if not usable(isPlayer) or not isPlayer then diag.noJugador = diag.noJugador + 1; return end
	local me = try(UnitIsUnit, unit, "player")
	if not usable(me) or me then return end
	-- En combate, en jefes, en JcJ o en algunos mapas el juego oculta quién es cada uno: no se apunta.
	if C_Secrets and C_Secrets.ShouldUnitIdentityBeSecret and try(C_Secrets.ShouldUnitIdentityBeSecret, unit) then diag.oculto = diag.oculto + 1; return end
	local guid = str(try(UnitGUID, unit)) -- si aun así llega oculto, str() lo descarta
	if not guid or not guid:match("^Player%-%d+%-%x+$") then diag.oculto = diag.oculto + 1; return end
	local t = now()
	if session[guid] and t - session[guid] < GAP then session[guid] = t; diag.mismo = diag.mismo + 1; return end
	session[guid] = t
	local name, surname = try(UnitFullName, unit)
	if not str(name) then diag.sinNombre = diag.sinNombre + 1; return end
	diag.apuntados = diag.apuntados + 1
	local level = num(try(UnitLevel, unit))
	if level and level < 1 then level = nil end
	local _, classFile = try(UnitClass, unit)
	local _, raceFile = try(UnitRace, unit)
	local guild = try(GetGuildInfo, unit)
	record(guid, name, str(surname), level, str(classFile), str(raceFile), str(try(UnitFactionGroup, unit)), str(guild), true)
end

local function seeGroup()
	if not ready or inCombat() then return end
	if try(IsInRaid) then
		for i = 1, 40 do seeUnit("raid" .. i, "grupo") end
	else
		for i = 1, 4 do seeUnit("party" .. i, "grupo") end
	end
end

-- Lista de amigos: como mucho cada 5 minutos (el juego avisa a menudo).
local lastFriends = 0
local function seeFriends()
	if not ready or inCombat() or not (C_FriendList and C_FriendList.GetNumFriends and C_FriendList.GetFriendInfoByIndex) then return end
	local t = now()
	if t - lastFriends < 300 then return end
	lastFriends = t
	for i = 1, (num(try(C_FriendList.GetNumFriends)) or 0) do
		local f = try(C_FriendList.GetFriendInfoByIndex, i)
		local guid = f and str(f.guid)
		local full = f and str(f.name)
		if guid and full and guid:match("^Player%-%d+%-%x+$") then
			-- El nombre puede venir con el apellido detrás («Nombre-Apellido» o «Nombre Apellido»).
			local name, surname = full:match("^([^%- ]+)[%- ](.+)$")
			record(guid, name or full, surname, num(f.level), nil, nil, nil, nil, false)
		end
	end
end

-- /quién (cuando lo escribe el jugador): sin número de personaje, solo el nombre completo.
local function seeWho()
	if not ready or not (C_FriendList and C_FriendList.GetNumWhoResults and C_FriendList.GetWhoInfo) then return end
	local t = now()
	for i = 1, (num(try(C_FriendList.GetNumWhoResults)) or 0) do
		local w = try(C_FriendList.GetWhoInfo, i)
		local full = w and clean(w.fullName, 80)
		if full and full ~= "" then
			local key = "q:" .. full
			if DB.quien[key] or quienCount < QUIEN_MAX then
				if not DB.quien[key] then quienCount = quienCount + 1 end
				DB.quien[key] = table.concat({ clean(w.fullGuildName, 60), num(w.level) or "", clean(w.filename, 20), t }, "|")
			end
		end
	end
end

-- Al salir: si hay demasiados, se quitan los que hace más tiempo que no ves.
local function prune()
	if count <= MAX then return end
	local list = {}
	for k, v in pairs(DB.jugadores) do
		local f = split(v)
		list[#list + 1] = { k, tonumber(f[10]) or 0 }
	end
	table.sort(list, function(a, b) return a[2] < b[2] end)
	for i = 1, #list - MAX do DB.jugadores[list[i][1]] = nil end
	count = math.min(count, MAX)
end

-- ───────── eventos ─────────
local frame = CreateFrame("Frame")
local function register(ev)
	if C_EventUtils and C_EventUtils.IsEventValid and not C_EventUtils.IsEventValid(ev) then return end
	pcall(frame.RegisterEvent, frame, ev)
end
for _, ev in ipairs({ "ADDON_LOADED", "PLAYER_ENTERING_WORLD", "PLAYER_LOGOUT", "NAME_PLATE_UNIT_ADDED", "PLAYER_TARGET_CHANGED", "UPDATE_MOUSEOVER_UNIT",
	"GROUP_ROSTER_UPDATE", "FRIENDLIST_UPDATE", "WHO_LIST_UPDATE", "PLAYER_REGEN_ENABLED" }) do register(ev) end

frame:SetScript("OnEvent", function(_, ev, arg1)
	if ev == "ADDON_LOADED" then
		if arg1 ~= ADDON then return end
		if type(ForeverTrackerVistos) ~= "table" or ForeverTrackerVistos.version ~= 1 or type(ForeverTrackerVistos.jugadores) ~= "table" then
			ForeverTrackerVistos = { version = 1, jugadores = {}, quien = {} }
		end
		DB = ForeverTrackerVistos
		if type(DB.quien) ~= "table" then DB.quien = {} end
		for _ in pairs(DB.jugadores) do count = count + 1 end
		for _ in pairs(DB.quien) do quienCount = quienCount + 1 end
		return
	end
	if not DB then return end
	if ev == "PLAYER_ENTERING_WORLD" then
		local g = str(try(UnitGUID, "player"))
		myHex = g and g:match("^Player%-%d+%-(%x+)$")
		local rn = str(try(GetCurrentRegionName))
		local region = rn and rn:lower() or REGIONS[num(try(GetCurrentRegion)) or 0]
		if region and VALID_REGION[region] then DB.region = region end
		ready = true
		C_Timer.After(5, seeGroup)
		C_Timer.After(8, seeFriends)
		return
	end
	if ev == "PLAYER_LOGOUT" then
		pcall(prune)
		local parts = {}
		for _, k in ipairs({ "placas", "seleccion", "raton", "grupo", "combate", "noJugador", "oculto", "sinNombre", "mismo", "apuntados" }) do parts[#parts + 1] = k .. "=" .. diag[k] end
		DB.diag = table.concat(parts, " ") .. " placasAmigas=" .. tostring(cvar("nameplateShowFriends"))
		return
	end
	if ev == "NAME_PLATE_UNIT_ADDED" then seeUnit(arg1, "placas"); return end
	if ev == "PLAYER_TARGET_CHANGED" then seeUnit("target", "seleccion"); return end
	if ev == "UPDATE_MOUSEOVER_UNIT" then seeUnit("mouseover", "raton"); return end
	if ev == "GROUP_ROSTER_UPDATE" or ev == "PLAYER_REGEN_ENABLED" then C_Timer.After(1, seeGroup); return end
	if ev == "FRIENDLIST_UPDATE" then seeFriends(); return end
	if ev == "WHO_LIST_UPDATE" then seeWho(); return end
end)

-- Para /ft.
FT.vistosResumen = function() return count, quienCount end
-- Qué ha visto el addon en esta sesión y por qué ha descartado (para /ft).
FT.vistosDiag = function()
	return string.format("en esta sesión: placas %d, selección %d, ratón %d, grupo %d · apuntados %d · descartados: combate %d, no jugador %d, oculto %d, sin nombre %d, mismo encuentro %d · placas de aliados %s",
		diag.placas, diag.seleccion, diag.raton, diag.grupo, diag.apuntados, diag.combate, diag.noJugador, diag.oculto, diag.sinNombre, diag.mismo,
		cvar("nameplateShowFriends") == "1" and "encendidas" or "apagadas (Mayús+V para verlas)")
end
