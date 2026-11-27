-- Status section at the top of File > Gestione plug-in for PadNook.
return {
  sectionsForTopOfDialog = function(f, propertyTable)
    local text
    local ok, e = pcall(function() text = require('Server').status() end)
    if not ok then text = 'Errore del plugin: ' .. tostring(e) end
    return {
      {
        title = 'PadNook',
        f:static_text {
          title = text .. '\n\nSe il collegamento è FERMO: menu Libreria > Extra plug-in > PadNook: stato e collegamento.',
          width_in_chars = 80,
          height_in_lines = 16,
          selectable = true,
        },
      },
    }
  end,
}
