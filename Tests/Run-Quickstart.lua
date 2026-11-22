-- Load from cl_lua after starting a fresh single-player quickstart.
local root = "LocalMods/Enhanced Experience/Tests/"
local scenarios = {
    expedition = { "Expedition", "Dugong", "findweapon factory remains vanilla: " },
    exterior = { "ExteriorFollow", "Dugong", "Dismissal clears follow: " },
    cleanup = { "MedicalCleanup", "Dugong", "Fixture: COMPLETE" },
    deconstruction = { "CrewDeconstruction", "Humpback", "Fixture complete: PASS" },
    security = { "SecurityDefense", "Humpback", "COMPLETE" },
    repair = { "CrewRepair", false, "COMPLETE" },
    coordination = { "CrewRepair", false, "COMPLETE" },
    inspection = { "EquipmentInspection", false, "COMPLETE" },
    inspectionreport = { "EquipmentInspection", false, "COMPLETE" },
    handoffunconscious = { "CrewRepair", false, "COMPLETE" },
    handoffdead = { "CrewRepair", false, "COMPLETE" },
    maintenance = { "Maintenance", "Humpback", "COMPLETE" },
}
local key = QuickstartScenario
QuickstartScenario = nil
local scenario = assert(scenarios[key], "Unknown QuickstartScenario")

LuaUserData.RegisterType("Barotrauma.GameMain")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local session = main.GameSession
local mode = session and session.GameMode and session.GameMode.Preset.Identifier.Value
assert(not main.Client and session and session.IsRunning and not session.RoundEnding and
    mode == "devsandbox" and Submarine.MainSub and Character.Controlled,
    string.format("Quickstart unavailable (client=%s, session=%s, running=%s, ending=%s, mode=%s, submarine=%s, controlled=%s)",
        tostring(main.Client ~= nil), tostring(session ~= nil), tostring(session and session.IsRunning),
        tostring(session and session.RoundEnding), tostring(mode), tostring(Submarine.MainSub ~= nil),
        tostring(Character.Controlled ~= nil)))
local submarine = session.SubmarineInfo.Name
assert(not scenario[2] or submarine == "1" or submarine == scenario[2],
    "Start quickstart 1 or quickstart " .. tostring(scenario[2]) .. " for " .. key)
assert(EnhancedExperienceQuickstartSession ~= session,
    "Start a fresh round before running another scenario")

local reportPath = root .. "QuickstartRun-" .. key .. ".log"
local details = ""
local finished = false
local failed, skipped, passed = false, false, false
local function report(status, reason)
    local header = key .. " (" .. submarine .. "): " .. status .. " - " .. reason
    File.Write(reportPath, header .. "\n" .. details)
    if status ~= "RUNNING" then
        finished = true
        print("[Quickstart] " .. header .. " | " .. reportPath)
    end
end

local function writeResult(_, contents)
    if finished or main.GameSession ~= session or session.RoundEnding then return end
    details = contents
    local complete = false
    failed, skipped, passed = false, false, false
    for line in contents:gmatch("[^\r\n]+") do
        failed = failed or line:match("^FAIL%f[%A]") or line:match("^ERROR%f[%A]") or line:match(": FAIL%f[%A]")
        skipped = skipped or line:match("^SKIP%f[%A]") or line:match(": NOT TESTED%f[%A]")
        passed = passed or line:match("^PASS%f[%A]") or line:match(": PASS%f[%A]")
        if key == "expedition" or key == "exterior" then
            complete = complete or line:sub(1, #scenario[3]) == scenario[3]
        else
            complete = complete or line == scenario[3]
        end
    end

    if complete then
        if failed then report("FAIL", "one or more assertions failed")
        elseif skipped then report("SKIP", "completed with skipped assertions")
        elseif passed then report("PASS", "scenario completed")
        else report("FAIL", "scenario completed without assertions") end
    else
        report("RUNNING", "waiting for the scenario to finish")
    end
end

report("RUNNING", "starting scenario")
EnhancedExperienceQuickstartSession = session
local previousWriter = EnhancedExperienceQuickstartWriteResult
EnhancedExperienceQuickstartWriteResult = writeResult
if key == "coordination" then CrewRepairEmergency = true end
if key == "inspectionreport" then EquipmentInspectionReport = true end
if key == "handoffunconscious" or key == "handoffdead" then CrewRepairHandoff = key end
local ok, message = pcall(dofile, root .. scenario[1] .. "Quickstart.lua")
EnhancedExperienceQuickstartWriteResult = previousWriter
if not ok then
    report("FAIL", "could not start scenario: " .. tostring(message))
elseif not finished then
    Timer.Wait(function()
        if not finished and main.GameSession == session and not session.RoundEnding then
            report("FAIL", "scenario did not complete within 300 seconds")
        end
    end, 300000)
    print("[Quickstart] Started " .. key .. " | " .. reportPath)
end
