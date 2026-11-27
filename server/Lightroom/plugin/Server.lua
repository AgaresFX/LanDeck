--[[
PadNook plugin core: two local sockets (started by Init.lua at Lightroom start, or from the menu).
  58873 (receive): commands from PadNook, one per line, fields separated by TAB.
  58874 (send):    state for PadNook, same format (val, range, photo, module, presets, err).
PadNook connects to both as a client and reconnects when Lightroom is restarted.
Everything is logged to PadNook.log inside the plugin folder.
]]

local LrFunctionContext = import 'LrFunctionContext'
local LrPathUtils = import 'LrPathUtils'
local LrSocket = import 'LrSocket'
local LrTasks = import 'LrTasks'

-- ---- Log in the plugin folder (no Lightroom logger: its folder changes between versions) ----

local logPath = nil
pcall(function() logPath = LrPathUtils.child(_PLUGIN.path, 'PadNook.log') end)

local recent = {}        -- last log lines, shown by the status window

local function log(text, mode)
  recent[#recent + 1] = os.date('%H:%M:%S') .. '  ' .. tostring(text)
  if #recent > 25 then table.remove(recent, 1) end
  if not logPath then return end
  pcall(function()
    local f = io.open(logPath, mode or 'a')
    if f then
      f:write(os.date('%H:%M:%S') .. '  ' .. tostring(text) .. '\n')
      f:close()
    end
  end)
end


-- ---- Shared state -----------------------------------------------------------------------------

-- Shared with Shutdown.lua through a global. Lightroom raises an error when an undefined global is read,
-- so it is read and written with rawget/rawset.
local function getShared()
  local ok, v = pcall(function() return rawget(_G, 'PADNOOK') end)
  return ok and v or nil
end

local okCommands, Commands = pcall(require, 'Commands')
if not okCommands then
  log('Commands.lua failed to load: ' .. tostring(Commands))
  Commands = nil
end

local state = { running = false, connected = false, startedBy = nil }

local RECEIVE_PORT = 58873
local SEND_PORT = 58874

local sender = nil

local function send(line)
  if sender and state.connected then
    local ok, e = pcall(function() sender:send(line .. '\n') end)
    if not ok then log('send failed: ' .. tostring(e)) end
  end
end

if Commands then Commands.setSender(send) end

-- ---- Sockets ----------------------------------------------------------------------------------

local function run(context)
  log('opening 127.0.0.1:' .. RECEIVE_PORT .. ' (commands) and ' .. SEND_PORT .. ' (state)')

  sender = LrSocket.bind {
    functionContext = context,
    plugin = _PLUGIN,
    port = SEND_PORT,
    mode = 'send',
    onConnecting = function(socket, port)
      log('state socket listening on ' .. tostring(port))
    end,
    onConnected = function(socket, port)
      log('state socket connected')
      state.connected = true
      Commands.resetCache()
    end,
    onClosed = function(socket)
      log('state socket closed')
      state.connected = false
      if state.running then socket:reconnect() end
    end,
    onError = function(socket, err)
      if err ~= 'timeout' then log('state socket error: ' .. tostring(err)) end
      state.connected = false
      if state.running then socket:reconnect() end
    end,
  }

  local receiver = LrSocket.bind {
    functionContext = context,
    plugin = _PLUGIN,
    port = RECEIVE_PORT,
    mode = 'receive',
    onConnecting = function(socket, port)
      log('command socket listening on ' .. tostring(port))
    end,
    onConnected = function(socket, port)
      log('command socket connected')
    end,
    onMessage = function(socket, message)
      if type(message) ~= 'string' then return end
      for line in string.gmatch(message, '[^\r\n]+') do
        LrTasks.startAsyncTask(function()
          -- LrTasks.pcall: commands may yield (catalog writes), plain pcall cannot.
          local ok, e = LrTasks.pcall(Commands.handle, line)
          if not ok then log('command failed: ' .. tostring(e)) end
        end)
      end
    end,
    onClosed = function(socket)
      log('command socket closed')
      if state.running then socket:reconnect() end
    end,
    onError = function(socket, err)
      if err ~= 'timeout' then log('command socket error: ' .. tostring(err)) end
      if state.running then socket:reconnect() end
    end,
  }

  log('sockets created')

  -- Keep the context (and both sockets) alive; report changes about 7 times a second.
  while state.running do
    if state.connected then
      local ok, err = LrTasks.pcall(Commands.poll)
      if not ok then
        log('poll failed: ' .. tostring(err))
        send('err\t' .. tostring(err))
      end
    end
    LrTasks.sleep(0.15)
  end

  log('PadNook plugin stopped')
  pcall(function() receiver:close() end)
  pcall(function() sender:close() end)
end

local M = {}

--- Starts the sockets (once). `by` says who started them: 'init' (Lightroom start) or 'menu'.
function M.start(by)
  if state.running then return false end
  if not Commands then return false end
  -- A reload of the plugin stops the previous instance first.
  local previous = getShared()
  if previous and previous ~= state then previous.running = false end
  state.running = true
  state.startedBy = by
  pcall(function() rawset(_G, 'PADNOOK', state) end)
  log('starting (' .. tostring(by) .. ')', by == 'init' and 'w' or 'a')
  LrTasks.startAsyncTask(function()
    local ok, e = LrTasks.pcall(function()
      LrFunctionContext.callWithContext('PadNook', run)
    end)
    if not ok then log('plugin failed: ' .. tostring(e)) end
    state.running = false
  end)
  return true
end

function M.status()
  local lines = {}
  lines[#lines + 1] = 'Collegamento: ' .. (state.running and 'in ascolto sulle porte 58873 e 58874' or 'FERMO')
  lines[#lines + 1] = 'PadNook collegato: ' .. (state.connected and 'sì' or 'no')
  lines[#lines + 1] = 'Avviato da: ' .. tostring(state.startedBy or 'nessuno (Init.lua non è stato eseguito)')
  if not Commands then lines[#lines + 1] = 'Errore: Commands.lua non caricato' end
  lines[#lines + 1] = 'Registro: ' .. tostring(logPath)
  lines[#lines + 1] = ''
  for _, l in ipairs(recent) do lines[#lines + 1] = l end
  return table.concat(lines, '\n')
end

function M.log(text) log(text) end

return M
