# Notes for Claude Code in this repo

Read `README.md` first. It covers setup, configuration, the tools and the change workflow.

## Setting up for a new user
1. Run `.\setup.ps1` (PowerShell). It checks prerequisites, builds, tests and publishes. It also creates
   `%APPDATA%\VisualCronAI\appsettings.json` from `config\appsettings.user.sample.json` and writes `.mcp.json`.
2. The user fills the `<...>` placeholders in `%APPDATA%\VisualCronAI\appsettings.json`: the server address and the ADF
   subscription, resource groups and factories. Ask them for these values. They are not secrets.
3. The user stores their VisualCron login themselves with `cmdkey` (printed by setup.ps1).
4. The user restarts Claude Code in this folder and approves the `visualcron` MCP server. Then call
   `visualcron_test_connection` for DEV.

## Rules
- **Never ask for, accept, print or store a password, client secret, token or SAS URL.** If the user pastes one, tell them to
  rotate it. Authentication happens only inside the bridge, via the Windows logon or Windows Credential Manager.
- **Do not change `AllowWrites`, `WritableJobGroups`, `JobGroups` or `Approval` settings on your own initiative.** These are
  the user's safety gates. Change one only when the user explicitly asks, and tell them what changed. Never enable PROD
  writes unless asked explicitly.
- Every write goes through the two-phase workflow:
  prepare (`visualcron_update_job` etc.) → show the user the changes → `visualcron_request_approval` (a human approves in a
  dialog) → `visualcron_apply_approved_changes`. Never try to approve on the user's behalf.
- Run a dry run first: prepare one proposal, show the changes and warnings, then do the batch.
- Environments are job groups on one shared server (`DEV: ...`, `UAT: ...`, `PROD: ...`). Always pass `environment`.
- Test new job behaviour on DEV with one manual run before rolling it out to UAT.
- After changes, confirm with a read-back (`visualcron_list_jobs` / `visualcron_get_job`) and report per job.

## Known VisualCron behaviour (verified on a live 12.1.3 server)
- Flow "IfOutput" conditions are evaluated against **ConditionValue2**, not ConditionValue1. The bridge maps this.
- Triggers store the creating client's UTC offset; the bridge stamps the server's offset.
- Task references `{TASK(<id>|StdOut)}` must point at tasks of the same job. Copied jobs often point at another job's tasks;
  `adfPollUntilComplete` repairs this.
- Job names may carry trailing spaces; lookups trim them.

## Build and test
`.\build.ps1` builds, runs all tests and publishes to `dist\`. Restart the MCP server after a rebuild (`/mcp` in Claude Code).
