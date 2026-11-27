-- Menu Extra plug-in > "PadNook: esporta impostazioni foto" (diagnostics).
-- Appends every Develop setting of the selected photo to PadNook-impostazioni.txt in the plugin folder,
-- so the name of a setting (e.g. AI Denoise) can be found by comparing two exports.
local LrApplication = import 'LrApplication'
local LrDialogs = import 'LrDialogs'
local LrPathUtils = import 'LrPathUtils'
local LrTasks = import 'LrTasks'

local function dump(value, indent, out)
  if type(value) ~= 'table' then
    out[#out + 1] = indent .. tostring(value)
    return
  end
  local keys = {}
  for k in pairs(value) do keys[#keys + 1] = k end
  table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
  for _, k in ipairs(keys) do
    local v = value[k]
    if type(v) == 'table' then
      out[#out + 1] = indent .. tostring(k) .. ' = {'
      dump(v, indent .. '  ', out)
      out[#out + 1] = indent .. '}'
    else
      out[#out + 1] = indent .. tostring(k) .. ' = ' .. tostring(v)
    end
  end
end

LrTasks.startAsyncTask(function()
  local ok, result = LrTasks.pcall(function()
    local photo = LrApplication.activeCatalog():getTargetPhoto()
    if not photo then return 'Seleziona prima una foto.' end
    local out = { '===== ' .. os.date('%H:%M:%S') .. '  ' .. tostring(photo:getFormattedMetadata('fileName')) .. ' =====' }
    dump(photo:getDevelopSettings(), '', out)
    local path = LrPathUtils.child(_PLUGIN.path, 'PadNook-impostazioni.txt')
    local f = io.open(path, 'a')
    if not f then return 'Impossibile scrivere ' .. path end
    f:write(table.concat(out, '\n') .. '\n\n')
    f:close()
    return 'Impostazioni (' .. (#out - 1) .. ' righe) aggiunte a:\n' .. path
  end)
  LrDialogs.message('PadNook', ok and result or ('Errore: ' .. tostring(result)), ok and 'info' or 'critical')
end)
