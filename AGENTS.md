# Project verification

After every code or configuration change, run `pwsh -NoProfile -File ./Tests/Run-All.ps1` from the project root. Fix any failures and rerun the command before reporting completion. If a failure cannot be fixed within the task scope or a check cannot run, report the affected check and the reason. Claim a check passed only if it ran and passed.
