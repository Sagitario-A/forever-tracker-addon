-- Forever Tracker: guarda lo que se puede leer de TU personaje para verlo en la web Forever Tracker.
--
-- Solo lee con la API del juego y nunca hace nada por ti (nada de automatizar). Nunca lee el chat, ni el nombre o número
-- de tu cuenta, ni nada de tu ordenador. Desde la 1.1.0, Vistos.lua apunta además lo público de los jugadores que ves
-- (ver ese archivo).
--
-- Qué deja en el archivo (WTF\Account\<cuenta>\SavedVariables\ForeverTracker.lua):
--   ForeverTrackerDB = { formato = 1, chars = { ["<GUID>"] = "<texto JSON>" } }
-- Un texto JSON ya montado por personaje, para que la web y el programa no tengan que entender Lua.
-- Lo que solo se ve al abrir una ventana (banco, correo, adiestramiento de bestias, ventana de profesión) se recuerda
-- entre sesiones en ForeverTrackerChar (archivo de cada personaje), y se copia al JSON.
-- El juego escribe estos archivos al cerrar sesión, al salir o con /reload.
local ADDON, FT = ...

local FORMATO = 1
local VERSION = "1.3.3"
do
	local get = (C_AddOns and C_AddOns.GetAddOnMetadata) or GetAddOnMetadata
	local ok, v = pcall(get, ADDON, "Version")
	if ok and type(v) == "string" and v ~= "" then VERSION = v end
end

local NULL = {} -- se escribe como null en el JSON
local snap = {} -- lo leído en esta sesión
local guid -- identificador del personaje (Player-<servidor>-<número en hexadecimal>)
local errors = {} -- fallos al leer, por parte (para saber qué no funciona en Forever)
local dirty, pending, waitingCombat, muted = {}, false, false, false
local bankOpen, mailOpen = false, false

-- ───────── utilidades ─────────
local function pack(...) return { n = select("#", ...), ... } end
-- Llama a una función del juego sin que un fallo rompa nada. Si no existe o falla, devuelve nil.
local function try(f, ...)
	if type(f) ~= "function" then return nil end
	local r = pack(pcall(f, ...))
	if not r[1] then return nil end
	return unpack(r, 2, r.n)
end
-- Valores «secretos» de las reglas nuevas (en combate o en mazmorras): no se pueden usar.
local function usable(v)
	if v == nil then return false end
	if issecretvalue and issecretvalue(v) then return false end
	return true
end
local function num(v) if usable(v) and type(v) == "number" then return v end end
-- Apariencias (1.3.3): la ventana de apariencias de Forever pone sola el filtro «no enseñar las que no tienes» en la categoría que
-- tiene elegida, y dura hasta salir del juego (un /reload no lo quita). Desde ese momento GetCategoryAppearances de esa categoría
-- devuelve solo las que tienes (visto en el juego el 2026-11-19: escudos, 118 al entrar y 18 tras abrir la ventana), mientras que
-- GetCategoryTotal sigue dando el total (118). Una lista está recortada si el juego dice que hay más y todo lo que trae lo tienes.
-- Devuelve si está recortada y el total que da el juego. Lo usan el progreso (este archivo) y la foto del recopilador (Mundo.lua).
function FT.listaRecortada(cat, lista)
	local total = C_TransmogCollection and num(try(C_TransmogCollection.GetCategoryTotal, cat))
	if not total or total <= #lista then return false, total end
	for _, a in ipairs(lista) do
		if type(a) ~= "table" or a.isCollected ~= true then return false, total end
	end
	return true, total
end
local function str(v) if usable(v) and type(v) == "string" then return v end end
local function inCombat()
	return (InCombatLockdown and InCombatLockdown()) or (UnitAffectingCombat and UnitAffectingCombat("player")) or false
end
local function now() return try(GetServerTime) or time() end

-- ───────── JSON ─────────
local function escape(s)
	return (s:gsub('[%c"\\]', function(c)
		if c == '"' then return '\\"' elseif c == "\\" then return "\\\\" end
		return string.format("\\u%04x", c:byte())
	end))
end
local encode
encode = function(v, buf)
	local t = type(v)
	if v == nil or v == NULL then buf[#buf + 1] = "null"
	elseif t == "boolean" then buf[#buf + 1] = v and "true" or "false"
	elseif t == "number" then
		if v ~= v or v == math.huge or v == -math.huge then buf[#buf + 1] = "null"
		elseif v == math.floor(v) then buf[#buf + 1] = string.format("%.0f", v)
		else buf[#buf + 1] = string.format("%.4f", v) end
	elseif t == "string" then buf[#buf + 1] = '"' .. escape(v) .. '"'
	elseif t == "table" then
		if #v > 0 or next(v) == nil then -- lista (o vacío)
			buf[#buf + 1] = "["
			for i = 1, #v do
				if i > 1 then buf[#buf + 1] = "," end
				encode(v[i], buf)
			end
			buf[#buf + 1] = "]"
		else
			local keys = {}
			for k in pairs(v) do keys[#keys + 1] = k end
			table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
			buf[#buf + 1] = "{"
			for i, k in ipairs(keys) do
				if i > 1 then buf[#buf + 1] = "," end
				buf[#buf + 1] = '"' .. escape(tostring(k)) .. '":'
				encode(v[k], buf)
			end
			buf[#buf + 1] = "}"
		end
	else buf[#buf + 1] = "null" end
end

-- Abre todas las cabeceras de una lista del juego (profesiones, reputaciones, monedas), lee, y deja cerradas otra vez
-- las que estaban cerradas. Mientras, no se hace caso de los avisos que provoca abrirlas.
local function withExpanded(count, info, expand, collapse, isCollapsed, read)
	muted = true
	local closed = {}
	local n = try(count) or 0
	for i = n, 1, -1 do
		local d = try(info, i)
		if d and d.isHeader and isCollapsed(d) and d.name then closed[d.name] = true; try(expand, i) end
	end
	local result = read(try(count) or 0)
	if next(closed) and collapse then
		for i = (try(count) or 0), 1, -1 do
			local d = try(info, i)
			if d and d.isHeader and d.name and closed[d.name] then try(collapse, i) end
		end
	end
	C_Timer.After(1, function() muted = false end)
	return result
end

-- ───────── lectores, uno por parte ─────────
local R = {}

R.identidad = function()
	local g = try(UnitGUID, "player")
	if str(g) and g:match("^Player%-%d+%-%x+$") then guid = g end
	local name, surname = try(UnitFullName, "player")
	snap.nombre = str(name) or snap.nombre
	snap.apellido = str(surname) or snap.apellido
	snap.reino = str(try(GetRealmName))
	snap.region = num(try(GetCurrentRegion))
	snap.regionNombre = str(try(GetCurrentRegionName))
	local _, classFile, classID = try(UnitClass, "player")
	snap.clase, snap.claseId = str(classFile), num(classID)
	local _, raceFile, raceID = try(UnitRace, "player")
	snap.raza, snap.razaId = str(raceFile), num(raceID)
	snap.faccion = str(try(UnitFactionGroup, "player"))
	snap.sexo = num(try(UnitSex, "player"))
	snap.nivel = num(try(UnitLevel, "player"))
	snap.xp = num(try(UnitXP, "player"))
	snap.xpMax = num(try(UnitXPMax, "player"))
	snap.descanso = num(try(GetXPExhaustion)) or 0
	local gName, gRank, gRankIndex = try(GetGuildInfo, "player")
	snap.hermandad = str(gName) and { nombre = gName, rango = str(gRank), rangoN = num(gRankIndex) } or NULL
	snap.zona = {
		mapa = C_Map and num(try(C_Map.GetBestMapForUnit, "player")),
		nombre = str(try(GetZoneText)),
		subzona = str(try(GetSubZoneText)),
	}
end

R.misiones = function()
	local ids = C_QuestLog and try(C_QuestLog.GetAllCompletedQuestIDs)
	if type(ids) == "table" then
		local out = {}
		for _, id in ipairs(ids) do if num(id) then out[#out + 1] = id end end
		table.sort(out)
		-- Las misiones hechas no se pierden: una lista vacía cuando ya había datos es que el juego aún no la tiene cargada.
		if #out > 0 or type(snap.misiones) ~= "table" or #snap.misiones == 0 then snap.misiones = out end
	end
	-- Misiones en curso: [número, 1 si ya está para entregar]
	if C_QuestLog and C_QuestLog.GetNumQuestLogEntries then
		local out = {}
		for i = 1, (try(C_QuestLog.GetNumQuestLogEntries) or 0) do
			local q = try(C_QuestLog.GetInfo, i)
			if q and not q.isHeader and num(q.questID) and q.questID > 0 then
				out[#out + 1] = { q.questID, try(C_QuestLog.IsComplete, q.questID) and 1 or 0 }
			end
		end
		snap.misionesEnCurso = out
	end
end

-- Hechizos de la web (Datos.lua) que conoce el personaje.
local PLAYER_BANK = Enum and Enum.SpellBookSpellBank and Enum.SpellBookSpellBank.Player
local function known(id)
	if C_SpellBook then
		if try(C_SpellBook.IsSpellKnown, id, PLAYER_BANK) then return true end
		if try(C_SpellBook.IsSpellInSpellBook, id, PLAYER_BANK, false) then return true end
	end
	if try(IsPlayerSpell, id) then return true end
	return false
end
R.hechizos = function()
	local out, seen = {}, {}
	for _, list in ipairs({ FT.RECETAS or {}, FT.HABILIDADES or {}, FT.ARMAS or {}, FT.MASCOTA or {} }) do
		for _, id in ipairs(list) do
			if not seen[id] then
				seen[id] = true
				if known(id) then out[#out + 1] = id end
			end
		end
	end
	table.sort(out)
	snap.hechizos = out
end

-- Profesiones, profesiones secundarias, armas, idiomas…: todo lo de la ventana de habilidades.
R.profesiones = function()
	local S = C_SkillInfo
	if not (S and S.GetNumSkillLines and S.GetSkillLineInfo) then errors.profesiones = "Sin C_SkillInfo"; return end
	snap.profesiones = withExpanded(S.GetNumSkillLines, S.GetSkillLineInfo, S.ExpandSkillHeader, S.CollapseSkillHeader,
		function(d) return d.isCollapsed end,
		function(n)
			local out, header = {}, nil
			for i = 1, n do
				local s = try(S.GetSkillLineInfo, i)
				if s then
					if s.isHeader then header = str(s.name)
					elseif num(s.skillID) then
						out[#out + 1] = { id = s.skillID, nombre = str(s.name), nivel = num(s.rank), max = num(s.maxRank), grupo = header }
					end
				end
			end
			return out
		end)
end

local function readContainer(bag, out, where)
	local size = C_Container and num(try(C_Container.GetContainerNumSlots, bag)) or 0
	for slot = 1, size do
		local it = try(C_Container.GetContainerItemInfo, bag, slot)
		if it and num(it.itemID) then out[#out + 1] = { where, slot, it.itemID, num(it.stackCount) or 1 } end
	end
	return size
end
local LAST_BAG = NUM_TOTAL_EQUIPPED_BAG_SLOTS or NUM_BAG_SLOTS or 4

-- Bolsas: lo que llevas encima ([bolsa, casilla, objeto, cantidad]) y qué bolsas tienes puestas ([bolsa, objeto, casillas]).
R.bolsas = function()
	local out, worn = {}, {}
	for bag = 0, LAST_BAG do
		local size = readContainer(bag, out, bag)
		if bag > 0 and C_Container and C_Container.ContainerIDToInventoryID then
			local inv = try(C_Container.ContainerIDToInventoryID, bag)
			local id = inv and num(try(GetInventoryItemID, "player", inv))
			if id then worn[#worn + 1] = { bag, id, size } end
		end
	end
	if KEYRING_CONTAINER and try(HasKey) then readContainer(KEYRING_CONTAINER, out, KEYRING_CONTAINER) end
	snap.bolsas, snap.bolsasPuestas = out, worn
end

-- Equipo puesto: [hueco, objeto, enlace del objeto sin el nombre (encantamientos, etc.)].
R.equipo = function()
	local out = {}
	for slot = 1, (INVSLOT_LAST_EQUIPPED or 19) do
		local id = num(try(GetInventoryItemID, "player", slot))
		if id then
			local link = str(try(GetInventoryItemLink, "player", slot))
			out[#out + 1] = { slot, id, link and link:match("|H(item:[%-%d:]+)|h") or "" }
		end
	end
	snap.equipo = out
end

R.dinero = function() snap.dinero = num(try(GetMoney)) end

R.monedas = function()
	local C = C_CurrencyInfo
	if not (C and C.GetCurrencyListSize and C.GetCurrencyListInfo) then return end
	snap.monedas = withExpanded(C.GetCurrencyListSize, C.GetCurrencyListInfo,
		function(i) C.ExpandCurrencyList(i, true) end, function(i) C.ExpandCurrencyList(i, false) end,
		function(d) return d.isHeaderExpanded == false end,
		function(n)
			local out = {}
			for i = 1, n do
				local d = try(C.GetCurrencyListInfo, i)
				if d and not d.isHeader then
					local id = num(d.currencyID)
					if not id and C.GetCurrencyListLink and C.GetCurrencyIDFromLink then
						id = num(try(C.GetCurrencyIDFromLink, try(C.GetCurrencyListLink, i)))
					end
					if id and num(d.quantity) and d.quantity > 0 then out[#out + 1] = { id, d.quantity } end
				end
			end
			return out
		end)
end

-- Reputaciones: [facción, reacción (1 odiado … 8 exaltado), puntos, inicio del nivel, fin del nivel].
R.reputaciones = function()
	local C = C_Reputation
	if not (C and C.GetNumFactions and C.GetFactionDataByIndex) then return end
	snap.reputaciones = withExpanded(C.GetNumFactions, C.GetFactionDataByIndex, C.ExpandFactionHeader, C.CollapseFactionHeader,
		function(d) return d.isCollapsed end,
		function(n)
			local out = {}
			for i = 1, n do
				local d = try(C.GetFactionDataByIndex, i)
				if d and num(d.factionID) and d.factionID > 0 and (not d.isHeader or d.isHeaderWithRep) then
					out[#out + 1] = { d.factionID, num(d.reaction) or 0, num(d.currentStanding) or 0, num(d.currentReactionThreshold) or 0, num(d.nextReactionThreshold) or 0 }
				end
			end
			return out
		end)
end

-- Colecciones: monturas (identificador de montura), mascotas (especie) y juguetes (objeto).
-- Mascotas y juguetes salen según los filtros de su ventana: si tienes alguno filtrado, puede faltar.
R.colecciones = function()
	if C_MountJournal and C_MountJournal.GetMountIDs then
		local out = {}
		for _, id in ipairs(try(C_MountJournal.GetMountIDs) or {}) do
			local r = pack(try(C_MountJournal.GetMountInfoByID, id))
			if r[11] == true then out[#out + 1] = id end
		end
		snap.monturas = out
	end
	if C_PetJournal and C_PetJournal.GetNumPets then
		local out, seen = {}, {}
		local total = num(try(C_PetJournal.GetNumPets)) or 0
		for i = 1, total do
			local petID, speciesID = try(C_PetJournal.GetPetInfoByIndex, i)
			if petID and num(speciesID) and not seen[speciesID] then seen[speciesID] = true; out[#out + 1] = speciesID end
		end
		table.sort(out)
		snap.mascotas = out
	end
	if C_ToyBox and C_ToyBox.GetNumFilteredToys and C_ToyBox.GetToyFromIndex then
		local out = {}
		for i = 1, (num(try(C_ToyBox.GetNumFilteredToys)) or 0) do
			local id = num(try(C_ToyBox.GetToyFromIndex, i))
			if id and id > 0 and try(PlayerHasToy, id) then out[#out + 1] = id end
		end
		table.sort(out)
		snap.juguetes = out
	end
	-- Transfiguraciones (1.3.1; diseno-addon-1.3.md 6.4.2; funciones probadas en el juego el 2026-11-18 con /run: GetCategoryAppearances(1)
	-- devuelve 245 apariencias): por categoría de apariencia (1 a 30, las que existan), cuántas hay (n) y las que el personaje tiene
	-- (tengo: visualID con isCollected). Sirve para la prueba cuenta/personaje (6.4) y, después, para enseñarlas en la web.
	if C_TransmogCollection and C_TransmogCollection.GetCategoryAppearances then
		local out, alguna = {}, false
		for cat = 1, 30 do
			local lista = try(C_TransmogCollection.GetCategoryAppearances, cat)
			if type(lista) == "table" and #lista > 0 then
				local tengo = {}
				for _, a in ipairs(lista) do
					local id = type(a) == "table" and num(a.visualID) or num(a)
					if id and (type(a) ~= "table" or a.isCollected == true) then tengo[#tengo + 1] = id end
				end
				table.sort(tengo)
				-- 1.3.3: si la ventana de apariencias ha recortado la lista de esta categoría, «n» es el total que da el juego.
				local recortada, total = FT.listaRecortada(cat, lista)
				out[tostring(cat)] = { n = recortada and total or #lista, tengo = tengo }
				alguna = true
			end
		end
		if alguna then snap.transfiguraciones = out end
	end
end

-- Talentos: especialización activa, el código de la configuración (el que se copia para compartir) y los nodos elegidos
-- ([nodo, opción, rangos]).
R.talentos = function()
	local t = {}
	local spec = try(C_SpecializationInfo and C_SpecializationInfo.GetSpecialization or GetSpecialization)
	if num(spec) then
		local id, name = try(C_SpecializationInfo and C_SpecializationInfo.GetSpecializationInfo or GetSpecializationInfo, spec)
		t.spec, t.nombreSpec = num(id), str(name)
	end
	local cfg = C_ClassTalents and num(try(C_ClassTalents.GetActiveConfigID))
	if cfg and C_Traits then
		t.codigo = str(try(C_Traits.GenerateImportString, cfg))
		local info = try(C_Traits.GetConfigInfo, cfg)
		local nodes = {}
		for _, tree in ipairs(info and info.treeIDs or {}) do
			for _, node in ipairs(try(C_Traits.GetTreeNodes, tree) or {}) do
				local n = try(C_Traits.GetNodeInfo, cfg, node)
				if n and num(n.ranksPurchased) and n.ranksPurchased > 0 and #nodes < 500 then
					nodes[#nodes + 1] = { node, n.activeEntry and num(n.activeEntry.entryID) or 0, n.ranksPurchased }
				end
			end
		end
		t.nodos = nodes
	end
	t.grupo = num(try(C_SpecializationInfo and C_SpecializationInfo.GetActiveSpecGroup or GetActiveSpecGroup))
	snap.talentos = t
end

-- Estadísticas (desde la 1.2.0): lo mismo que usa la ventana de personaje del juego para sus recuadros
-- (Blizzard_UIPanels_Game/Camelot/PaperDollFrameStats.lua), incluidas las cifras que el juego calcula por dentro y la web
-- no puede saber: poder de ataque por fuerza y agilidad, crítico por agilidad e intelecto, regeneración por espíritu,
-- vida por aguante y lo que para la armadura.
local function nums(...)
	local r, out = pack(...), {}
	for i = 1, r.n do out[i] = num(r[i]) or NULL end
	return out
end
R.estadisticas = function()
	local s = {}
	local level = num(try(UnitLevel, "player"))
	s.nivel = level
	s.principales = {}
	for i = 1, 5 do
		local base, eff, pos, neg = try(UnitStat, "player", i)
		s.principales[i] = nums(base, eff, pos, neg)
	end
	local fue, agi, int = num(s.principales[1][2]), num(s.principales[2][2]), num(s.principales[4][2])
	if fue then s.apFuerza = num(try(GetAttackPowerForStat, 1, fue)) end
	if agi then
		s.apAgilidad = num(try(GetAttackPowerForStat, 2, agi))
		s.apDistanciaAgilidad = num(try(GetRangedAttackPowerForStat, 2, agi))
		local c = num(try(GetCritChanceFromStat, 2, agi))
		s.criticoAgilidad = c and c * 100
	end
	if int then
		local c = num(try(GetSpellCritChanceFromStat, 4, int))
		s.criticoIntelecto = c and c * 100
	end
	local d = num(try(GetDodgeChanceFromAttribute))
	s.esquivarAgilidad = d and d * 100
	s.vidaPorAguante = num(try(UnitHPPerStamina, "player"))
	s.regenEspiritu = nums(try(GetHealthRegenFromSpirit), try(GetManaRegenFromSpirit))
	s.regenVida = num(try(GetHealthRegen))
	s.regenMana = nums(try(GetManaRegen))
	s.salud = num(try(UnitHealthMax, "player"))
	local _, token = try(UnitPowerType, "player")
	s.tipoPoder, s.poder = str(token), num(try(UnitPowerMax, "player"))
	s.mana = num(try(UnitPowerMax, "player", 0))
	s.armadura = nums(try(UnitArmor, "player"))
	local eff = s.armadura[2]
	if num(eff) and level and C_PaperDollInfo and C_PaperDollInfo.GetArmorEffectiveness then
		local a = num(try(C_PaperDollInfo.GetArmorEffectiveness, eff, level))
		s.armaduraReduce = a and a * 100
	end
	s.defensa = nums(try(UnitDefenseSkill, "player"))
	s.esquivar, s.parar, s.bloquear = num(try(GetDodgeChance)), num(try(GetParryChance)), num(try(GetBlockChance))
	s.valorBloqueo = num(try(GetShieldBlock))
	s.poderAtaque = nums(try(UnitAttackPower, "player"))
	s.poderAtaqueDistancia = nums(try(UnitRangedAttackPower, "player"))
	s.critico = nums(try(GetCritChance), try(GetRangedCritChance), try(GetSpellCritChance, 2))
	local hit = function(cr, mod) return (num(try(GetCombatRatingBonus, cr)) or 0) + (num(try(mod)) or 0) end
	if CR_HIT_MELEE then
		s.golpe = { hit(CR_HIT_MELEE, GetHitModifier), hit(CR_HIT_RANGED, GetRangedHitModifier), hit(CR_HIT_SPELL, GetSpellHitModifier), hit(CR_HIT_MELEE, GetOffhandHitModifier) }
	end
	local rb, ra = try(GetRangedHaste)
	s.celeridad = { num(try(GetMeleeHaste)) or 0, (num(rb) or 0) + (num(ra) or 0), num(try(UnitSpellHaste, "player")) or 0 }
	s.danioHechizos = {}
	for school = 2, 7 do s.danioHechizos[school - 1] = num(try(GetSpellBonusDamage, school)) or NULL end
	s.sanacion = num(try(GetSpellBonusHealing))
	s.penetracion = num(try(GetSpellPenetration))
	s.pericia = nums(try(GetExpertise))
	s.danio = nums(try(UnitDamage, "player"))
	s.velocidad = nums(try(UnitAttackSpeed, "player"))
	s.distancia = nums(try(UnitRangedDamage, "player"))
	s.resistencias = {}
	for school = 2, 6 do
		local _, total = try(UnitResistance, "player", school)
		s.resistencias[school - 1] = num(total) or NULL
	end
	s.fecha = now()
	snap.estadisticas = s
end

-- Banco: solo se puede leer con el banco abierto. Se guarda lo que había la última vez ([pestaña, casilla, objeto, cantidad]).
R.banco = function()
	if not bankOpen then return end
	local out, any = {}, false
	local BT = Enum and Enum.BankType
	if C_Bank and C_Bank.FetchPurchasedBankTabData and BT and try(C_Bank.CanViewBank, BT.Character) then
		for t, tab in ipairs(try(C_Bank.FetchPurchasedBankTabData, BT.Character) or {}) do
			if num(tab.ID) then any = true; readContainer(tab.ID, out, t) end
		end
	end
	if not any and BANK_CONTAINER then -- banco de antes (casillas + bolsas del banco)
		if readContainer(BANK_CONTAINER, out, 0) > 0 then any = true end
		for bag = LAST_BAG + 1, LAST_BAG + (NUM_BANKBAGSLOTS or 0) do readContainer(bag, out, bag) end
	end
	if any then ForeverTrackerChar.banco = { fecha = now(), objetos = out } end
end

-- Correo: solo con el buzón abierto. Objetos adjuntos ([objeto, cantidad]) y dinero adjunto; nunca quién lo manda ni el texto.
R.correo = function()
	if not mailOpen then return end
	local out, money = {}, 0
	for i = 1, (num(try(GetInboxNumItems)) or 0) do
		local h = pack(try(GetInboxHeaderInfo, i))
		money = money + (num(h[5]) or 0)
		for j = 1, (ATTACHMENTS_MAX_RECEIVE or 16) do
			local _, itemID, _, count = try(GetInboxItem, i, j)
			if num(itemID) then out[#out + 1] = { itemID, num(count) or 1 } end
		end
	end
	ForeverTrackerChar.correo = { fecha = now(), objetos = out, dinero = money }
end

-- Mascota del cazador: «Adiestramiento de bestias» abre una ventana de instructor con lo que sabe el cazador.
R.mascota = function()
	if not (C_Trainer and C_Trainer.GetTrainerType and Enum and Enum.TrainerType) then return end
	if try(C_Trainer.GetTrainerType) ~= Enum.TrainerType.Pet then return end
	local ids = {}
	for i = 1, (num(try(GetNumTrainerServices)) or 0) do
		local d = C_TooltipInfo and try(C_TooltipInfo.GetTrainerService, i)
		if d and num(d.id) then ids[#ids + 1] = d.id end
	end
	if #ids > 0 then
		table.sort(ids)
		ForeverTrackerChar.mascota = { fecha = now(), hechizos = ids }
	end
end

-- Ventana de una profesión abierta: todas las recetas aprendidas de golpe (complementa la lista de Datos.lua).
-- Nunca la de otro jugador (enlace o hermandad).
R.ventana = function()
	local T = C_TradeSkillUI
	if not (T and T.GetAllRecipeIDs and T.GetRecipeInfo) then return end
	if try(T.IsTradeSkillLinked) or try(T.IsTradeSkillGuild) or try(T.IsNPCCrafting) then return end
	local base = try(T.GetBaseProfessionInfo)
	local prof = base and num(base.professionID)
	if not prof then return end
	local ids = {}
	for _, id in ipairs(try(T.GetAllRecipeIDs) or {}) do
		local r = try(T.GetRecipeInfo, id)
		if r and r.learned and num(id) then ids[#ids + 1] = id end
	end
	if #ids > 0 then
		table.sort(ids)
		ForeverTrackerChar.ventanas = ForeverTrackerChar.ventanas or {}
		ForeverTrackerChar.ventanas[tostring(prof)] = { fecha = now(), ids = ids }
	end
end

-- Desde la 1.3.0 (diseno-addon-1.3.md, 6.2): idioma del cliente, versión del juego, modo de juego y la lista de addons del jugador
-- (nombre, versión, activado; nunca su configuración: decisión del 2026-11-17).
R.extra = function()
	snap.idioma = str(try(GetLocale))
	local version, build = try(GetBuildInfo)
	snap.version, snap.build = str(version), tonumber(build)
	snap.modo = C_GameRules and num(try(C_GameRules.GetActiveGameMode)) or NULL
	local A = C_AddOns
	if A and A.GetNumAddOns and A.GetAddOnInfo then
		local out = {}
		for i = 1, math.min(num(try(A.GetNumAddOns)) or 0, 300) do
			local name = str(try(A.GetAddOnInfo, i))
			if name then
				local estado = A.GetAddOnEnableState and num(try(A.GetAddOnEnableState, name)) or NULL
				out[#out + 1] = { name, str(try(A.GetAddOnMetadata, name, "Version")) or NULL, estado }
			end
		end
		snap.addons = out
	end
end

-- Toda la ventana de estadísticas del juego (6.2; comprobado en el juego que las tres funciones existen en Forever):
-- por categoría, [número, nombre, valor]. En Forever no hay logros como tales (puntos de legado): no se tocan.
R.estadisticasJuego = function()
	local cats = try(GetStatisticsCategoryList)
	if type(cats) ~= "table" then return end
	local out = {}
	for _, cat in ipairs(cats) do
		local n = num(try(GetCategoryNumAchievements, cat)) or 0
		local l = {}
		for i = 1, math.min(n, 300) do
			local id, nombre = try(GetAchievementInfo, cat, i)
			if num(id) then l[#l + 1] = { id, str(nombre) or NULL, str(try(GetStatistic, id)) or NULL } end
		end
		out[tostring(cat)] = l
	end
	snap.estadisticasJuego = out
end

local ALL = { "identidad", "misiones", "hechizos", "profesiones", "bolsas", "equipo", "dinero", "monedas", "reputaciones", "colecciones", "talentos", "estadisticas", "extra", "estadisticasJuego" }

-- ───────── montar y guardar el JSON ─────────
local function build()
	if not guid or not ForeverTrackerDB then return end
	local c = ForeverTrackerChar or {}
	local d = {}
	for k, v in pairs(snap) do d[k] = v end
	d.formato, d.addon, d.datos, d.fecha, d.guid = FORMATO, VERSION, FT.DATOS, now(), guid
	d.banco = c.banco or NULL
	d.correo = c.correo or NULL
	d.mascotaCazador = c.mascota or NULL
	d.recetasVentana = c.ventanas or {}
	-- Desde la 1.3.0: la cronología del personaje (Mundo.lua, dato del jugador) y el resumen del recopilador (para Ajustes de la web).
	d.cronologia = c.cronologia or {}
	if FT.R then
		d.recopilador = try(FT.R.resumen) or NULL
		d.sesion = try(FT.R.sesion) or NULL
	end
	if next(errors) then d.errores = errors end
	local buf = {}
	encode(d, buf)
	ForeverTrackerDB.chars[guid] = table.concat(buf)
end

local function run()
	if inCombat() then waitingCombat = true; return end
	local parts = dirty
	dirty = {}
	for part in pairs(parts) do
		local ok, err = pcall(R[part])
		if ok then errors[part] = nil else errors[part] = tostring(err):sub(1, 200) end
	end
	build()
end

local function schedule(parts, delay)
	for _, p in ipairs(parts) do dirty[p] = true end
	if pending then return end
	pending = true
	C_Timer.After(delay or 2, function() pending = false; run() end)
end

-- ───────── eventos ─────────
local frame = CreateFrame("Frame")
local EVENTS = {
	PLAYER_ENTERING_WORLD = { ALL, 4 },
	QUEST_TURNED_IN = { { "misiones", "dinero", "bolsas", "identidad" }, 1.5 },
	QUEST_LOG_UPDATE = { { "misiones" }, 5 },
	SPELLS_CHANGED = { { "hechizos" }, 3 },
	LEARNED_SPELL_IN_SKILL_LINE = { { "hechizos" }, 2 },
	LEARNED_SPELL_IN_TAB = { { "hechizos" }, 2 },
	NEW_RECIPE_LEARNED = { { "hechizos" }, 2 },
	SKILL_LINES_CHANGED = { { "profesiones" }, 3 },
	PLAYER_LEVEL_UP = { { "identidad", "hechizos", "estadisticas" }, 2 },
	CHARACTER_POINTS_CHANGED = { { "talentos", "estadisticas" }, 3 },
	COMBAT_RATING_UPDATE = { { "estadisticas" }, 5 },
	PLAYER_XP_UPDATE = { { "identidad" }, 10 },
	PLAYER_GUILD_UPDATE = { { "identidad" }, 3 },
	ZONE_CHANGED_NEW_AREA = { { "identidad" }, 3 },
	BAG_UPDATE_DELAYED = { { "bolsas" }, 3 },
	PLAYER_EQUIPMENT_CHANGED = { { "equipo", "bolsas", "estadisticas" }, 3 },
	PLAYER_MONEY = { { "dinero" }, 3 },
	CURRENCY_DISPLAY_UPDATE = { { "monedas" }, 5 },
	UPDATE_FACTION = { { "reputaciones" }, 5 },
	NEW_MOUNT_ADDED = { { "colecciones" }, 3 },
	NEW_PET_ADDED = { { "colecciones" }, 3 },
	NEW_TOY_ADDED = { { "colecciones" }, 3 },
	TRAIT_CONFIG_UPDATED = { { "talentos", "estadisticas" }, 3 },
	PLAYER_TALENT_UPDATE = { { "talentos", "estadisticas" }, 3 },
	ACTIVE_TALENT_GROUP_CHANGED = { { "talentos", "hechizos" }, 3 },
	PLAYERBANKSLOTS_CHANGED = { { "banco" }, 0.5 },
	MAIL_INBOX_UPDATE = { { "correo" }, 0.5 },
	TRAINER_SHOW = { { "mascota" }, 0.5 },
	TRAINER_UPDATE = { { "mascota" }, 0.5 },
	TRADE_SKILL_SHOW = { { "ventana" }, 1 },
	TRADE_SKILL_LIST_UPDATE = { { "ventana" }, 1 },
}
-- Avisos que provoca el propio addon al abrir cabeceras: no cuentan.
local MUTABLE = { SKILL_LINES_CHANGED = true, UPDATE_FACTION = true, CURRENCY_DISPLAY_UPDATE = true }

local function register(ev)
	if C_EventUtils and C_EventUtils.IsEventValid and not C_EventUtils.IsEventValid(ev) then return end
	pcall(frame.RegisterEvent, frame, ev)
end
register("ADDON_LOADED")
register("PLAYER_LOGOUT")
register("PLAYER_REGEN_ENABLED")
register("BANKFRAME_OPENED")
register("BANKFRAME_CLOSED")
register("PLAYER_INTERACTION_MANAGER_FRAME_SHOW")
register("PLAYER_INTERACTION_MANAGER_FRAME_HIDE")
register("MAIL_SHOW")
register("MAIL_CLOSED")
for ev in pairs(EVENTS) do register(ev) end

local BANKER = Enum and Enum.PlayerInteractionType and Enum.PlayerInteractionType.Banker
local MAILBOX = Enum and Enum.PlayerInteractionType and Enum.PlayerInteractionType.MailInfo

frame:SetScript("OnEvent", function(_, ev, arg1)
	if ev == "ADDON_LOADED" then
		if arg1 ~= ADDON then return end
		if type(ForeverTrackerDB) ~= "table" or ForeverTrackerDB.formato ~= FORMATO or type(ForeverTrackerDB.chars) ~= "table" then
			ForeverTrackerDB = { formato = FORMATO, chars = {} }
		end
		if type(ForeverTrackerChar) ~= "table" then ForeverTrackerChar = {} end
		ForeverTrackerDB.addon = VERSION
		return
	end
	if ev == "PLAYER_LOGOUT" then
		-- Solo se monta el JSON con lo ya leído. No se vuelve a leer nada: al salir del juego (no con /reload), el juego ya ha
		-- vaciado el personaje y devuelve ceros (visto el 2026-11-11 con la 1.2.0: 0 misiones hechas, estadísticas, equipo,
		-- bolsas y dinero a 0), que pisaban los datos buenos. Lo que cambie justo antes ya lo han apuntado sus avisos.
		pcall(build)
		return
	end
	if ev == "PLAYER_REGEN_ENABLED" then
		if waitingCombat then waitingCombat = false; schedule({}, 1) end
		return
	end
	if ev == "BANKFRAME_OPENED" or (ev == "PLAYER_INTERACTION_MANAGER_FRAME_SHOW" and BANKER and arg1 == BANKER) then
		bankOpen = true; schedule({ "banco" }, 0.5); return
	end
	if ev == "BANKFRAME_CLOSED" or (ev == "PLAYER_INTERACTION_MANAGER_FRAME_HIDE" and BANKER and arg1 == BANKER) then
		bankOpen = false; return
	end
	if ev == "MAIL_SHOW" or (ev == "PLAYER_INTERACTION_MANAGER_FRAME_SHOW" and MAILBOX and arg1 == MAILBOX) then
		mailOpen = true; schedule({ "correo" }, 1); return
	end
	if ev == "MAIL_CLOSED" or (ev == "PLAYER_INTERACTION_MANAGER_FRAME_HIDE" and MAILBOX and arg1 == MAILBOX) then
		mailOpen = false; return
	end
	if muted and MUTABLE[ev] then return end
	local e = EVENTS[ev]
	if not e then return end
	if ev == "BAG_UPDATE_DELAYED" and bankOpen then schedule({ "banco" }, 0.5) end
	schedule(e[1], e[2])
end)

-- ───────── /ft: qué se ha guardado ─────────
-- Desde la 1.3.0 el addon no escribe en el chat (decisión del 2026-11-16): /ft abre un panel pequeño con el estado y una
-- pestaña «Privacidad» con el interruptor de la base común (diseno-addon-1.3.md, 10.8). Las líneas se juntan y se enseñan ahí.
local REGIONS = { [1] = "us", [2] = "kr", [3] = "eu", [4] = "tw" }
local lineas = {}
local function say(text) lineas[#lineas + 1] = text end
local function count(t) return type(t) == "table" and #t or 0 end
local function when(t) return t and date("%d/%m %H:%M", t) or "nunca" end
local function money(c)
	c = c or 0
	return string.format("%d o %d p %d c", math.floor(c / 10000), math.floor(c % 10000 / 100), c % 100)
end

local function report()
	lineas = {}
	if not guid then say("Todavía no sé quién eres: espera unos segundos y vuelve a escribir /ft."); return lineas end
	local server, hex = guid:match("^Player%-(%d+)%-(%x+)$")
	local region = REGIONS[snap.region or 0] or "?"
	say(string.format("%s %s · nivel %s · número de la armería: %s-%s", snap.nombre or "?", snap.apellido or "", tostring(snap.nivel or "?"),
		region, hex and tostring(tonumber(hex, 16)) or "?"))
	local inList = {}
	for name, list in pairs({ recetas = FT.RECETAS, habilidades = FT.HABILIDADES, armas = FT.ARMAS, mascota = FT.MASCOTA }) do
		local set = {}
		for _, id in ipairs(list or {}) do set[id] = true end
		local n = 0
		for _, id in ipairs(snap.hechizos or {}) do if set[id] then n = n + 1 end end
		inList[name] = n
	end
	say(string.format("misiones hechas %d · en curso %d", count(snap.misiones), count(snap.misionesEnCurso)))
	say(string.format("conoces %d recetas, %d habilidades, %d armas y %d de mascota de la lista de la web",
		inList.recetas, inList.habilidades, inList.armas, inList.mascota))
	-- Forever da cada profesión con dos números (por ejemplo Herrería 164 y 2938): se enseña una vez.
	local profs, seenProf = {}, {}
	for _, p in ipairs(snap.profesiones or {}) do
		local text = p.max and p.max > 1 and ((p.nombre or p.id) .. " " .. (p.nivel or 0) .. "/" .. p.max)
		if text and not seenProf[text] then seenProf[text] = true; profs[#profs + 1] = text end
	end
	say("habilidades: " .. (#profs > 0 and table.concat(profs, ", ") or "ninguna leída"))
	local c = ForeverTrackerChar or {}
	say(string.format("bolsas %d objetos · equipo %d · dinero %s", count(snap.bolsas), count(snap.equipo), money(snap.dinero)))
	say(string.format("reputaciones %d · monturas %d · mascotas %d · juguetes %d · monedas %d", count(snap.reputaciones),
		count(snap.monturas), count(snap.mascotas), count(snap.juguetes), count(snap.monedas)))
	say("banco: " .. (c.banco and ("visto el " .. when(c.banco.fecha) .. ", " .. count(c.banco.objetos) .. " objetos") or "ábrelo una vez para que lo apunte"))
	say("correo: " .. (c.correo and ("visto el " .. when(c.correo.fecha)) or "abre el buzón una vez para que lo apunte"))
	if snap.clase == "HUNTER" then
		say("mascota: " .. (c.mascota and ("visto el " .. when(c.mascota.fecha) .. ", " .. count(c.mascota.hechizos) .. " habilidades") or "abre «Adiestramiento de bestias» una vez para que lo apunte"))
	end
	if FT.vistosResumen then
		local n, q = FT.vistosResumen()
		say(string.format("jugadores que has visto: %d · de /quién: %d", n, q))
		if FT.vistosDiag then say(FT.vistosDiag()) end
	end
	if FT.R and FT.R.resumen then
		local r = try(FT.R.resumen)
		if r then
			say(string.format("base común: %s · lotes guardados %d (%d esperando al programa, %.1f MB) · en memoria %d",
				r.interruptor and "aportando datos" or "APAGADO", r.lotes or 0, r.pendientes or 0, (r.bytes or 0) / 1048576, r.enMemoria or 0))
			if r.encargos then say(string.format("encargos de la web (%s): %d fichas pedidas, %d lecturas locales%s", tostring(r.encargos.lista or r.lista or "?"),
				r.encargos.hechas or 0, r.encargos.locales or 0, r.encargos.terminado and " · hecho" or " · en marcha")) end
		end
	end
	local errs = {}
	for k, v in pairs(errors) do errs[#errs + 1] = k .. ": " .. v end
	if #errs > 0 then say("|cffff6060no se pudo leer|r " .. table.concat(errs, " · ")) end
	say("El juego lo escribe en el archivo al cerrar sesión, al salir o con /reload; el programa de Windows lo sube solo.")
	return lineas
end
FT.informe = report -- para las pruebas

-- El panel (una ventana pequeña del juego). Si algo de la interfaz no existiera, no se enseña nada: nunca al chat.
local panel
local function construyePanel()
	if panel or not UIParent then return panel end
	local f = CreateFrame("Frame", "ForeverTrackerPanel", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
	f:SetSize(460, 280)
	f:SetPoint("CENTER")
	f:SetFrameStrata("DIALOG")
	f:SetMovable(true)
	f:EnableMouse(true)
	f:RegisterForDrag("LeftButton")
	f:SetScript("OnDragStart", f.StartMoving)
	f:SetScript("OnDragStop", f.StopMovingOrSizing)
	if f.SetBackdrop then
		f:SetBackdrop({ bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background", edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Border",
			tile = true, tileSize = 32, edgeSize = 32, insets = { left = 11, right = 12, top = 12, bottom = 11 } })
	end
	f.title = f:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
	f.title:SetPoint("TOP", 0, -16)
	f.title:SetText("Forever Tracker " .. VERSION)
	f.body = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
	f.body:SetPoint("TOPLEFT", 20, -44)
	f.body:SetPoint("BOTTOMRIGHT", -20, 50)
	f.body:SetJustifyH("LEFT")
	f.body:SetJustifyV("TOP")
	f.body:SetWordWrap(true)
	local close = CreateFrame("Button", nil, f, "UIPanelCloseButton")
	close:SetPoint("TOPRIGHT", -4, -4)
	f.tabEstado = CreateFrame("Button", nil, f, "UIPanelButtonTemplate")
	f.tabEstado:SetSize(100, 22)
	f.tabEstado:SetPoint("BOTTOMLEFT", 16, 14)
	f.tabEstado:SetText("Estado")
	f.tabPriv = CreateFrame("Button", nil, f, "UIPanelButtonTemplate")
	f.tabPriv:SetSize(100, 22)
	f.tabPriv:SetPoint("LEFT", f.tabEstado, "RIGHT", 6, 0)
	f.tabPriv:SetText("Privacidad")
	f.check = CreateFrame("CheckButton", nil, f, "UICheckButtonTemplate")
	f.check:SetPoint("TOPLEFT", 20, -120)
	local etiqueta = f.check.Text or f.check.text
	if etiqueta then etiqueta:SetText("Aportar datos del juego a la base común de Forever Tracker") end
	f.check:SetScript("OnClick", function(self) if FT.R and FT.R.setInterruptor then FT.R.setInterruptor(self:GetChecked() and true or false) end end)
	f.check:Hide()
	f.tabEstado:SetScript("OnClick", function() f.check:Hide(); f.body:SetText(table.concat(lineas, "\n")) end)
	f.tabPriv:SetScript("OnClick", function()
		f.body:SetText("Lo que ves jugando (vendedores, misiones, botín, precios…) se junta de forma anónima con lo de los demás jugadores para la base de datos del juego de la web. "
			.. "Nunca el chat, el correo ni nada de tu PC. Cómo funciona y cómo se apaga: política de privacidad de la web.\n\n\n\n"
			.. "Tu progreso (lo que ves de tus personajes en la web) se sigue guardando igual.")
		f.check:SetChecked(FT.R and FT.R.interruptor and FT.R.interruptor() or false)
		f.check:Show()
	end)
	if UISpecialFrames then tinsert(UISpecialFrames, "ForeverTrackerPanel") end
	panel = f
	return f
end
local function abrePanel()
	local ok = pcall(construyePanel)
	if not ok or not panel then return end
	panel.check:Hide()
	panel.body:SetText(table.concat(lineas, "\n"))
	panel:Show()
end

SLASH_FOREVERTRACKER1 = "/ft"
SlashCmdList.FOREVERTRACKER = function()
	if not inCombat() then
		for _, p in ipairs(ALL) do dirty[p] = true end
		run()
	end
	report()
	abrePanel()
end
