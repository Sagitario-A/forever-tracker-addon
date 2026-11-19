-- Forever Tracker: lo que el jugador ve jugando (desde la 1.3.0). Diseño: diseno-addon-1.3.md, apartado 5.
--
-- Cada cosa que el jugador tiene delante (un vendedor, una misión en su PNJ, lo que cae de un enemigo, una mena, un vuelo, una
-- muerte, la subasta…) se apunta como una observación con su contexto, a través del motor de Recopilador.lua (FT.R). Solo fuera de
-- combate salvo lo que solo pasa en combate (jefes, morir). Nunca nombres ni números de otros jugadores: eso va a Vistos.lua.
-- Todo con funciones probadas en el juego.
local ADDON, FT = ...
local R = FT.R
if not R then return end
local try, num, str, b01, inCombat, now, limpio, todos, uno, pack = R.try, R.num, R.str, R.b01, R.inCombat, R.now, R.limpio, R.todos, R.uno, R.pack
local SECRETO = R.SECRETO

-- ───────── utilidades ─────────
-- GUID de criatura u objeto del mundo → tipo («pnj», «objeto_mundo», «objeto») y número de Blizzard. Los de jugador nunca se usan.
local function guidPartes(g)
	if type(g) ~= "string" then return nil end
	local clase, id = g:match("^(%a+)%-%d+%-%d+%-%d+%-%d+%-(%d+)%-%x+$")
	if clase == "Creature" or clase == "Vehicle" or clase == "Pet" then return "pnj", tonumber(id), g end
	if clase == "GameObject" then return "objeto_mundo", tonumber(id), g end
	if g:match("^Item%-") then return "objeto", nil, g end
	return nil
end
local function unitGuid(u)
	local g = str(try(UnitGUID, u))
	if g and g:match("^Player%-") then return nil end
	return g
end
local function enlace(link)
	local s = str(link)
	return s and s:match("|H(item:[%-%d:]+)|h") or nil
end
local function limpiaTexto(s, max)
	s = str(s)
	if not s then return nil end
	s = s:gsub("[%c]", " ")
	return (s:sub(1, max or 4000))
end
-- Vuelve a poner los marcadores que el juego sustituyó por el nombre, la clase y la raza del jugador (M10.4). El género ($G) lo
-- deja la base.
local yo = {}
local function marcadores(s)
	if type(s) ~= "string" then return s end
	if yo.nombre and yo.nombre ~= "" then s = s:gsub(yo.nombre:gsub("%p", "%%%0"), "$N") end
	if yo.clase and yo.clase ~= "" then s = s:gsub(yo.clase:gsub("%p", "%%%0"), "$C") end
	if yo.raza and yo.raza ~= "" then s = s:gsub(yo.raza:gsub("%p", "%%%0"), "$R") end
	return s
end
local function texto(t, como)
	if t.texto then t.texto = marcadores(limpiaTexto(t.texto)) end
	t.idioma = t.idioma or R.idioma()
	R.obs("texto", R.conPos(t, como or "propia"))
end
local function enInstancia()
	local nombre, tipo, dificultad, nombreDif, maxJug, _, _, idInst = try(GetInstanceInfo)
	if str(tipo) and tipo ~= "none" then return { id = num(idInst), nombre = str(nombre), tipo = tipo, dificultad = str(nombreDif) or num(dificultad), tamano = num(maxJug) } end
	return nil
end

-- Lo último que ha pasado, para atribuir ganancias y botín.
local ultimo = { hechizo = {}, muerto = nil, botin = nil, mision = nil }
local function hace(t) return t and t.hora and (GetTime() - t.hora) or 1e9 end

-- ───────── 5.2 enemigos y personajes del juego; 5.6 PNJ con servicio (posición y ejemplar) ─────────
local ejemplares = {} -- GUID → última vez (segundos del reloj del juego) que se apuntó
local function ejemplar(g, como, tipo, id)
	local antes = ejemplares[g]
	if antes and GetTime() - antes < 600 then return end
	ejemplares[g] = GetTime()
	R.obs("ejemplar", R.conPos({ tipo = tipo, id = id, guid = g }, como))
end
local function unidad(u, como)
	if inCombat() or not try(UnitExists, u) then return end
	if try(UnitIsPlayer, u) then return end
	local g = unitGuid(u)
	local tipo, id = guidPartes(g)
	if tipo ~= "pnj" or not id then return end
	ejemplar(g, como, tipo, id)
	local idioma = R.idioma()
	local nombre = str(try(UnitName, u))
	if nombre then R.foto("pnj", id, "nombre", nombre, como, idioma) end
	R.foto("pnj", id, "nivel", num(try(UnitLevel, u)), como, false)
	R.foto("pnj", id, "clasificacion", str(try(UnitClassification, u)), como, false)
	R.foto("pnj", id, "tipo_criatura", str(try(UnitCreatureType, u)), como, idioma)
	R.foto("pnj", id, "familia", str(try(UnitCreatureFamily, u)), como, idioma)
	R.foto("pnj", id, "faccion", str(try(UnitFactionGroup, u)), como, false)
	R.foto("pnj", id, "reaccion", num(try(UnitReaction, "player", u)), como, false)
	if C_UnitAuras and C_UnitAuras.GetAuraDataByIndex then
		local efectos = {}
		for i = 1, 40 do
			local a = try(C_UnitAuras.GetAuraDataByIndex, u, i, "HELPFUL")
			if not a or (issecretvalue and issecretvalue(a)) then break end
			if num(a.spellId) then efectos[#efectos + 1] = a.spellId end
		end
		if #efectos > 0 then table.sort(efectos); R.foto("pnj", id, "efectos", efectos, como, false) end
	end
end

-- ───────── 5.3 combate: medidor al terminar, resumen al morir, jefes ─────────
local muertos = {}
local function objetivoMuere()
	local g = unitGuid("target")
	local tipo, id = guidPartes(g)
	if tipo ~= "pnj" then return end
	ultimo.muerto = { guid = g, id = id, nombre = str(try(UnitName, "target")), nivel = num(try(UnitLevel, "target")),
		clasificacion = str(try(UnitClassification, "target")), hora = GetTime() }
	muertos[g] = ultimo.muerto
end
local function medidorCombate()
	local D = C_DamageMeter
	if type(D) ~= "table" or not D.GetCombatSessionFromType then return end
	local ses = try(D.GetCombatSessionFromType, 1, 10) -- 10 = daño recibido por los enemigos (probado en el juego)
	local recibido = try(D.GetCombatSessionFromType, 1, 11)
	if type(ses) ~= "table" or type(ses.combatSources) ~= "table" then muertos = {}; return end
	local porGuid = {}
	if type(recibido) == "table" and type(recibido.combatSources) == "table" then
		for _, s in ipairs(recibido.combatSources) do if type(s) == "table" and str(s.sourceGUID) then porGuid[s.sourceGUID] = num(s.totalAmount) end end
	end
	local duracion = num(ses.durationSeconds)
	for _, s in ipairs(ses.combatSources) do
		if type(s) == "table" then
			local g = str(s.sourceGUID)
			local tipo, id = guidPartes(g)
			if tipo == "pnj" then
				local m = g and muertos[g]
				R.obs("combate", { guid = g, nombre = str(s.name), nivel_pnj = m and m.nivel or nil, clasificacion = str(s.classification) or (m and m.clasificacion),
					dano_hecho = num(s.totalAmount), dano_recibido = porGuid[g], duracion = duracion, resultado = m and "muerto" or "vivo" })
			end
		end
	end
	muertos = {}
end
local durabilidad = {}
local RANURAS = { 1, 3, 5, 6, 7, 8, 9, 10, 16, 17, 18 }
local function leeDurabilidad()
	local d = {}
	for _, r in ipairs(RANURAS) do
		local cur, max = try(GetInventoryItemDurability, r)
		if num(cur) then d[r] = { cur, num(max) } end
	end
	return d
end
local function muerte()
	local golpes = {}
	if C_DeathRecap and try(C_DeathRecap.HasRecapEvents) then
		local evs = try(C_DeathRecap.GetRecapEvents)
		if type(evs) == "table" then
			for i, e in ipairs(evs) do
				if i > 10 then break end
				if type(e) == "table" then
					golpes[#golpes + 1] = { limpio(e.sourceName) or false, limpio(e.amount) or false, limpio(e.school) or false, limpio(e.timestamp) or false,
						limpio(e.currentHP) or false, limpio(e.spellId) or false, limpio(e.spellName) or false }
				end
			end
		end
	end
	R.obs("combate", R.conPos({ resultado = "muerte", golpes = golpes }, "propia"))
	-- Durabilidad perdida al morir (5.24): la de antes frente a la de después.
	local despues = leeDurabilidad()
	for r, v in pairs(despues) do
		local antes = durabilidad[r]
		if antes and antes[1] > v[1] then R.obs("progreso", { habilidad = nil, de = antes[1], a = v[1], con_tipo = "durabilidad", con_id = r, cantidad = v[2] }) end
	end
	durabilidad = despues
end
local function jefe(ev, encuentro, nombre, dificultad, tamano, ganado)
	local inst = enInstancia()
	R.obs("mazmorra", { mazmorra = inst and inst.id, nombre = inst and inst.nombre, dificultad = str(dificultad) or num(dificultad), tamano = num(tamano),
		jefe = num(encuentro), jefe_nombre = str(nombre), ganado = ev == "ENCOUNTER_END" and b01(ganado == 1 or ganado == true) or nil })
end

-- ───────── 5.4 botín (con su origen exacto), 5.5 menas y cofres, pesca, cajas ─────────
local COMO_HECHIZO = { [2575] = "minar", [2366] = "recolectar", [3365] = "abrir", [8613] = "desollar", [921] = "robar", [13262] = "desencantar", [1804] = "ganzua" }
local HABILIDAD_COMO = { minar = 186, recolectar = 182, desollar = 393, pesca = 356 }
local botines = {} -- GUID → reloj de la última vez
local function habilidadDe(linea)
	local S = C_SkillInfo
	if not (linea and S and S.GetNumSkillLines and S.GetSkillLineInfo) then return nil end
	for i = 1, (num(try(S.GetNumSkillLines)) or 0) do
		local s = try(S.GetSkillLineInfo, i)
		if s and not s.isHeader and num(s.skillID) == linea then return num(s.rank) end
	end
	return nil
end
local function botin()
	if inCombat() then return end
	local n = num(try(GetNumLootItems)) or 0
	local pesca = try(IsFishingLoot) == true
	local como = pesca and "pesca" or nil
	local hechizo = nil
	if not como and hace(ultimo.hechizo) < 8 then hechizo = ultimo.hechizo.id; como = COMO_HECHIZO[hechizo] end
	como = como or "saquear"
	local porOrigen = {}
	local orden = {}
	local dinero = 0
	for i = 1, n do
		local g, cant = try(GetLootSourceInfo, i)
		g = str(g)
		local clave = g or "?"
		if not porOrigen[clave] then porOrigen[clave] = { guid = g, objetos = {} }; orden[#orden + 1] = clave end
		-- GetLootSlotInfo: icono, nombre, cantidad, moneda, calidad, bloqueado, es de misión. Dinero = GetLootSlotType 2.
		local _, nombre, cuantos, _, calidad = try(GetLootSlotInfo, i)
		local link = enlace(try(GetLootSlotLink, i))
		local esDinero = try(GetLootSlotType, i) == 2
		if esDinero then
			-- La línea del botín trae el texto («21 Cobre»); se guarda tal cual y la base lo convierte a cobre.
			porOrigen[clave].dinero = (porOrigen[clave].dinero or "") .. (str(nombre) or "")
		elseif link then
			porOrigen[clave].objetos[#porOrigen[clave].objetos + 1] = { link, num(cant) or num(cuantos) or 1, num(calidad) or false }
		elseif str(nombre) then
			porOrigen[clave].objetos[#porOrigen[clave].objetos + 1] = { nombre, num(cant) or num(cuantos) or 1, num(calidad) or false }
		end
	end
	if n == 0 then
		-- Sin botín: también cuenta (M6.2). El origen es el objetivo o lo último que señalaste.
		local g = unitGuid("target") or (ultimo.muerto and hace(ultimo.muerto) < 30 and ultimo.muerto.guid)
		porOrigen["?"] = { guid = g, objetos = {} }; orden[1] = "?"
	end
	local inst = enInstancia()
	local grupo = num(try(GetNumGroupMembers)) or 0
	local habilidad = habilidadDe(HABILIDAD_COMO[como])
	for _, clave in ipairs(orden) do
		local o = porOrigen[clave]
		local g = o.guid
		if g and botines[g] and GetTime() - botines[g] < 60 then
		else
			if g then botines[g] = GetTime() end
			local tipo, id = guidPartes(g)
			local nombre = nil
			if tipo == "pnj" then nombre = muertos[g] and muertos[g].nombre or (ultimo.muerto and ultimo.muerto.guid == g and ultimo.muerto.nombre) end
			if tipo and g then ejemplar(g, "saquear", tipo, id) end
			R.obs("botin", R.conPos({ origen_guid = g, origen_nombre = nombre, como = pesca and "pesca" or (tipo == "objeto" and "abrir" or como), hechizo = hechizo,
				objetos = o.objetos, dinero = o.dinero, dificultad = inst and inst.dificultad or nil, grupo_n = grupo, habilidad = habilidad }, "saquear"))
		end
	end
	ultimo.botin = { hora = GetTime() }
end
-- Repartos de botín en grupo (probado en el juego): solo el objeto y cuántos tiraron, nunca nombres.
local function reparto()
	if not (C_LootHistory and C_LootHistory.GetAllEncounterInfos) then return end
	local l = try(C_LootHistory.GetAllEncounterInfos)
	if type(l) ~= "table" or type(l[1]) ~= "table" or not l[1].encounterID then return end
	local drops = try(C_LootHistory.GetSortedDropsForEncounter, l[1].encounterID)
	if type(drops) ~= "table" then return end
	for i, d in ipairs(drops) do
		if i > 20 then break end
		if type(d) == "table" and str(d.itemHyperlink) then
			local tiradas = type(d.rollInfos) == "table" and #d.rollInfos or nil
			R.foto("reparto", (num(d.lootListID) or 0) .. ":" .. (l[1].encounterID or 0), enlace(d.itemHyperlink) or "?", { tiradas = tiradas, ganado = b01(d.winner ~= nil) }, nil, false)
		end
	end
end
-- Recuadros de objetos del mundo (menas, hierbas, cofres, campamentos): el nombre, la posición y la línea de la profesión con su color.
local ultimoObjeto = {}
local function recuadroObjeto(tooltip, datos)
	if inCombat() or type(datos) ~= "table" or type(datos.lines) ~= "table" then return end
	local primera = datos.lines[1] and datos.lines[1].leftText
	if type(primera) ~= "string" or (issecretvalue and issecretvalue(primera)) or primera == "" then return end
	ultimoObjeto = { nombre = primera, hora = GetTime() }
	local lineas = {}
	for i, l in ipairs(datos.lines) do
		if i > 6 then break end
		lineas[#lineas + 1] = { limpio(l.leftText) or false, limpio(l.leftColor) or false }
	end
	local g = str(datos.guid)
	local tipo, id = guidPartes(g)
	local idioma = R.idioma()
	-- Sin número en el recuadro: la foto va por nombre; la base la cruza con lo que piquen otros en el mismo sitio (A4.5).
	local t = R.conPos({ tipo = "objeto_mundo", id = id, nombre = primera, guid = g }, "raton")
	R.obs("posicion", t)
	if #lineas > 1 then
		local habilidades = {}
		for _, linea in ipairs({ 186, 182, 393, 356 }) do local h = habilidadDe(linea); if h then habilidades[tostring(linea)] = h end end
		R.foto("objeto_mundo", id or primera, "recuadro", { lineas = lineas, habilidad = habilidades }, nil, idioma)
	end
end

-- ───────── 5.6 personajes con servicio ─────────
local function pnjActual(como)
	local g = unitGuid("npc")
	local tipo, id = guidPartes(g)
	if g and tipo then ejemplar(g, como, tipo, id) end
	return g, tipo, id
end
local function vendedor()
	if inCombat() then return end
	local g, tipo, id = pnjActual("hablar")
	if tipo ~= "pnj" then return end
	local n = num(try(GetMerchantNumItems)) or 0
	local lista = {}
	for i = 1, n do
		local info = try(C_MerchantFrame.GetItemInfo, i)
		local item = num(try(GetMerchantItemID, i))
		if item then
			local costes = nil
			local nCostes = num(try(GetMerchantItemCostInfo, i)) or 0
			if nCostes > 0 then
				costes = {}
				for j = 1, nCostes do costes[j] = todos(GetMerchantItemCostItem, i, j) or false end
			end
			local fila = { item, type(info) == "table" and num(info.price) or false, type(info) == "table" and num(info.stackCount) or false,
				type(info) == "table" and num(info.numAvailable) or false, num(try(GetMerchantItemMaxStack, i)) or false, costes or false,
				type(info) == "table" and b01(info.hasExtendedCost) or false }
			lista[#lista + 1] = fila
		end
	end
	local reputacion = num(try(UnitReaction, "player", "npc"))
	if R.foto("pnj", id, "vende", lista, "hablar", false) then
		for _, f in ipairs(lista) do
			if f[2] and f[2] ~= false then R.obs("precio", { casa = nil, objeto = f[1], precio = f[2], cantidad = f[3] or 1, de = "vendedor", pnj_guid = g, reputacion = reputacion }) end
		end
	end
	if try(CanMerchantRepair) then
		local coste = num(try(GetRepairAllCost))
		if coste and coste > 0 then R.obs("precio", { precio = coste, de = "reparar", pnj_guid = g, reputacion = reputacion }) end
	end
end
local function instructor()
	if inCombat() then return end
	local g, tipo, id = pnjActual("hablar")
	if tipo ~= "pnj" then return end
	local n = num(try(GetNumTrainerServices)) or 0
	local lista = {}
	for i = 1, n do
		local info = todos(GetTrainerServiceInfo, i)
		if info and info[1] ~= false then
			local d = C_TooltipInfo and try(C_TooltipInfo.GetTrainerService, i)
			lista[#lista + 1] = { info, todos(GetTrainerServiceCost, i) or false, todos(GetTrainerServiceSkillReq, i) or false,
				num(try(GetTrainerServiceLevelReq, i)) or false, d and num(d.id) or false }
		end
	end
	R.foto("pnj", id, "ensena", lista, "hablar", R.idioma())
end
local function conversacion()
	if inCombat() or not C_GossipInfo then return end
	local g, tipo, id = pnjActual("hablar")
	if tipo ~= "pnj" then return end
	local t = str(try(C_GossipInfo.GetText))
	if t and t ~= "" then texto({ tipo = "pnj", id = id, guid = g, clase_texto = "conversacion", texto = t }, "hablar") end
	R.foto("pnj", id, "opciones", uno(C_GossipInfo.GetOptions) or {}, nil, R.idioma())
	local da, recibe = {}, {}
	for _, q in ipairs(uno(C_GossipInfo.GetAvailableQuests) or {}) do if type(q) == "table" and num(q.questID) then da[#da + 1] = q.questID end end
	for _, q in ipairs(uno(C_GossipInfo.GetActiveQuests) or {}) do if type(q) == "table" and num(q.questID) then recibe[#recibe + 1] = q.questID end end
	if #da > 0 then R.foto("pnj", id, "da_misiones", da, "hablar", false) end
	if #recibe > 0 then R.foto("pnj", id, "recibe_misiones", recibe, "hablar", false) end
end
local vueloActual = nil
local function maestroVuelo()
	if inCombat() then return end
	local g, tipo, id = pnjActual("hablar")
	local n = num(try(NumTaxiNodes)) or 0
	local nodos, actual = {}, nil
	for i = 1, n do
		local x, y = try(TaxiNodePosition, i)
		local t = str(try(TaxiNodeGetType, i))
		nodos[#nodos + 1] = { str(try(TaxiNodeName, i)) or false, num(try(TaxiNodeCost, i)) or false, t or false, num(x) or false, num(y) or false }
		if t == "CURRENT" then actual = i end
	end
	local mapaTaxi = num(try(GetTaxiMapID))
	local detalle = mapaTaxi and C_TaxiMap and uno(C_TaxiMap.GetTaxiNodesForMap, mapaTaxi) or nil
	if tipo == "pnj" then R.foto("pnj", id, "vuelos", { nodos = nodos, mapa = mapaTaxi, detalle = detalle }, "hablar", R.idioma()) end
	vueloActual = { desde = actual and nodos[actual] and nodos[actual][1] or nil, pnj = id, nodos = nodos }
end
local function tomaVuelo(i)
	if not vueloActual then return end
	vueloActual.hasta = str(try(TaxiNodeName, i))
	vueloActual.coste = num(try(TaxiNodeCost, i))
	vueloActual.pedido = GetTime()
end
local function control(ev)
	local enTaxi = try(UnitOnTaxi, "player") == true
	if ev == "PLAYER_CONTROL_LOST" and enTaxi and vueloActual and vueloActual.hasta then
		vueloActual.inicio = GetTime()
	elseif ev == "PLAYER_CONTROL_GAINED" and vueloActual and vueloActual.inicio then
		R.obs("viaje", { clase_viaje = "vuelo", de_nodo = vueloActual.desde, a_nodo = vueloActual.hasta, coste = vueloActual.coste, duracion = math.floor(GetTime() - vueloActual.inicio) })
		vueloActual = nil
	end
end
local function hogar()
	local g, tipo, id = pnjActual("hablar")
	R.obs("viaje", R.conPos({ clase_viaje = "hogar", de_nodo = tipo == "pnj" and id or nil, a_nodo = str(try(GetBindLocation)) }, "hablar"))
end
local function buzon()
	local g, tipo, id = pnjActual("hablar")
	if tipo == "objeto_mundo" then R.obs("posicion", R.conPos({ tipo = tipo, id = id, nombre = "buzon", guid = g }, "hablar")) end
end

-- ───────── 5.7 / 4.4 misiones en el PNJ ─────────
local function recompensas()
	local fijas, elegir = {}, {}
	for i = 1, (num(try(GetNumQuestRewards)) or 0) do
		local r = todos(GetQuestItemInfo, "reward", i)
		if r then fijas[#fijas + 1] = r end
	end
	for i = 1, (num(try(GetNumQuestChoices)) or 0) do
		local r = todos(GetQuestItemInfo, "choice", i)
		if r then elegir[#elegir + 1] = r end
	end
	-- Regla 15: con 0 opciones, nil (no observado), nunca lista vacía.
	return fijas, (#elegir > 0) and elegir or nil
end
local function misionPNJ(ev)
	if inCombat() then return end
	local g, tipo, id = pnjActual("hablar")
	local q = num(try(GetQuestID))
	if not q or q <= 0 then return end
	local clase = ev == "QUEST_DETAIL" and "detalle" or ev == "QUEST_PROGRESS" and "progreso" or "entrega"
	local fijas, elegir = recompensas()
	local v = { titulo = str(try(GetTitleText)), pnj = tipo == "pnj" and id or nil, pnj_tipo = tipo, dinero = num(try(GetRewardMoney)), xp = num(try(GetRewardXP)),
		fijas = fijas, elegir = elegir, nivel = R.nivel() }
	if ev == "QUEST_DETAIL" then v.objetivos = marcadores(limpiaTexto(try(GetObjectiveText)))
	elseif ev == "QUEST_PROGRESS" then
		v.pide = {}
		for i = 1, (num(try(GetNumQuestItems)) or 0) do v.pide[#v.pide + 1] = todos(GetQuestItemInfo, "required", i) or false end
	end
	R.foto("mision", q, clase, v, "hablar", R.idioma())
	if tipo == "pnj" and id then R.foto("mision", q, clase == "detalle" and "la_da" or "la_recibe", id, "hablar", false) end
	local t = ev == "QUEST_DETAIL" and try(GetQuestText) or ev == "QUEST_PROGRESS" and try(GetProgressText) or try(GetRewardText)
	if str(t) and t ~= "" then texto({ tipo = "mision", id = q, guid = g, clase_texto = clase, texto = t }, "hablar") end
	ultimo.mision = { id = q, hora = GetTime() }
end
local function misionAceptada(questID)
	local q = num(questID)
	if not q then return end
	R.obs("progreso", { con_tipo = "mision_aceptada", con_id = q })
	ultimo.mision = { id = q, hora = GetTime() }
	local mapa = C_Map and num(try(C_Map.GetBestMapForUnit, "player"))
	local l = mapa and C_QuestLog and uno(C_QuestLog.GetQuestsOnMap, mapa)
	if type(l) == "table" then
		for _, m in ipairs(l) do
			if type(m) == "table" and num(m.questID) == q then R.foto("mision", q, "en_mapa", { mapa = mapa, x = m.x, y = m.y }, nil, false) end
		end
	end
	R.estado()
end
local function misionEntregada(questID, xp, dinero)
	local q = num(questID)
	if not q then return end
	R.obs("progreso", { con_tipo = "mision_entregada", con_id = q })
	if num(xp) and xp > 0 then R.obs("ganancia", { clase_ganancia = "xp", cantidad = xp, de_tipo = "mision", de_id = q }) end
	if num(dinero) and dinero > 0 then R.obs("ganancia", { clase_ganancia = "dinero", cantidad = dinero, de_tipo = "mision", de_id = q }) end
	ultimo.mision = { id = q, hora = GetTime(), entregada = true }
	R.estado()
end
local function avance(tipo, msg)
	local t = str(msg)
	if not t or t == "" then return end
	texto({ tipo = "mision", id = ultimo.mision and hace(ultimo.mision) < 600 and ultimo.mision.id or nil, clase_texto = "avance", texto = t }, "propia")
end

-- ───────── 5.8 profesiones ─────────
local PROF_LINEAS = { 171, 164, 333, 202, 165, 197, 186, 185, 129, 182, 393, 356, 755 }
local habilidades = {} -- línea → nivel
local function leeHabilidades()
	local S = C_SkillInfo
	if not (S and S.GetNumSkillLines and S.GetSkillLineInfo) then return {} end
	local out = {}
	for i = 1, (num(try(S.GetNumSkillLines)) or 0) do
		local s = try(S.GetSkillLineInfo, i)
		if s and not s.isHeader and num(s.skillID) and num(s.rank) then out[s.skillID] = { s.rank, num(s.maxRank), str(s.name) } end
	end
	return out
end
-- La ventana de una profesión. En el juego (2026-11-17) la primera versión leía las 1.257 recetas de golpe y nueve veces (el aviso
-- TRADE_SKILL_LIST_UPDATE se repite): un pico de 1,08 s, el tirón que notó. Ahora: una lectura por profesión y habilidad como mucho cada 2
-- minutos, y los esquemas de las recetas a trozos de 40 por imagen.
local profesionVista = {}  -- prof → "habilidad|recetas" y hora de la última lectura
local esquemasEnCola = nil
local function esquemasPaso()
	local q = esquemasEnCola
	if not q then return end
	local T = C_TradeSkillUI
	for _ = 1, 40 do
		local id = q.ids[q.pos]
		if not id then esquemasEnCola = nil; return end
		q.pos = q.pos + 1
		if not inCombat() and T and T.GetRecipeInfo then
			local r = try(T.GetRecipeInfo, id)
			if r then R.foto("receta", id, "esquema", { info = limpio(r), esquema = uno(T.GetRecipeSchematic, id, false) }, nil, R.idioma()) end
		end
	end
	C_Timer.After(0.05, function() R.segura("esquemas", esquemasPaso) end)
end
local function profesion()
	if inCombat() then return end
	local T = C_TradeSkillUI
	if not (T and T.GetAllRecipeIDs and T.GetRecipeInfo) then return end
	if try(T.IsTradeSkillLinked) or try(T.IsTradeSkillGuild) or try(T.IsNPCCrafting) then return end
	local base = try(T.GetBaseProfessionInfo)
	local prof = base and num(base.professionID)
	if not prof then return end
	-- A veces el primer aviso llega con la profesión aún sin cargar (número 0 y nombre vacío; visto en el juego, 2026-11-18): se espera al siguiente.
	if prof == 0 and (str(base.professionName) or "") == "" then return end
	local ids = try(T.GetAllRecipeIDs) or {}
	-- La habilidad actual del jugador en esta profesión: por el número de la línea o, si no coincide, por el nombre.
	local hab = leeHabilidades()
	local linea = num(base.skillLineID) or num(base.parentProfessionID) or prof
	local nivel = (hab[prof] and hab[prof][1]) or (hab[linea] and hab[linea][1]) or nil
	if not nivel then
		for l, h in pairs(hab) do if h[3] and str(base.professionName) == h[3] then nivel = h[1]; linea = l end end
	end
	local clave = tostring(nivel) .. "|" .. #ids
	local antes = profesionVista[prof]
	if antes and antes.clave == clave and GetTime() - antes.hora < 120 then return end
	profesionVista[prof] = { clave = clave, hora = GetTime() }
	local recetas, dificultades, pendientes = {}, {}, {}
	for _, id in ipairs(ids) do
		if num(id) then
			local r = try(T.GetRecipeInfo, id)
			if r then
				recetas[#recetas + 1] = id
				dificultades[#dificultades + 1] = { id, limpio(r.difficulty) or false, b01(r.learned) or false }
				pendientes[#pendientes + 1] = id
			end
		end
	end
	table.sort(recetas)
	R.foto("profesion", prof, "recetas", { nombre = str(base.professionName), linea = linea, recetas = recetas }, nil, R.idioma())
	-- La dificultad depende de la habilidad del jugador: foto con la habilidad dentro, así se repite cuando sube (→ habilidad_umbral).
	R.foto("profesion", prof, "dificultad", { habilidad = nivel, linea = linea, recetas = dificultades }, nil, false)
	-- Los esquemas (materiales, lo que crea) son fotos que casi nunca cambian: a trozos, sin parar el juego.
	if not esquemasEnCola then esquemasEnCola = { ids = pendientes, pos = 1 }; C_Timer.After(0.05, function() R.segura("esquemas", esquemasPaso) end)
	else for _, id in ipairs(pendientes) do esquemasEnCola.ids[#esquemasEnCola.ids + 1] = id end end
end
local function subidas()
	local ahora = leeHabilidades()
	for linea, h in pairs(ahora) do
		local antes = habilidades[linea]
		if antes and h[1] > antes[1] then
			local con = hace(ultimo.hechizo) < 15 and ultimo.hechizo or nil
			R.obs("progreso", { habilidad = linea, de = antes[1], a = h[1], con_tipo = con and "hechizo" or nil, con_id = con and con.id or nil })
		end
	end
	habilidades = ahora
end
local function fabricado(...)
	local args = pack(...)
	local itemID, cantidad
	if type(args[1]) == "table" then itemID, cantidad = num(args[1].itemID), num(args[1].quantity)
	else itemID, cantidad = num(args[1]), num(args[3]) end
	local receta = hace(ultimo.hechizo) < 15 and ultimo.hechizo.id or nil
	R.obs("progreso", { receta = receta, resultado = itemID or enlace(args[2]), cantidad = cantidad or 1, con_tipo = "fabricacion" })
end

-- ───────── 5.9 ganancias: por diferencias, no por el chat ─────────
local antes = { xp = nil, dinero = nil, rep = {} }
local function fotoGanancias()
	antes.xp = num(try(UnitXP, "player"))
	antes.dinero = num(try(GetMoney))
	antes.rep = {}
	local C = C_Reputation
	if C and C.GetNumFactions and C.GetFactionDataByIndex then
		for i = 1, math.min(num(try(C.GetNumFactions)) or 0, 300) do
			local d = try(C.GetFactionDataByIndex, i)
			if d and num(d.factionID) and num(d.currentStanding) then antes.rep[d.factionID] = d.currentStanding end
		end
	end
end
local function deQue()
	if ultimo.mision and ultimo.mision.entregada and hace(ultimo.mision) < 5 then return "mision", ultimo.mision.id, nil end
	if ultimo.muerto and hace(ultimo.muerto) < 8 then return "pnj", ultimo.muerto.id, ultimo.muerto.nombre end
	if ultimo.botin and hace(ultimo.botin) < 5 then return "botin", nil, nil end
	return nil, nil, nil
end
local function xp()
	local ahora = num(try(UnitXP, "player"))
	if antes.xp and ahora and ahora > antes.xp then
		local tipo, id, nombre = deQue()
		if tipo ~= "mision" then R.obs("ganancia", { clase_ganancia = "xp", cantidad = ahora - antes.xp, de_tipo = tipo, de_id = id, de_nombre = nombre }) end
	end
	antes.xp = ahora
	R.estado()
end
local function dinero()
	local ahora = num(try(GetMoney))
	if antes.dinero and ahora and ahora ~= antes.dinero then
		local tipo, id, nombre = deQue()
		if tipo ~= "mision" then R.obs("ganancia", { clase_ganancia = "dinero", cantidad = ahora - antes.dinero, de_tipo = tipo, de_id = id, de_nombre = nombre }) end
	end
	antes.dinero = ahora
end
local function reputacion()
	local C = C_Reputation
	if not (C and C.GetNumFactions and C.GetFactionDataByIndex) then return end
	for i = 1, math.min(num(try(C.GetNumFactions)) or 0, 300) do
		local d = try(C.GetFactionDataByIndex, i)
		if d and num(d.factionID) and num(d.currentStanding) then
			local a = antes.rep[d.factionID]
			if a and d.currentStanding ~= a then
				local tipo, id, nombre = deQue()
				R.obs("ganancia", { clase_ganancia = "reputacion", cantidad = d.currentStanding - a, de_tipo = tipo, de_id = id, de_nombre = nombre, faccion_rep = d.factionID })
			end
			antes.rep[d.factionID] = d.currentStanding
		end
	end
end

-- ───────── 5.10 mazmorras ─────────
local instanciaVista = nil
local function instancia()
	local inst = enInstancia()
	if inst and inst.id ~= instanciaVista then
		instanciaVista = inst.id
		R.obs("mazmorra", { mazmorra = inst.id, nombre = inst.nombre, dificultad = inst.dificultad, tamano = inst.tamano })
		R.cronologia("mazmorra", inst.id)
	elseif not inst then instanciaVista = nil end
end
local function bloqueos()
	local n = num(try(GetNumSavedInstances)) or 0
	local l = {}
	for i = 1, n do l[#l + 1] = todos(GetSavedInstanceInfo, i) or false end
	if n > 0 then R.foto("jugador", 0, "bloqueos", l, nil, false) end
end
local function buscadorMazmorras()
	local l = {}
	for id = 1, 400 do
		local d = todos(GetLFGDungeonInfo, id)
		if d and d[1] ~= false then l[#l + 1] = { id, d[1], d[4], d[5], d[2], d[3] } end
	end
	if #l > 0 then R.foto("juego", 0, "buscador_mazmorras", l, nil, R.idioma()) end
end

-- ───────── 5.11 lo que dicen los personajes del juego ─────────
local HABLA = { CHAT_MSG_MONSTER_SAY = "dice", CHAT_MSG_MONSTER_YELL = "grita", CHAT_MSG_MONSTER_EMOTE = "emocion", CHAT_MSG_MONSTER_WHISPER = "susurra_pnj",
	CHAT_MSG_RAID_BOSS_EMOTE = "jefe_emocion", CHAT_MSG_RAID_BOSS_WHISPER = "jefe_susurra" }
local function habla(ev, msg, autor, ...)
	-- El GUID de quien habla va en el argumento 12 de los avisos de chat, pero en el juego (2026-11-17) llegó vacío: se busca
	-- entre todos los argumentos el que tenga forma de GUID.
	local args = pack(...)
	local g = nil
	for i = 1, args.n do
		local a = args[i]
		if type(a) == "string" and a:match("^%a+%-%d+%-") then g = a; break end
	end
	local tipo, id = guidPartes(str(g))
	if str(g) and g:match("^Player%-") then return end
	texto({ tipo = tipo or "pnj", id = id, nombre = str(autor), guid = tipo and g or nil, clase_texto = HABLA[ev], texto = msg }, "propia")
end

-- ───────── 5.14 subasta (lotes aparte, clase «subasta») ─────────
-- Casa de subastas: «<región>-<reino>» (M4.4.2), hasta saber si en Forever la subasta es por reino o compartida.
local function casa()
	local region = str(try(GetCurrentRegionName))
	if not region then return nil end
	return region .. "-" .. (str(try(GetRealmName)) or "?")
end
local listaVista = 0
local function listaSubasta(ev, nuevos)
	if not (C_AuctionHouse and C_AuctionHouse.GetBrowseResults) then return end
	local filas = type(nuevos) == "table" and nuevos or nil
	if not filas then
		local l = try(C_AuctionHouse.GetBrowseResults)
		if type(l) ~= "table" then return end
		if ev == "AUCTION_HOUSE_BROWSE_RESULTS_UPDATED" then listaVista = 0 end
		filas = {}
		for i = listaVista + 1, #l do filas[#filas + 1] = l[i] end
		listaVista = #l
	end
	local c = casa()
	for _, r in ipairs(filas) do
		if type(r) == "table" and type(r.itemKey) == "table" and num(r.itemKey.itemID) then
			R.obs("precio", { casa = c, objeto = r.itemKey.itemID, sufijo = num(r.itemKey.itemSuffix), nivel_objeto = num(r.itemKey.itemLevel),
				precio = num(r.minPrice), cantidad = num(r.totalQuantity), de = "escaneo" }, "subasta")
		end
	end
end
local detalleVisto = {}
local function detalleSubasta(ev, clave)
	local mercancia = ev == "COMMODITY_SEARCH_RESULTS_UPDATED"
	local id = mercancia and num(clave) or (type(clave) == "table" and num(clave.itemID))
	if not id then return end
	local k = tostring(id) .. ":" .. math.floor(GetTime())
	if detalleVisto[k] then return end -- cada aviso llega dos veces
	detalleVisto = { [k] = true }
	local n
	if mercancia then n = num(try(C_AuctionHouse.GetNumCommoditySearchResults, clave)) else n = num(try(C_AuctionHouse.GetNumItemSearchResults, clave)) end
	local c = casa()
	for i = 1, math.min(n or 0, 500) do
		local r
		if mercancia then r = try(C_AuctionHouse.GetCommoditySearchResultInfo, clave, i) else r = try(C_AuctionHouse.GetItemSearchResultInfo, clave, i) end
		if type(r) == "table" then
			R.obs("precio", { casa = c, objeto = id, sufijo = type(r.itemKey) == "table" and num(r.itemKey.itemSuffix) or (type(clave) == "table" and num(clave.itemSuffix)),
				nivel_objeto = type(r.itemKey) == "table" and num(r.itemKey.itemLevel) or (type(clave) == "table" and num(clave.itemLevel)),
				precio = num(r.unitPrice) or num(r.buyoutAmount), cantidad = num(r.quantity), de = mercancia and "detalle_unidad" or "detalle",
				segundos = num(r.timeLeftSeconds) or num(r.timeLeft) }, "subasta")
		end
	end
end

-- ───────── 5.16 mundo y calendario, 5.17 reglas de Forever ─────────
local function reglas()
	if not (C_GameRules and Enum and Enum.GameRule) then return end
	local activas = {}
	for k, v in pairs(Enum.GameRule) do
		if try(C_GameRules.IsGameRuleActive, v) then
			local valor = try(C_GameRules.GetGameRuleAsFloat, v, 2)
			activas[tostring(k)] = limpio(valor) or true
		end
	end
	activas["__experiencia"] = num(try(C_GameRules.GetForeverExperiencePreset)) or false
	activas["__modo"] = num(try(C_GameRules.GetActiveGameMode)) or false
	R.foto("regla", str(try(GetRealmName)) or "?", "activas", activas, nil, false)
end
-- 5.27 (decidido el 2026-11-18; 1.3.2): dos fotos por categoría de apariencia de transfiguración (1-30), una categoría cada 2 s desde los 35 s
-- de entrar: «lista», todas las apariencias que lista el juego, las tenga el jugador o no (dice qué apariencias de los archivos existen de
-- verdad en esta versión de Forever), y «fuentes», qué objetos dan cada apariencia (GetAppearanceSources, probada en el juego el 2026-11-18
-- con /run: trae visualID, itemID, itemModID, sourceID, invType, categoryID y campos del jugador que no se apuntan). El juego filtra por lo
-- que la clase puede usar (visto con dos personajes), así que la base junta las fotos de jugadores de clases distintas; la clase va en la
-- cabecera del lote. Una vez por versión, valor y 3 días, como las demás fotos. Lo que TIENE cada uno va al progreso (ForeverTracker.lua).
local function apariencias(cat)
	if not (C_TransmogCollection and C_TransmogCollection.GetCategoryAppearances) then return end
	local lista = try(C_TransmogCollection.GetCategoryAppearances, cat)
	if type(lista) ~= "table" or #lista == 0 then return end
	-- 1.3.3: si la ventana de apariencias ha recortado esta categoría a «solo las que tienes» (pasa tras abrirla y hacer /reload), no
	-- se manda la foto: sería una lista incompleta. Se hará en la siguiente entrada limpia al juego.
	if FT.listaRecortada and FT.listaRecortada(cat, lista) then return end
	local ids, fuentes = {}, {}
	for _, a in ipairs(lista) do
		local id = type(a) == "table" and num(a.visualID) or num(a)
		if id then
			ids[#ids + 1] = id
			if C_TransmogCollection.GetAppearanceSources then
				local src = try(C_TransmogCollection.GetAppearanceSources, id)
				if type(src) == "table" and #src > 0 then
					local l = {}
					for _, s in ipairs(src) do
						if type(s) == "table" and num(s.itemID) then l[#l + 1] = { s.itemID, num(s.itemModID) or 0, num(s.sourceID) or false, num(s.invType) or false } end
					end
					if #l > 0 then fuentes[tostring(id)] = l end
				end
			end
		end
	end
	table.sort(ids)
	R.foto("apariencia", cat, "lista", { n = #lista, ids = ids }, nil, false)
	if next(fuentes) then R.foto("apariencia", cat, "fuentes", fuentes, nil, false) end
end
local function calendario()
	if not (C_Calendar and C_DateAndTime) then return end
	local hoy = try(C_DateAndTime.GetCurrentCalendarTime)
	local dia = type(hoy) == "table" and num(hoy.monthDay) or 1
	local eventos = {}
	for d = 0, 6 do
		local n = num(try(C_Calendar.GetNumDayEvents, 0, dia + d)) or 0
		for i = 1, math.min(n, 20) do
			local e = try(C_Calendar.GetDayEvent, 0, dia + d, i)
			if type(e) == "table" then eventos[#eventos + 1] = { dia + d, limpio(e.title) or false, limpio(e.calendarType) or false, limpio(e.eventType) or false, limpio(e.startTime) or false, limpio(e.endTime) or false } end
		end
	end
	local fecha = type(hoy) == "table" and (tostring(hoy.year) .. "-" .. tostring(hoy.month) .. "-" .. tostring(dia)) or "?"
	R.foto("calendario", fecha, "eventos", eventos, nil, R.idioma())
	R.foto("juego", 0, "reinicios", { diario = num(try(C_DateAndTime.GetSecondsUntilDailyReset)), semanal = num(try(C_DateAndTime.GetSecondsUntilWeeklyReset)), hora = now() }, nil, false)
end
local function mapaActual()
	local mapa = C_Map and num(try(C_Map.GetBestMapForUnit, "player"))
	if mapa then R.foto("mapa", mapa, "info", uno(C_Map.GetMapInfo, mapa), nil, R.idioma()) end
end

-- ───────── 5.18 muertes y cementerios; 5.19 campamentos (efectos ganados) ─────────
local function mapaZona()
	local mapa = C_Map and num(try(C_Map.GetBestMapForUnit, "player"))
	if not mapa then return nil end
	for _ = 1, 5 do
		local info = try(C_Map.GetMapInfo, mapa)
		if type(info) ~= "table" then break end
		if info.mapType == 3 or not info.parentMapID or info.parentMapID == 0 or (info.mapType and info.mapType < 3) then return mapa end
		mapa = info.parentMapID
	end
	return mapa
end
local ultimoCementerio = 0
local function fantasma()
	if not try(UnitIsGhost, "player") then return end
	if GetTime() - ultimoCementerio < 30 then return end -- PLAYER_ALIVE llega dos veces al liberar (visto en el juego, 2026-11-17)
	ultimoCementerio = GetTime()
	local z = mapaZona()
	local cx, cy
	local cuerpo = C_DeathInfo and try(C_DeathInfo.GetCorpseMapPosition, z or 0)
	if cuerpo then local ok, x, y = pcall(cuerpo.GetXY, cuerpo); if ok then cx, cy = num(x), num(y) end end
	local p = R.pos("propia")
	R.obs("cementerio", { mapa = p.mapa, x = p.x, y = p.y, mapa_cuerpo = z, cx = cx, cy = cy })
end
local misEfectos = nil
local function efectosPropios()
	if inCombat() or not (C_UnitAuras and C_UnitAuras.GetAuraDataByIndex) then misEfectos = nil; return end
	local ahora = {}
	for i = 1, 40 do
		local a = try(C_UnitAuras.GetAuraDataByIndex, "player", i, "HELPFUL")
		if not a or (issecretvalue and issecretvalue(a)) then break end
		if num(a.spellId) then ahora[a.spellId] = a end
	end
	if misEfectos then
		for id, a in pairs(ahora) do
			if not misEfectos[id] then
				local cerca = hace(ultimoObjeto) < 60 and ultimoObjeto.nombre or nil
				R.obs("efecto", R.conPos({ hechizo = id, nombre = str(a.name), de_tipo = cerca and "objeto_mundo" or nil, de_nombre = cerca,
					cifras = limpio(a.points), duracion = num(a.duration) }, "propia"))
			end
		end
	end
	misEfectos = ahora
end

-- ───────── 5.1 posición: subzonas, compañeros de grupo; 5.25 transportes ─────────
local ultimaPos = nil
local function subzona(ev)
	local p = R.pos("propia")
	R.obs("posicion", { tipo = "subzona", nombre = p.subzona or str(try(GetZoneText)), mapa = p.mapa, x = p.x, y = p.y, mx = p.mx, my = p.my, subzona = p.subzona, como = ev })
	if ev == "ZONE_CHANGED_NEW_AREA" then
		if ultimaPos and p.mx and p.my and ultimaPos.mx and ultimaPos.my then
			local d = math.sqrt((p.mx - ultimaPos.mx) ^ 2 + (p.my - ultimaPos.my) ^ 2)
			local t = GetTime() - ultimaPos.hora
			local hogar = ultimo.hechizo.id == 8690 and hace(ultimo.hechizo) < 60
			if d > 2000 and t < 600 and not try(UnitOnTaxi, "player") and not hogar and not enInstancia() then
				R.obs("viaje", { clase_viaje = "transporte", de_nodo = ultimaPos.mapa, a_nodo = p.mapa, coste = math.floor(d), duracion = math.floor(t) })
			end
		end
		R.cronologia("zona", p.mapa)
		mapaActual()
	end
	ultimaPos = { mapa = p.mapa, mx = p.mx, my = p.my, hora = GetTime() }
	R.estado()
end
local function grupo()
	if inCombat() or (num(try(GetNumGroupMembers)) or 0) == 0 then return end
	local mapa = C_Map and num(try(C_Map.GetBestMapForUnit, "player"))
	for i = 1, 4 do
		local u = "party" .. i
		if try(UnitExists, u) then
			local my, mx = try(UnitPosition, u)
			local x, y
			local v = mapa and try(C_Map.GetPlayerMapPosition, mapa, u)
			if v then local ok, a, b = pcall(v.GetXY, v); if ok then x, y = num(a), num(b) end end
			if num(mx) or x then R.obs("posicion", { tipo = "grupo", id = i, mapa = mapa, x = x, y = y, mx = num(mx), my = num(my), como = "grupo" }) end
		end
	end
end

-- ───────── 5.23 domar ─────────
local mascotaVista = nil
local function mascota()
	local g = str(try(UnitGUID, "pet"))
	if not g or g == mascotaVista then return end
	mascotaVista = g
	local id = g:match("^Pet%-%d+%-%d+%-%d+%-%d+%-(%d+)%-")
	if not id then return end
	local familia = str(try(UnitCreatureFamily, "pet"))
	R.foto("pnj", tonumber(id), "domable", { familia = familia or false }, nil, R.idioma())
end

-- ───────── 6.3 cronología del personaje (dato del jugador, en ForeverTrackerChar) ─────────
local CRONO_MAX = 2000
local cronoPos = nil
function R.cronologia(tipo, dato)
	if type(ForeverTrackerChar) ~= "table" then return end
	local c = ForeverTrackerChar.cronologia
	if type(c) ~= "table" then c = {}; ForeverTrackerChar.cronologia = c end
	local p = R.pos("propia")
	c[#c + 1] = { now(), tipo, p.mapa or false, p.x or false, p.y or false, dato == nil and false or dato }
	if #c > CRONO_MAX then
		for i = 1, #c do if c[i][2] == "pos" then table.remove(c, i); break end end
		if #c > CRONO_MAX then table.remove(c, 1) end
	end
end
local function cronoTick()
	if inCombat() or enInstancia() then return end
	local p = R.pos("propia")
	if not (p.mx and p.my) then return end
	if cronoPos and math.sqrt((p.mx - cronoPos.mx) ^ 2 + (p.my - cronoPos.my) ^ 2) < 20 then return end
	cronoPos = { mx = p.mx, my = p.my }
	R.cronologia("pos", nil)
end

-- ───────── avisos del juego ─────────
local frame = CreateFrame("Frame")
local function register(ev)
	if C_EventUtils and C_EventUtils.IsEventValid and not C_EventUtils.IsEventValid(ev) then return end
	pcall(frame.RegisterEvent, frame, ev)
end
local EVENTOS = { "PLAYER_ENTERING_WORLD", "ZONE_CHANGED", "ZONE_CHANGED_INDOORS", "ZONE_CHANGED_NEW_AREA", "PLAYER_TARGET_CHANGED", "UPDATE_MOUSEOVER_UNIT",
	"NAME_PLATE_UNIT_ADDED", "PLAYER_TARGET_DIED", "PLAYER_REGEN_ENABLED", "PLAYER_DEAD", "PLAYER_ALIVE", "UPDATE_INVENTORY_DURABILITY", "ENCOUNTER_START", "ENCOUNTER_END",
	"LOOT_OPENED", "LOOT_HISTORY_UPDATE_DROP", "UNIT_SPELLCAST_SUCCEEDED", "MERCHANT_SHOW", "MERCHANT_UPDATE", "TRAINER_SHOW", "TRAINER_UPDATE", "GOSSIP_SHOW",
	"TAXIMAP_OPENED", "PLAYER_CONTROL_LOST", "PLAYER_CONTROL_GAINED", "HEARTHSTONE_BOUND", "MAIL_SHOW", "QUEST_DETAIL", "QUEST_PROGRESS", "QUEST_COMPLETE",
	"QUEST_ACCEPTED", "QUEST_TURNED_IN", "UI_INFO_MESSAGE", "TRADE_SKILL_SHOW", "TRADE_SKILL_LIST_UPDATE", "TRADE_SKILL_ITEM_CRAFTED_RESULT", "SKILL_LINES_CHANGED",
	"PLAYER_XP_UPDATE", "PLAYER_MONEY", "UPDATE_FACTION", "UPDATE_INSTANCE_INFO", "CHAT_MSG_MONSTER_SAY", "CHAT_MSG_MONSTER_YELL", "CHAT_MSG_MONSTER_EMOTE",
	"CHAT_MSG_MONSTER_WHISPER", "CHAT_MSG_RAID_BOSS_EMOTE", "CHAT_MSG_RAID_BOSS_WHISPER", "AUCTION_HOUSE_BROWSE_RESULTS_ADDED", "AUCTION_HOUSE_BROWSE_RESULTS_UPDATED",
	"COMMODITY_SEARCH_RESULTS_UPDATED", "ITEM_SEARCH_RESULTS_UPDATED", "UNIT_AURA", "UNIT_PET", "PLAYER_LEVEL_UP", "PLAYER_LOGOUT" }
for _, ev in ipairs(EVENTOS) do register(ev) end
local pendienteTras = {} -- lo que hay que leer al salir de combate
local function tras(nombre, fn) if inCombat() then pendienteTras[nombre] = fn else R.segura(nombre, fn) end end
local function luego(s, nombre, fn, ...) local args = pack(...); C_Timer.After(s, function() R.segura(nombre, fn, unpack(args, 1, args.n)) end) end
local arrancado = false

frame:SetScript("OnEvent", function(_, ev, ...)
	if not R.activo() and ev ~= "PLAYER_ENTERING_WORLD" and ev ~= "PLAYER_LOGOUT" then return end
	local a1, a2, a3 = ...
	if ev == "PLAYER_ENTERING_WORLD" then
		if not arrancado then
			arrancado = true
			local n, clase, raza = try(UnitName, "player"), try(UnitClass, "player"), try(UnitRace, "player")
			yo = { nombre = str(n), clase = str(clase), raza = str(raza) }
			if TooltipDataProcessor and TooltipDataProcessor.AddTooltipPostCall and Enum and Enum.TooltipDataType and Enum.TooltipDataType.Object then
				pcall(TooltipDataProcessor.AddTooltipPostCall, Enum.TooltipDataType.Object, function(tt, d) R.segura("recuadro", recuadroObjeto, tt, d) end)
			end
			if hooksecurefunc and TakeTaxiNode then pcall(hooksecurefunc, "TakeTaxiNode", function(i) R.segura("vuelo", tomaVuelo, i) end) end
			luego(3, "ganancias", fotoGanancias)
			luego(3, "habilidades", function() habilidades = leeHabilidades(); durabilidad = leeDurabilidad() end)
			luego(20, "reglas", reglas)
			luego(25, "calendario", calendario)
			luego(30, "buscador", buscadorMazmorras)
			for cat = 1, 30 do luego(35 + cat * 2, "apariencias", apariencias, cat) end
			luego(8, "efectos", efectosPropios)
			C_Timer.NewTicker(60, function() R.segura("cronologia", cronoTick); R.segura("grupo", grupo) end)
		end
		luego(2, "instancia", instancia)
		luego(4, "mapa", mapaActual)
		return
	end
	if ev == "PLAYER_LOGOUT" then return end
	if ev == "ZONE_CHANGED" or ev == "ZONE_CHANGED_INDOORS" or ev == "ZONE_CHANGED_NEW_AREA" then
		R.segura("subzona", subzona, ev)
		if ev == "ZONE_CHANGED_NEW_AREA" then luego(2, "instancia", instancia) end
	elseif ev == "PLAYER_TARGET_CHANGED" then R.segura("unidad", unidad, "target", "senalar")
	elseif ev == "UPDATE_MOUSEOVER_UNIT" then R.segura("unidad", unidad, "mouseover", "raton")
	elseif ev == "NAME_PLATE_UNIT_ADDED" then R.segura("unidad", unidad, a1, "placa")
	elseif ev == "PLAYER_TARGET_DIED" then R.segura("muere", objetivoMuere)
	elseif ev == "PLAYER_REGEN_ENABLED" then
		luego(1.5, "medidor", medidorCombate)
		for nombre, fn in pairs(pendienteTras) do R.segura(nombre, fn) end
		pendienteTras = {}
		luego(2, "efectos", efectosPropios)
	elseif ev == "PLAYER_DEAD" then luego(1, "muerte", muerte); R.cronologia("muerte", nil)
	elseif ev == "PLAYER_ALIVE" then luego(1, "fantasma", fantasma)
	elseif ev == "UPDATE_INVENTORY_DURABILITY" then if not try(UnitIsDeadOrGhost, "player") then R.segura("durabilidad", function() durabilidad = leeDurabilidad() end) end
	elseif ev == "ENCOUNTER_START" or ev == "ENCOUNTER_END" then R.segura("jefe", jefe, ev, ...)
	elseif ev == "LOOT_OPENED" then R.segura("botin", botin)
	elseif ev == "LOOT_HISTORY_UPDATE_DROP" then tras("reparto", reparto)
	elseif ev == "UNIT_SPELLCAST_SUCCEEDED" then
		if a1 == "player" then ultimo.hechizo = { id = num(a3), hora = GetTime() } end
	elseif ev == "MERCHANT_SHOW" or ev == "MERCHANT_UPDATE" then luego(0.5, "vendedor", vendedor)
	elseif ev == "TRAINER_SHOW" or ev == "TRAINER_UPDATE" then luego(0.5, "instructor", instructor)
	elseif ev == "GOSSIP_SHOW" then luego(0.3, "conversacion", conversacion)
	elseif ev == "TAXIMAP_OPENED" then luego(0.5, "vuelo", maestroVuelo)
	elseif ev == "PLAYER_CONTROL_LOST" or ev == "PLAYER_CONTROL_GAINED" then luego(0.5, "control", control, ev)
	elseif ev == "HEARTHSTONE_BOUND" then R.segura("hogar", hogar)
	elseif ev == "MAIL_SHOW" then R.segura("buzon", buzon)
	elseif ev == "QUEST_DETAIL" or ev == "QUEST_PROGRESS" or ev == "QUEST_COMPLETE" then luego(1, "mision", misionPNJ, ev)
	elseif ev == "QUEST_ACCEPTED" then R.segura("aceptada", misionAceptada, num(a2) or num(a1)) -- (índice, misión) en el clásico; (misión) en el actual
	elseif ev == "QUEST_TURNED_IN" then R.segura("entregada", misionEntregada, a1, a2, a3)
	elseif ev == "UI_INFO_MESSAGE" then R.segura("avance", avance, a1, a2)
	elseif ev == "TRADE_SKILL_SHOW" or ev == "TRADE_SKILL_LIST_UPDATE" then luego(1, "profesion", profesion)
	elseif ev == "TRADE_SKILL_ITEM_CRAFTED_RESULT" then R.segura("fabricado", fabricado, ...)
	elseif ev == "SKILL_LINES_CHANGED" then luego(0.5, "subidas", subidas)
	elseif ev == "PLAYER_XP_UPDATE" then R.segura("xp", xp)
	elseif ev == "PLAYER_MONEY" then R.segura("dinero", dinero)
	elseif ev == "UPDATE_FACTION" then luego(0.5, "reputacion", reputacion)
	elseif ev == "UPDATE_INSTANCE_INFO" then tras("bloqueos", bloqueos)
	elseif HABLA[ev] then R.segura("habla", habla, ev, ...)
	elseif ev == "AUCTION_HOUSE_BROWSE_RESULTS_ADDED" or ev == "AUCTION_HOUSE_BROWSE_RESULTS_UPDATED" then R.segura("subasta", listaSubasta, ev, a1)
	elseif ev == "COMMODITY_SEARCH_RESULTS_UPDATED" or ev == "ITEM_SEARCH_RESULTS_UPDATED" then luego(0.5, "subasta_detalle", detalleSubasta, ev, a1)
	elseif ev == "UNIT_AURA" then if a1 == "player" then tras("efectos", efectosPropios) end
	elseif ev == "UNIT_PET" then if a1 == "player" then luego(1, "mascota", mascota) end
	elseif ev == "PLAYER_LEVEL_UP" then R.cronologia("nivel", num(a1)); R.estado()
	end
end)
