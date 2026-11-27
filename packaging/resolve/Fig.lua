-- Fig for DaVinci Resolve: Workspace > Scripts > Fig brings a Fig project's timeline into the open Resolve project.
-- Fig wrote this file and points it at itself each time it starts. If Fig has moved, open it once (or use its
-- Help > Install DaVinci Resolve importer).
local FIG = "@FIG@"

local resolve = resolve or (Resolve and Resolve()) or bmd.scriptapp("Resolve")
local fu = fu or fusion or resolve:Fusion()
local windows = package.config:sub(1, 1) == "\\"
-- set when run outside the menu (tests): no dialogs, the project and timeline come from these
local quiet_project = os.getenv("FIG_RESOLVE_PROJECT")

local function tell(title, message)
    print("Fig: " .. message)
    if quiet_project or not fu then return end
    fu:AskUser(title, { { "message", "Text", Name = "", Default = message, ReadOnly = true, Lines = 10, Wrap = true } })
end

local function fail(message)
    tell("Fig", message)
    error(message, 0)
end

local function quote(s)
    if windows then return '"' .. s .. '"' end
    return "'" .. (s:gsub("'", "'\\''")) .. "'"
end

-- Runs Fig without a window. Resolve's own library path would load Resolve's copies of libraries Fig also uses.
local function run_fig(...)
    local parts = { quote(FIG) }
    for _, a in ipairs({ ... }) do parts[#parts + 1] = quote(a) end
    local cmd = table.concat(parts, " ")
    if windows then
        cmd = '"start "" /wait /b ' .. cmd .. '"'
    else
        cmd = "env -u LD_LIBRARY_PATH -u DYLD_LIBRARY_PATH -u PYTHONPATH -u PYTHONHOME " .. cmd
    end
    local ok = os.execute(cmd)
    return ok == true or ok == 0
end

local function temp_file(name)
    local dir = os.getenv("TMPDIR") or os.getenv("TEMP") or os.getenv("TMP") or "/tmp"
    return dir .. (windows and "\\" or "/") .. "fig-resolve-" .. name .. ".lua"
end

local function read_back(path)
    local chunk = loadfile(path)
    os.remove(path)
    return chunk and chunk()
end

local function choose(title, label, options)
    local r = fu:AskUser(title, { { "choice", "Dropdown", Name = label, Options = options } })
    return r and r.choice + 1
end

-- which project, and which of its timelines
local list_path = temp_file("projects")
if not run_fig("--resolve-projects", list_path) then
    fail("Couldn't start Fig (" .. FIG .. "). Open Fig once so it can point this script at itself.")
end
local projects = read_back(list_path) or fail("Fig didn't list its projects.")
if #projects == 0 then fail("There are no Fig projects yet.") end

local pick, timeline
if quiet_project then
    for _, p in ipairs(projects) do
        if p.id == quiet_project then pick = p end
    end
    pick = pick or fail("No Fig project " .. quiet_project)
    timeline = tonumber(os.getenv("FIG_RESOLVE_TIMELINE") or "1")
else
    local names = {}
    for i, p in ipairs(projects) do names[i] = p.name end
    local i = choose("Import from Fig", "Project", names)
    if not i then return end
    pick = projects[i]
    timeline = 1
    if #pick.timelines > 1 then
        timeline = choose("Import from Fig", "Timeline", pick.timelines)
        if not timeline then return end
    end
end

local manifest_path = temp_file("export")
run_fig("--resolve-export", pick.id, tostring(timeline), manifest_path)
local m = read_back(manifest_path) or fail("Fig didn't write the timeline.")
if m.error then fail(m.error) end

local project = resolve:GetProjectManager():GetCurrentProject() or fail("Open a project in Resolve first.")
local pool = project:GetMediaPool()
-- a project with no timelines yet takes the reel's size and frame rate
if project:GetTimelineCount() == 0 then
    project:SetSetting("timelineResolutionWidth", tostring(m.width))
    project:SetSetting("timelineResolutionHeight", tostring(m.height))
    project:SetSetting("timelineFrameRate", m.fps)
end
-- the reel's media goes in a bin named after it, the same one each time
local root, folder = pool:GetRootFolder(), nil
for _, f in ipairs(root:GetSubFolderList() or {}) do
    if f:GetName() == m.name then folder = f end
end
folder = folder or pool:AddSubFolder(root, m.name)
if folder then pool:SetCurrentFolder(folder) end

-- Resolve won't import over a timeline of the same name: importing again makes "<name> 2"
local taken = {}
for i = 1, project:GetTimelineCount() do taken[project:GetTimelineByIndex(i):GetName()] = true end
local name, n = m.name .. " - " .. m.timeline, 1
while taken[name] do
    n = n + 1
    name = m.name .. " - " .. m.timeline .. " " .. n
end

local tl = pool:ImportTimelineFromFile(m.xml, { timelineName = name })
if not tl then fail("Resolve couldn't read the timeline Fig wrote at " .. m.xml) end
if tl:GetSetting("timelineResolutionWidth") ~= tostring(m.width) or tl:GetSetting("timelineResolutionHeight") ~= tostring(m.height) then
    tl:SetSetting("useCustomSettings", "1")
    tl:SetSetting("timelineResolutionWidth", tostring(m.width))
    tl:SetSetting("timelineResolutionHeight", tostring(m.height))
end
project:SetCurrentTimeline(tl)

local notes = m.notes
local rate = tonumber(tl:GetSetting("timelineFrameRate"))
if rate and math.abs(rate - tonumber(m.fps)) > 0.01 then
    table.insert(notes, 1, "This Resolve project runs at " .. rate .. " fps and the reel at " .. m.fps
        .. ": cuts land on the nearest frame. Import into a new project to keep the reel's rate.")
end
if #notes > 0 then
    tell("Imported from Fig", "\"" .. name .. "\" is in. What didn't come over:\n\n- " .. table.concat(notes, "\n- "))
else
    print("Fig: imported " .. name)
end
