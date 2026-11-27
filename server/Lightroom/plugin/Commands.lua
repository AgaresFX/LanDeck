--[[
PadNook commands (one line, fields separated by TAB):
  hello                      full state again
  watch  P1,P2,...           Develop parameters reported on change
  inc|dec  PARAM  STEP       value +/- step, clamped to the range
  set  PARAM  VALUE          absolute value
  reset  PARAM               default value
  resetAll | autoTone | copy | paste | sync | beforeAfter | undo | redo | next | prev
  preset  NAME               apply a Develop preset (by name) to the selected photos
  presets                    list of the Develop presets
  range  PARAM               min and max of a parameter
  rating  0..5 | flag  pick|reject|none | label  red|yellow|green|blue|purple|none
  module  library|develop|map|book|slideshow|print|web
State sent back: val P V | range P MIN MAX | photo RATING FLAG LABEL | module NAME | presets N1 N2... | err TEXT
]]

local LrApplication = import 'LrApplication'
local LrApplicationView = import 'LrApplicationView'
local LrDevelopController = import 'LrDevelopController'
local LrSelection = import 'LrSelection'
local LrTasks = import 'LrTasks'
local LrUndo = import 'LrUndo'

local M = {}

local send = function(line) end
local watched = {}       -- PadNook name -> true
local lastValue = {}     -- PadNook name -> last value sent
local lastPhoto = nil    -- "rating\tflag\tlabel"
local lastModule = nil
local copied = nil       -- develop settings copied with "copy"
local beforeAfter = false

-- PadNook names -> Lightroom names to try (newer process versions use the 2012 names).
local NAMES = {
  Exposure = { 'Exposure', 'Exposure2012' },
  Contrast = { 'Contrast', 'Contrast2012' },
  Highlights = { 'Highlights', 'Highlights2012' },
  Shadows = { 'Shadows', 'Shadows2012' },
  Whites = { 'Whites', 'Whites2012' },
  Blacks = { 'Blacks', 'Blacks2012' },
  Clarity = { 'Clarity', 'Clarity2012' },
  Temperature = { 'Temperature' },
  Tint = { 'Tint' },
  Vibrance = { 'Vibrance' },
  Saturation = { 'Saturation' },
  Texture = { 'Texture' },
  Dehaze = { 'Dehaze' },
}
local resolved = {}      -- PadNook name -> Lightroom name that worked

function M.setSender(fn) send = fn end

function M.resetCache()
  lastValue = {}
  lastPhoto = nil
  lastModule = nil
end

local function err(text)
  send('err\t' .. tostring(text))
end

local function inDevelop()
  return LrApplicationView.getCurrentModuleName() == 'develop'
end

local function ensureDevelop()
  if not inDevelop() then
    LrApplicationView.switchToModule('develop')
  end
end

local function lrName(name)
  if resolved[name] then return resolved[name] end
  local candidates = NAMES[name] or { name }
  for _, candidate in ipairs(candidates) do
    local ok, value = pcall(LrDevelopController.getValue, candidate)
    if ok and value ~= nil then
      resolved[name] = candidate
      return candidate
    end
  end
  return nil
end

local function getValue(name)
  local n = lrName(name)
  if not n then return nil end
  local ok, value = pcall(LrDevelopController.getValue, n)
  if ok then return value end
  return nil
end

local function getRange(name)
  local n = lrName(name)
  if not n then return nil end
  local ok, min, max = pcall(LrDevelopController.getRange, n)
  if ok and min ~= nil and max ~= nil then return min, max end
  return nil
end

local function setValue(name, value)
  local n = lrName(name)
  if not n then
    err('Parametro non disponibile: ' .. name)
    return
  end
  local min, max = getRange(name)
  if min and value < min then value = min end
  if max and value > max then value = max end
  local ok, e = pcall(LrDevelopController.setValue, n, value)
  if not ok then err(e) end
end

local function fmt(v)
  return string.format('%.4f', v)
end

local function targetPhotos()
  local catalog = LrApplication.activeCatalog()
  local photos = catalog:getTargetPhotos()
  return catalog, photos
end

local function withWrite(title, fn)
  local catalog = LrApplication.activeCatalog()
  local ok, e = LrTasks.pcall(function()
    catalog:withWriteAccessDo(title, fn, { timeout = 5 })
  end)
  if not ok then err(e) end
end

local function sendPresets()
  local names = {}
  local ok, e = pcall(function()
    for _, folder in ipairs(LrApplication.developPresetFolders()) do
      for _, preset in ipairs(folder:getDevelopPresets()) do
        names[#names + 1] = preset:getName()
      end
    end
  end)
  if not ok then
    err('Elenco preset non disponibile: ' .. tostring(e))
    return
  end
  send('presets\t' .. table.concat(names, '\t'))
end

local function findPreset(name)
  local lower = string.lower(name)
  for _, folder in ipairs(LrApplication.developPresetFolders()) do
    for _, preset in ipairs(folder:getDevelopPresets()) do
      if string.lower(preset:getName()) == lower then return preset end
    end
  end
  return nil
end

local LABEL_TOGGLES = {
  red = 'toggleRedLabel',
  yellow = 'toggleYellowLabel',
  green = 'toggleGreenLabel',
  blue = 'toggleBlueLabel',
  purple = 'togglePurpleLabel',
}

function M.handle(line)
  local f = {}
  for field in string.gmatch(line, '[^\t]+') do f[#f + 1] = field end
  local cmd = f[1]
  if not cmd then return end
  local ok, e = LrTasks.pcall(function()
    if cmd == 'hello' then
      M.resetCache()
    elseif cmd == 'watch' then
      watched = {}
      for name in string.gmatch(f[2] or '', '[^,]+') do watched[name] = true end
      lastValue = {}
    elseif cmd == 'inc' or cmd == 'dec' then
      ensureDevelop()
      local step = tonumber(f[3] or '') or 0
      local current = getValue(f[2])
      if current == nil then
        err('Nessuna foto in Sviluppo')
        return
      end
      if cmd == 'dec' then step = -step end
      setValue(f[2], current + step)
    elseif cmd == 'set' then
      ensureDevelop()
      local value = tonumber(f[3] or '')
      if value then setValue(f[2], value) end
    elseif cmd == 'reset' then
      ensureDevelop()
      local n = lrName(f[2])
      if n then LrDevelopController.resetToDefault(n) end
    elseif cmd == 'resetAll' then
      ensureDevelop()
      LrDevelopController.resetAllDevelopAdjustments()
    elseif cmd == 'autoTone' then
      ensureDevelop()
      LrDevelopController.setAutoTone()
    elseif cmd == 'range' then
      local min, max = getRange(f[2])
      if min then send('range\t' .. f[2] .. '\t' .. fmt(min) .. '\t' .. fmt(max)) end
    elseif cmd == 'copy' then
      local catalog = LrApplication.activeCatalog()
      local photo = catalog:getTargetPhoto()
      if not photo then
        err('Nessuna foto selezionata')
        return
      end
      copied = photo:getDevelopSettings()
    elseif cmd == 'paste' or cmd == 'sync' then
      if not copied then
        err('Prima copia le impostazioni di una foto')
        return
      end
      local catalog, photos = targetPhotos()
      if cmd == 'paste' then photos = { catalog:getTargetPhoto() } end
      withWrite('PadNook: incolla impostazioni', function()
        for _, photo in ipairs(photos) do photo:applyDevelopSettings(copied) end
      end)
    elseif cmd == 'presets' then
      sendPresets()
    elseif cmd == 'preset' then
      local preset = findPreset(f[2] or '')
      if not preset then
        err('Preset non trovato: ' .. tostring(f[2]))
        return
      end
      local _, photos = targetPhotos()
      withWrite('PadNook: preset', function()
        for _, photo in ipairs(photos) do photo:applyDevelopPreset(preset, _PLUGIN) end
      end)
    elseif cmd == 'beforeAfter' then
      ensureDevelop()
      beforeAfter = not beforeAfter
      LrApplicationView.showView(beforeAfter and 'develop_before_after_horiz' or 'develop_loupe')
    elseif cmd == 'undo' then
      LrUndo.undo()
    elseif cmd == 'redo' then
      LrUndo.redo()
    elseif cmd == 'next' then
      LrSelection.nextPhoto()
    elseif cmd == 'prev' then
      LrSelection.previousPhoto()
    elseif cmd == 'rating' then
      LrSelection.setRating(tonumber(f[2] or '0') or 0)
    elseif cmd == 'flag' then
      if f[2] == 'pick' then LrSelection.flagAsPick()
      elseif f[2] == 'reject' then LrSelection.flagAsReject()
      else LrSelection.removeFlag() end
    elseif cmd == 'label' then
      local toggle = LABEL_TOGGLES[f[2] or '']
      if toggle then LrSelection[toggle]()
      else LrSelection.setColorLabel('none') end
    elseif cmd == 'module' then
      LrApplicationView.switchToModule(f[2] or 'library')
    else
      err('Comando sconosciuto: ' .. cmd)
    end
  end)
  if not ok then err(e) end
  lastPhoto = nil -- report the new rating/flag/label at the next poll
end

-- Sends what changed since the last call (runs about 7 times a second).
function M.poll()
  local module = LrApplicationView.getCurrentModuleName()
  if module ~= lastModule then
    lastModule = module
    send('module\t' .. tostring(module))
  end

  local photo = LrApplication.activeCatalog():getTargetPhoto()
  local info = 'none'
  if photo then
    local rating = photo:getRawMetadata('rating') or 0
    local pick = photo:getRawMetadata('pickStatus') or 0
    local label = photo:getRawMetadata('colorNameForLabel') or ''
    if label == '' then label = 'none' end
    info = tostring(rating) .. '\t' .. tostring(pick) .. '\t' .. tostring(label)
  end
  if info ~= lastPhoto then
    lastPhoto = info
    send('photo\t' .. info)
  end

  if module ~= 'develop' or not photo then return end
  for name in pairs(watched) do
    local value = getValue(name)
    if value ~= nil and lastValue[name] ~= value then
      if lastValue[name] == nil then
        local min, max = getRange(name)
        if min then send('range\t' .. name .. '\t' .. fmt(min) .. '\t' .. fmt(max)) end
      end
      lastValue[name] = value
      send('val\t' .. name .. '\t' .. fmt(value))
    end
  end
end

return M
