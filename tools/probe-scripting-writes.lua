-- Does a script actually write to the score? This is the final step of the scripting investigation.
--
-- Prerequisites, learned the hard way:
--   * Dorico open, WITH a document that has at least one player - a brand-new project is a Setup-mode shell with
--     no staves, so it has no voice stream to write into.
--   * Your work saved: a probe like this has already crashed Dorico once, by calling C++ constructors with
--     plausible-but-wrong argument lists (the crash was an ACCESS_VIOLATION inside the MSVC exception machinery,
--     so a Lua pcall does not protect you). Everything below calls only constructors whose signatures are known,
--     but "known" is not "safe".
--
-- Run it:
--   dorico-use command send --name Script.RunScript \
--     --param "ScriptPath=<repo>/tools/probe-scripting-writes.lua" --param ShowConsole=false
--   cat .spinney/scripting/write-probe.out
--
-- The output file is written and flushed line by line, so if the process dies the last line names the call that
-- did it. Nothing is saved to disk by this script; the write lives in the document until you undecide it (Ctrl+Z),
-- which is also what `session read` will show afterwards.

local path = '<repo>/.spinney/scripting/write-probe.out'
local file = io.open(path, 'w')

local function w(...)
  local parts = {}
  for index = 1, select('#', ...) do parts[#parts + 1] = tostring((select(index, ...))) end
  file:write(table.concat(parts, ' ') .. '\n')
  file:flush()
end

local function attempt(label, body)
  local ok, result = pcall(body)
  w(label, '->', tostring(ok), tostring(result))
  return ok, result
end

w('=== write probe ===')
local app = DoApp.DoApp()
w('versionString', app.versionString)

local score = app.currentScore
w('score', tostring(score))

local flows = score.flows
w('flows', tostring(#flows))

if #flows == 0 then
  w('no flow in this document: open a project that has at least one player and run this again')
  w('=== end ===')
  file:close()
  return
end

local flow = flows[1]
local streams = flow.voiceStreams
w('voice streams', tostring(#streams))

if #streams == 0 then
  w('no voice stream in this flow: the document has no staves yet')
  w('=== end ===')
  file:close()
  return
end

local stream = streams[1]
w('stream', tostring(stream))

local function notes()
  local count = 0
  for _ in string.gmatch(stream:dump(), 'kNoteEvent') do count = count + 1 end
  return count
end

w('notes before', notes())

-- The three arguments, built from the types the registration exposes: a length from a number, and a pitch from a
-- MIDI number plus the spelling letter - the same pair the project file stores.
local Length = DoCore.RhythmicLength
local Pitch = DoCore.MIDIPitch
local Spelled = DoCore.MIDIPitchWithSpelling
local Name = DoCore.NoteName

w('--- building the arguments ---')
attempt('RhythmicLength(0)', function() return Length(0) end)
attempt('RhythmicLength(2)', function() return Length(2) end)
attempt('MIDIPitch(60)', function() return Pitch(60) end)
attempt('NoteName.fromString("C")', function() return Name.fromString('C') end)
attempt('MIDIPitchWithSpelling(MIDIPitch(60), "C")', function() return Spelled(Pitch(60), Name.fromString('C')) end)

w('--- the write itself ---')
local ok, created = attempt('stream:createNote(0, 2, C4)', function()
  return stream:createNote(Length(0), Length(2), Spelled(Pitch(60), Name.fromString('C')))
end)
w('created', tostring(created))

w('notes after', notes())
w('=== end ===')
file:close()
