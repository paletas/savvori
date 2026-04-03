# Copilot instructions for this repository

These instructions help AI coding agents work productively in this codebase. Keep edits concise and verify assumptions against the workspace before proceeding.

## Repo snapshot
- Root: `d:\Savvori`
- Solution: `Savvori.sln`
- Projects:
   - Web API: `src/Savvori.WebApi/Savvori.WebApi.csproj` (ASP.NET Core minimal API, .NET 10 RC)
   - Tests: `tests/Savvori.Web.Tests/Savvori.Web.Tests.csproj` (xUnit)

## First steps for any task
1. Operate via the solution: prefer `Savvori.sln` for adds/refs/builds.
2. Confirm target paths before running commands; use Windows PowerShell syntax.
3. If adding projects, keep to `src/` and `tests/` layout and add them to the solution.

## Build, run, test
- Build all:
   - `dotnet build Savvori.sln`
- Run the web app:
   - `dotnet run --project src/Savvori.WebApi/Savvori.WebApi.csproj`
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
   - `dotnet add tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj reference src/Savvori.WebApi/Savvori.WebApi.csproj`

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
