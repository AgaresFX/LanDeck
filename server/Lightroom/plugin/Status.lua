-- Menu File / Libreria > Extra plug-in > "PadNook: stato e collegamento".
-- Starts the sockets if Lightroom did not run Init.lua, then shows the status and the last log lines.
local LrDialogs = import 'LrDialogs'
local LrTasks = import 'LrTasks'

LrTasks.startAsyncTask(function()
  local ok, result = LrTasks.pcall(function()
    local Server = require 'Server'
    local started = Server.start('menu')
    if started then LrTasks.sleep(1) end
    return (started and 'Collegamento avviato ora dal menu.\n\n' or '') .. Server.status()
  end)
  LrDialogs.message('PadNook', ok and result or ('Errore del plugin: ' .. tostring(result)), ok and 'info' or 'critical')
end)
