# Copilot instructions for this repository

These instructions help AI coding agents work productively in this codebase. Keep edits concise and verify assumptions against the workspace before proceeding.

## Repo snapshot
- Root: `s:\Savvori`
- Solution: `Savvori.sln`
- SDK: .NET 10.0.200 (pinned via `global.json`, rolls forward to latest patch)
- Projects:
   - App Host: `src/Savvori.AppHost/Savvori.AppHost.csproj` (Aspire orchestration, Aspire SDK 13.2.1)
   - Service Defaults: `src/Savvori.ServiceDefaults/Savvori.ServiceDefaults.csproj` (OpenTelemetry, health checks, resilience)
   - Web API: `src/Savvori.WebApi/Savvori.WebApi.csproj` (ASP.NET Core minimal API, .NET 10)
   - Web App: `src/Savvori.WebApp/Savvori.WebApp.csproj` (Razor Pages, .NET 10)
   - Shared: `src/Savvori.Shared/Savvori.Shared.csproj` (EF Core models/DbContext)
   - Tests: `tests/Savvori.Web.Tests/Savvori.Web.Tests.csproj` (xUnit)

## First steps for any task
1. Operate via the solution: prefer `Savvori.sln` for adds/refs/builds.
2. Confirm target paths before running commands; use Windows PowerShell syntax.
3. If adding projects, keep to `src/` and `tests/` layout and add them to the solution.
4. Use `$env:USERPROFILE\.dotnet\tools\aspire.exe` or ensure `~/.dotnet/tools` is in PATH for Aspire CLI.

## Build, run, test
- Build all:
   - `dotnet build Savvori.sln`
- Run with Aspire (recommended — starts PostgreSQL container via Podman, dashboard, all services):
   - `aspire run` (from repo root, or `& "$env:USERPROFILE\.dotnet\tools\aspire.exe" run`)
   - Aspire dashboard: http://localhost:15888 (auto-opens)
   - WebApi URL injected by Aspire
- Run the web API directly (requires local PostgreSQL at `ConnectionStrings:savvori`):
   - `dotnet run --project src/Savvori.WebApi/Savvori.WebApi.csproj`
   - Development endpoints:
      - GET http://localhost:5000/weatherforecast
      - OpenAPI (dev): http://localhost:5000/openapi/v1.json
      - Health: http://localhost:5000/health
- Run tests:
   - `dotnet test Savvori.sln`

Notes:
- **Container runtime**: Podman 5.8.1. `ASPIRE_CONTAINER_RUNTIME=podman` is set as a user environment variable. Aspire uses Podman to run the PostgreSQL container.
- All projects target `net10.0`. Aspire packages are at 13.2.x. Keep versions consistent.

## Architectural conventions
- Minimal API in `Program.cs` defines endpoints directly. Example:
   - `app.MapGet("/weatherforecast", ...).WithName("GetWeatherForecast");`
- Configuration: `appsettings.json` + `appsettings.Development.json` in the web project.
- Database connection: resolved from `ConnectionStrings:savvori` (key name used by Aspire client integration `builder.AddNpgsqlDbContext<SavvoriDbContext>("savvori")`).
- Namespaces/projects follow `Savvori.*`. New code should align with this naming.
- ServiceDefaults (`builder.AddServiceDefaults()` / `app.MapDefaultEndpoints()`) must be wired in all executable projects.

## Aspire AppHost conventions
- Add new projects to orchestration in `Savvori.AppHost/AppHost.cs`.
- Use `builder.AddProject<Projects.Savvori_ProjectName>("resource-name")` for .NET projects.
- Use `.WithReference(resource).WaitFor(resource)` to wire database dependencies.
- Add infrastructure (DBs, caches, etc.) via Aspire hosting integrations (`dotnet add package Aspire.Hosting.*`).

## Working patterns for agents
- When adding endpoints, extend `Program.cs` or introduce modules with `MapGroup` as needed; keep routing consistent.
- When adding services, register them via `builder.Services` in `Program.cs`. Prefer constructor injection for testability.
- Tests live under `tests/` with xUnit. Add at least a happy-path test when changing public behavior.
- Update the solution file whenever you add/remove projects or references.
- New HTTP clients should use `AddStandardResilienceHandler()` (already applied globally via ServiceDefaults).

## Aspire MCP
- The Aspire MCP server is configured in `.vscode/mcp.json` (type: stdio, command: `aspire agent mcp`).
- Aspire MCP tools (list_resources, console_logs, traces, etc.) are available when the AppHost is running.
- Start the AppHost first with `aspire run`, then use Aspire MCP tools to inspect running services.

## Common tasks: concrete examples
- Add a new test project:
   - `dotnet new xunit -n Savvori.Api.Tests -o tests/Savvori.Api.Tests`
   - `dotnet sln Savvori.sln add tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj`
- Reference the web project from tests:
   - `dotnet add tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj reference src/Savvori.WebApi/Savvori.WebApi.csproj`
- Add an Aspire infrastructure integration:
   - `aspire add <integration-name>` (from repo root)
- Add a new project to Aspire orchestration:
   - Add `ProjectReference` in AppHost.csproj, then `builder.AddProject<Projects.ProjectName>("name")` in AppHost.cs

## Quality gates before PRs
- Build succeeds and `dotnet test` passes locally.
- No stray changes in `bin/`/`obj/` (ensure `.gitignore` excludes them; keep defaults).
- README and these instructions updated when public behavior, run commands, or structure change.

Keep this file short and practical. Adjust examples when versions/paths change.

---

# Agent Development and Documentation Protocol

This section defines the workflow and standards for AI coding agents working in this repository. The goal is to ensure that every new or changed functional requirement is fully understood, documented, implemented, and tested to a high standard.

## 1. Requirement Clarification
- When a new functional requirement is described, the agent must:
   - Ask clarifying questions to fully understand the feature, its scope, and acceptance criteria.
   - If the requirement is a change to an existing feature, clarify the nature and impact of the change.
   - If the feature is complex, suggest splitting it into smaller, manageable steps.

## 2. Functional Documentation
- The agent must:
   - Update the functional documentation to include the new feature or update the relevant section if it is a change.
   - Ensure the documentation clearly describes:
      - The purpose and scope of the feature
      - User stories or scenarios
      - Acceptance criteria
      - Any dependencies or constraints

## 3. Technical Documentation
- The agent must:
   - Review existing technical documentation for relevant context.
   - Ask questions to clarify technical details as needed.
   - Update technical documentation to reflect any new or changed implementation details, including:
      - Architecture and design decisions
      - Data models and APIs
      - Integration points
      - Error handling and edge cases

## 4. Implementation
- The agent must:
   - Develop the feature according to the clarified requirements and updated documentation.
   - Follow repository conventions and best practices.
   - If the feature is too complex, break it into multiple PRs or steps as appropriate.

## 5. Testing
- The agent must:
   - Ensure automated tests exist for all new or changed functionality.
   - Target at least 85% code coverage for the relevant code.
   - Add or update tests as needed (unit, integration, or end-to-end).
   - Do not consider the feature complete until all tests pass.

## 6. Review and Iteration
- The agent must:
   - Review the implementation and documentation for completeness and clarity.
   - Iterate as needed based on feedback or test results.

## 7. Completion Criteria
- The feature is only considered complete when:
   - Functional and technical documentation are up to date
   - Implementation matches the requirements
   - Tests exist and pass with at least 85% coverage

---

**Note:** This protocol is mandatory for all agent-driven development in this repository. Any deviation must be justified and documented.
