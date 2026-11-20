# VisualCronAI — MCP server for VisualCron job management

Manage real VisualCron jobs from Claude Code or GitHub Copilot (VS Code) in plain English. Nothing is written
until a **human approves** the exact change. Jobs that run **Azure Data Factory** pipelines are first-class.

## Quick start (after cloning)
**You need:** Windows, the **VisualCron 12 client** installed (it provides `VisualCronAPI.dll`), the **.NET 10 SDK**
(`winget install Microsoft.DotNet.SDK.10`), [Claude Code](https://claude.com/claude-code), and network access to the
VisualCron server (connect the VPN/Zscaler first if your server needs it).

```powershell
git clone <this-repo-url> visualcron-mcp
cd visualcron-mcp
.\setup.ps1      # checks prerequisites, builds + tests, creates your config, writes .mcp.json
```
Then:
1. **Edit `%APPDATA%\VisualCronAI\appsettings.json`** and replace every `<...>` placeholder: the server address and the ADF
   subscription, resource group and factory per environment. Ask your team for these values; they are not secrets.
2. **Store your VisualCron login yourself.** `cmdkey` prompts for the password, so nobody (and no AI) ever sees it:
   ```powershell
   cmdkey /generic:VisualCronAI/VCSERVER /user:DOMAIN\your.user /pass
   ```
   If your Windows logon is already a VisualCron user, set `"AuthMode": "AD"` instead and skip `cmdkey`.
3. **Start Claude Code in this folder** (`claude`), approve the `visualcron` MCP server when asked, and say:
   *"test the VisualCron connection for DEV"*.

You can also just ask Claude Code: *"Read the README and set this up for me."* It will follow the section below.

## For Claude (AI assistant): how to set up and work with this repo
Also see `CLAUDE.md`. Follow these steps when a user asks you to set this up.
1. Check prerequisites: `C:\Program Files (x86)\VisualCron\VisualCronAPI.dll` exists, and `dotnet --list-sdks` shows 10.x.
   If something is missing, tell the user what to install. Do not install system software without asking.
2. Run `.\setup.ps1`. If tests fail, report the output; don't skip them.
3. Ask the user for the **non-secret** values (server host name, ADF subscription ID, resource groups and factory names). Then
   fill the placeholders in `%APPDATA%\VisualCronAI\appsettings.json`. Keep UAT and PROD at `"AllowWrites": false`.
4. Tell the user to run the `cmdkey` command themselves. **Never ask for, accept or handle a password, client secret,
   token or SAS URL.** If one is pasted into the chat, tell the user to rotate it.
5. Ask the user to restart Claude Code (or run `/mcp`) so the `visualcron` server starts. Then call `visualcron_test_connection`
   for DEV and report the server version and job count.
6. For any change, use the workflow in [Change workflow](#change-workflow-enforced-server-side): prepare → show the changes
   → `visualcron_request_approval` (the human clicks Yes in a dialog or form) → `visualcron_apply_approved_changes` → read back.
   Never change `AllowWrites` or the group scopes unless the user explicitly asks.

### What a session looks like
| You say | What happens |
|---|---|
| *List the DEV and UAT jobs of D365* | Read-only listing: group, last run, result, next run |
| *Show me how "Devices - Trigger ADF" works* | Tasks, ADF factory, pipeline, status-check behaviour |
| *Fix the polling on the 7 DEV ADF jobs* | 7 proposals with diffs → 7 approval dialogs → applied, verified by read-back |
| *Remove the schedules, we run them manually* | Schedule triggers removed after approval; jobs stay enabled |
| *Give each job a short description* | Description proposals → approval → applied |
| *I ran the jobs — check the runs* | Per job: ADF RunId, number of status checks, final ADF status, job result |
| *Why did X fail last night?* | Job history and task output (secrets redacted) |

```
Claude Code / Copilot ──stdio/MCP──► VisualCronAI.Mcp.exe (.NET 10)
                                       │  tools, validation, proposals, approval, audit
                                       └─stdio/JSON──► bridge\VisualCronAI.Bridge.exe (.NET Framework 4.8)
                                                          └─ VisualCronAPI.dll (installed VisualCron 12) ──► VisualCron server(s)
VisualCron Execute task ──► VisualCronAI.AdfRunner.exe ──► Key Vault (SP secret) ──► ADF REST API (start / poll / cancel)
```

## Why there is a bridge process
The installed VisualCron API (`VisualCronAPI.dll` 12.1.5, `VisualCron.dll` 12.0.8) targets **.NET Framework 4.8**
and uses WCF (`System.ServiceModel`), which .NET 8/10 cannot host. The MCP server therefore runs on .NET 10 (current LTS;
.NET 8 left support in Nov 2026) and starts a small net48 bridge that is the **only** component loading vendor DLLs.
Vendor DLLs are resolved at runtime from `C:\Program Files (x86)\VisualCron` and are never copied into this repo.

All API calls used were verified by reflection against the installed DLLs (`Client.Connect`, `Server.Jobs.GetAll/Get/Add/Update/Activate/DeActivate/Remove`,
`Jobs.Tasks.Add/Update/Remove`, `Jobs.Triggers.Add/Update`, `Log.GetJobHistory/GetTaskHistory/GetTaskOutput`,
`Notifications.GetAll`, `Credentials.GetAll`, `TimeAPI.GetNextExecutions`, `JobClass.Clone/CreateNewIds`). Schedule
semantics (`DayType`, `Days[]`, `FirstRun`) were verified with VisualCron's own scheduling engine (see bridge tests).

## Projects
| Project | Purpose |
|---|---|
| `src/VisualCronAI.Mcp` | MCP server: `Program.cs`, `Tools/`, `Approval/` (elicitation), `appsettings.json` |
| `src/VisualCronAI.Core` | Interfaces, models, validation, secret scanner, schedule/time-zone normalizer, comparer, `ChangeService` (two-phase workflow) |
| `src/VisualCronAI.Infrastructure` | `Api/BridgeProcessGateway`, `Audit/JsonlAuditLog` (hash-chained), `Authentication/WindowsCurrentUser`, `Approval/WindowsDialogApprovalPrompt` |
| `src/VisualCronAI.Contracts` | netstandard2.0 DTOs + bridge wire protocol (no secret fields — enforced by a test) |
| `src/VisualCronAI.Bridge` | net48 adapter over VisualCronAPI.dll: mapping, verify-after-write, reconcile, rollback |
| `src/VisualCronAI.Adf` / `src/VisualCronAI.AdfRunner` | ADF REST client, Key Vault credential, start/poll/cancel runner exe |
| `tests/` | Unit, workflow, security, bridge-protocol, MCP end-to-end, opt-in DEV integration tests |

## Install
1. **.NET 10 SDK** (to build) and **.NET 10 runtime** (to run the MCP server). VisualCron 12 client/API installed on the
   machine running the MCP server (it provides `VisualCronAPI.dll`).
2. Build, test and publish:
   ```powershell
   .\build.ps1          # -> dist\mcp (server + bridge), dist\adf-runner
   ```
3. Configure environments in `%APPDATA%\VisualCronAI\appsettings.json` (overrides `dist\mcp\appsettings.json`; same shape):
   ```json
   { "VisualCronAI": { "Environments": {
       "DEV": { "Address": "vc-dev-server.corp.local", "AllowWrites": true },
       "UAT": { "Address": "vc-uat-server.corp.local", "AllowWrites": false } } } }
   ```
   An empty `Address` means *not configured*; the server refuses to connect rather than guessing a host.

   **One server, environments as job groups** (the default config): DEV, UAT and PROD all use the *same* `Address` and are
   separated by job group prefix (`DEV: ADF-D365 Loads`, `UAT: …`, `PROD: …`):
   - `JobGroups: ["DEV:*"]` scopes what the `DEV` environment can see and resolve (a PROD job ID is refused in DEV).
   - `WritableJobGroups: ["DEV:*"]` fences writes; a change can never move or edit a job outside those groups.
   - Job names only need to be unique within a group, so `Products - Trigger ADF` can exist in all three.
   - Cloning DEV→UAT maps `DEV: ADF-D365 Loads` → `UAT: ADF-D365 Loads` (via `EnvironmentTokens`), retargets ADF tasks,
     and re-points job dependencies to the UAT jobs of the same name. The bridge shares one connection across the environments.
   - UAT and PROD ship with `AllowWrites: false`.

### Authentication (credentials never reach the AI)
- **`AuthMode: "AD"` (default, recommended):** Windows logon of the account running the MCP server. No password stored anywhere.
  For a dedicated service account, run VS Code / Claude Code as that account, or point the MCP config at a wrapper started with `runas`.
- **`AuthMode: "CredentialManager"`:** a VisualCron user whose password you store yourself:
  `cmdkey /generic:VisualCronAI/DEV /user:svc_visualcron /pass` and set `"CredentialTarget": "VisualCronAI/DEV"`.
  Only the bridge reads it (CredRead); it is never logged, returned, or sent over MCP.
- **Least privilege:** give the account only the VisualCron permissions it needs (read jobs; modify jobs in the ADF group), and
  use `WritableJobGroups` to restrict writes server-side as well.

## Register the server
**Claude Code:** `setup.ps1` writes `.mcp.json` with this clone's absolute path. The file is git-ignored because the path is
machine-specific. Or register the server manually:
```powershell
claude mcp add visualcron --scope user -- "<clone path>\dist\mcp\VisualCronAI.Mcp.exe"
claude mcp list            # then /mcp inside Claude Code to see the tools
```
**GitHub Copilot (VS Code)** — `.vscode/mcp.json` is provided. Open the folder, run *MCP: List Servers* → `visualcron` → Start,
then in Copilot Chat choose **Agent** mode and open the tools picker to confirm the `visualcron_*` tools.

## Change workflow (enforced server-side)
1. **Prepare** — `visualcron_draft_job`+`visualcron_create_job`, `visualcron_clone_job`, `visualcron_update_job`, `visualcron_add_task`,
   `visualcron_update_task`, `visualcron_remove_task`, `visualcron_update_schedule`, `visualcron_enable_job`, `visualcron_disable_job`.
   These only create an immutable **proposal** (diff, validation, next run times computed by VisualCron's TimeAPI).
2. **Preview** — `visualcron_preview_changes`, `visualcron_validate_job`.
3. **Approve** — `visualcron_request_approval` asks the **human** via MCP elicitation (a form in Claude Code / VS Code) or, if
   the client lacks elicitation, a native Windows Yes/No dialog. The model cannot answer either.
   **Production** additionally requires: `AllowWrites: true`, your Windows account in `Approval:ProductionApprovers`, and typing
   `PROD <job name>` (or a second explicit dialog confirmation).
4. **Apply** — `visualcron_apply_approved_changes` re-checks status, approval expiry (15 min), content hash, environment write switch,
   and the job's **version token** (optimistic concurrency), writes through the bridge, reads the job back to **verify**,
   reconciles via the per-task/trigger APIs if needed, and **rolls back** (restore original / remove created job) on failure.

Every step is written to `%LOCALAPPDATA%\VisualCronAI\audit\audit-YYYYMMDD.jsonl` (redacted, SHA-256 hash-chained;
`JsonlAuditLog.VerifyChain` detects edits). VisualCron's own audit log records the server-side change too.

Other safeguards: inputs are validated (names, IDs, times, pipeline names, parameter values, lengths); job definitions that
contain password/key/token/SAS/private-key patterns are rejected; every tool response is passed through the redactor;
logs go to stderr only; proposals live in memory and expire (60 min).

## ADF jobs in the team's HTTP-task pattern (default)
Existing jobs (`DEV/UAT: ADF-D365 Loads`) call the ADF REST API with native tasks:
`Get Access Token (HTTP) → JSON filter access_token → Trigger Pipeline (HTTP createRun) → Get RunId → Wait → Get Pipeline Status (HTTP) → JSON filter status`.
The token request uses job variables `{JOB(Active|Variable|tenantid|clientid|clientsecret)}`; secrets stay on the server.
- **Read/compare:** HTTP, JSON-filter and Wait tasks are fully modeled; `visualcron_get_job` shows `adfHttp` (subscription, resource group,
  factory, pipeline, parameters, status-check behaviour). Secret-looking HTTP values are masked inside the bridge and kept on write.
- **Create:** `visualcron_create_adf_job` clones the environment's `AdfTemplateJob` (VisualCron copies its job variables), swaps the
  pipeline, retargets to the environment's `AdfSubscriptionId/AdfResourceGroup/AdfFactoryName`, sets schedule/dependencies/notifications,
  and creates it inactive. Task references `{TASK(<id>|StdOut)}` are rewritten to the clone's own tasks.
- **Fix status checking:** `visualcron_update_job` with `adfPollUntilComplete: true` turns the single check after a fixed wait into a
  loop (Wait → status → back to Wait while `Queued|InProgress|Canceling`), raises a task error for any other status, and sets a job
  timeout (`AdfMaxRunMinutes`, counted as error). **Verify with one DEV run before rolling out** (flow conditions are server-evaluated).
- **Clone DEV→UAT:** URLs are retargeted to the UAT factory; validation warns when an existing job calls another environment's factory.
- **Retarget an existing job:** `visualcron_update_job` with `adfRetargetToEnvironmentFactory: true` points its URLs at the
  environment's configured factory.
- **Manual-only jobs:** `visualcron_update_schedule` with `schedules: []` removes the schedule triggers; the job stays enabled.
- Trigger times: VisualCron stores the creating client's UTC offset on each trigger; the bridge stamps the **server's** offset on
  triggers it writes and shows existing triggers in server time.

## ADF integration (alternative: runner)
The job's task is a VisualCron **Execute** task running:
```
C:\VisualCronAI\adf-runner\VisualCronAI.AdfRunner.exe run --env DEV --pipeline p_D365_Product_Load --param LoadDate={DATEFORMAT(yyyy-MM-dd)}
```
The runner fetches the service principal's secret from **Key Vault**, calls `createRun`, prints `RunId=…`, polls every 30 s,
cancels the run on timeout (default 120 min), prints failed activity errors, and exits `0` success · `1` failed · `2` cancelled ·
`3` timeout · `4` config · `5` auth · `6` Azure API. VisualCron then applies the task's **retry flow** and the job's **failure
notifications**. Throttling/5xx while polling is retried; `createRun` is never retried on ambiguous network errors (no duplicate runs).

Deploy to **each VisualCron server**: copy `dist\adf-runner\` to the `AdfRunnerPath` you configured, create `adf-runner.json` from
`adf-runner.sample.json` (tenant, subscription, resource group, factory, SP client ID, Key Vault URI, secret name), and choose how the
runner reaches Key Vault (`KeyVaultAccess`): `ManagedIdentity` (Azure/Arc VM), `AzureCli` (`az login` once as the VisualCron task account),
`ClientCertificate` (vault-reader app + cert in the machine store) or `Default`. Grant the SP **Data Factory Contributor** on the factory and
the vault identity **Key Vault Secrets User** on the secret. Parameter types: `name:int`, `name:bool`, `name:float`, `name:json`.
Failure e-mails: create an e-mail notification once in VisualCron, then attach it (`visualcron_list_notifications` → `errorNotificationIds`).

## Example prompts
1. *Create a VisualCron job named D365_Product_Daily_Load in DEV that executes the ADF pipeline p_D365_Product_Load every day at 2 AM Central Time.*
2. *Find the DEV job for D365 Product Load and clone it for UAT. Replace environment-specific configuration and show me the differences before saving.*
3. *Find the VisualCron job named Salesforce_Account_Load and change its schedule to 3 AM.*
4. *Create a job that runs the Account pipeline first, waits for it to succeed, then runs the Contact pipeline.* (two ordered tasks; or *"…a separate Contact job that starts when the Account job completes successfully"* for a dependency trigger)
5. *Update the D365 Product Load job to retry twice after failures and send the "Data team e-mail" notification if all attempts fail.*
6. *Compare the DEV and UAT versions of the D365 Product Load job — tasks, schedules, parameters and dependencies.*
7. *Why did Salesforce_Account_Load fail last night? Show me the logs.*

## Tests and validation results (2026-11-20)
`.\build.ps1` → **136 passed, 0 failed, 3 skipped** (the skipped ones are the opt-in DEV integration tests).
- Bridge (net48, real VisualCron DLLs, no server): schedule mapping proven with `TimeAPI.GetNextExecutions` (daily, weekday, monthly,
  every-N-minutes, round trips), job/task/trigger mapping, unmodeled task settings preserved, clone ID remapping, version tokens.
- Workflow: no write without approval; rejected/expired/tampered/blocked proposals refused; writes-disabled envs; production approver +
  typed confirmation; concurrency conflict; verification failure; single-use proposals; audit sequence.
- Scenarios 1–6 above; ADF runner success/failure/timeout+cancel/transient retry/no duplicate create; ADF REST request shape.
- Security: secret detection/redaction (JSON stays valid), no password fields in contracts/config, server-side secrets redacted in tool
  output, hash-chain tamper detection.
- Process level: the real bridge exe (hello/preview/errors, unreachable server) and the real MCP server over stdio with the official MCP
  client (all 24 required tools + 3 helpers discoverable, errors returned as tool errors, unconfigured env refused without connecting).

**Verified on a live VisualCron 12.1.3 server** (shared DEV/UAT/PROD job groups) with human approval for every write:
- reading, listing and logs for all DEV/UAT ADF jobs;
- creating, test-running and deleting a test copy of an ADF job;
- the polling fix on 14 jobs: a real run looped until ADF reported Succeeded;
- description updates and schedule removal, each confirmed by read-back.

Things learned on the live server are now built in:
- flow conditions compare **ConditionValue2**;
- trigger UTC offsets;
- `{TASK(id)}` references copied from other jobs, which are repaired;
- trailing spaces in job names.

Optional opt-in integration tests (create inactive `VCAI_IT_*` jobs, verify, delete them; they refuse loopback addresses):
```powershell
$env:VISUALCRONAI_IT_DEV_ADDRESS = "<your-visualcron-server>"
dotnet test --filter Category=DevIntegration
```

## Limitations
- New tasks can be **ADF pipeline, Execute, or PowerShell**. Other VisualCron task types are read, compared, renamed, reordered,
  enabled/disabled and copied verbatim by clone, but their type-specific settings must be edited in the VisualCron client.
- Time triggers support daily/weekly/monthly times and every-N-minutes; other trigger types are read-only. VisualCron triggers use the
  server's local time; when you specify another zone the times are converted (DST-difference warnings are shown).
- Notifications and credentials are referenced, not created.
- Proposals/drafts are per-session (in memory) by design.

## Troubleshooting
| Symptom | Fix |
|---|---|
| `no VisualCron server address configured` | Set `Address` for that environment in `%APPDATA%\VisualCronAI\appsettings.json`. |
| `VisualCron bridge not found` | Run `.\build.ps1`; the bridge must be in `dist\mcp\bridge`. |
| `connection_failed` / login failed | Check host/port 16444 reachability and that your Windows account (AD mode) is a VisualCron user with permissions. |
| `No human approval channel` | Use a client with elicitation, or set `Approval:Mode` to `WindowsDialog` on an interactive desktop session. |
| `concurrency_conflict` | Someone changed the job after the proposal was prepared — prepare it again. |
| Tools missing in client | Restart the server (`/mcp` in Claude Code, *MCP: List Servers* in VS Code); check stderr log in the client's MCP output. |
| ADF task exit code 5 | Runner could not read Key Vault — check `KeyVaultAccess` and RBAC on the vault. |
