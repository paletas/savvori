# Copilot instructions for this repository

These instructions help AI coding agents work productively in this codebase. Keep edits concise and verify assumptions against the workspace before proceeding.

## Repo snapshot
- Root: `d:\Savvori`
- Solution: `Savvori.sln`
- Projects:
   - Web API: `src/Savvori.Web/Savvori.Web.csproj` (ASP.NET Core minimal API, .NET 10 RC)
   - Tests: `tests/Savvori.Web.Tests/Savvori.Web.Tests.csproj` (xUnit)

## First steps for any task
1. Operate via the solution: prefer `Savvori.sln` for adds/refs/builds.
2. Confirm target paths before running commands; use Windows PowerShell syntax.
3. If adding projects, keep to `src/` and `tests/` layout and add them to the solution.

## Build, run, test
- Build all:
   - `dotnet build Savvori.sln`
- Run the web app:
   - `dotnet run --project src/Savvori.Web/Savvori.Web.csproj`
   - Development endpoints:
      - GET http://localhost:5000/weatherforecast
      - OpenAPI (dev): http://localhost:5000/openapi/v1.json
- Run tests:
   - `dotnet test Savvori.sln`

Notes:
- The web project targets `net10.0` and currently references `Microsoft.AspNetCore.OpenApi` RC packages. Keep versions consistent across changes.

## Architectural conventions
- Minimal API in `Program.cs` defines endpoints directly. Example:
   - `app.MapGet("/weatherforecast", ...).WithName("GetWeatherForecast");`
- Configuration: `appsettings.json` + `appsettings.Development.json` in the web project.
- Namespaces/projects follow `Savvori.*`. New code should align with this naming.

## Working patterns for agents
- When adding endpoints, extend `Program.cs` or introduce modules with `MapGroup` as needed; keep routing consistent.
- When adding services, register them via `builder.Services` in `Program.cs`. Prefer constructor injection for testability.
- Tests live under `tests/` with xUnit. Add at least a happy-path test when changing public behavior.
- Update the solution file whenever you add/remove projects or references.

## Common tasks: concrete examples
- Add a new test project:
   - `dotnet new xunit -n Savvori.Api.Tests -o tests/Savvori.Api.Tests`
   - `dotnet sln Savvori.sln add tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj`
- Reference the web project from tests:
   - `dotnet add tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj reference src/Savvori.Web/Savvori.Web.csproj`

## Quality gates before PRs
- Build succeeds and `dotnet test` passes locally.
- No stray changes in `bin/`/`obj/` (ensure `.gitignore` excludes them; keep defaults).
- README and these instructions updated when public behavior, run commands, or structure change.

Keep this file short and practical. Adjust examples when versions/paths change.
