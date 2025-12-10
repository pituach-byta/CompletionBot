<!-- .github/copilot-instructions.md - guidance for AI coding agents working on CompletionBot -->
# CompletionBot — AI agent instructions

Purpose: give an AI coding agent the minimal, repository-specific knowledge to be productive.

- **Big picture**: this is a two-tier app:
  - `Server/` — ASP.NET Core Web API (controllers in `Server/Controllers`, DB logic in `Server/Services/DbService.cs`, models in `Server/Models/Entities.cs`).
  - `Client/` — React + Vite single-page app (`package.json` scripts: `dev`, `build`, `preview`).

- **Primary data flow & responsibilities**:
  - Admins upload an Excel workbook to `POST api/admin/upload-excel`, which the server saves to `Server/Data/Current_Debts.xlsx` and syncs to the SQL database (see `AdminController.UploadExcel`).
  - Students interact with the bot via `POST api/chat/message` (see `ChatController.SendMessage`) — input may be a national ID (login step) or a chat message. The server returns a `BotResponse` object (fields: `Reply`, `ActionType`, `Data`, `StudentId`).
  - The `DbService` wraps Dapper/SqlClient connections; it expects a `ConnectionStrings:DefaultConnection` entry in `Server/appsettings.json`.

- **Key files to reference**:
  - `Server/Program.cs` — DI registration, CORS policy (`AllowReactApp`), Swagger setup, HTTPS redirection intentionally disabled for dev.
  - `Server/Controllers/AdminController.cs` — Excel sync logic, upsert semantics, and export endpoints.
  - `Server/Controllers/ChatController.cs` — chat flow (ID lookup → show debts → handle unpaid/paid branching), builds payloads sent to the client.
  - `Server/Services/DbService.cs` — database access helpers and helper methods used by controllers.
  - `Server/Models/Entities.cs` — DTOs and models used across controllers (Student, StudentDebt, ChatRequest, BotResponse).
  - `Server/appsettings.json` — contains `DefaultConnection`, `OpenAI:ApiKey`, and `FileStorage:BasePath` placeholders.
  - `Client/` — React UI; uses `axios` to call server endpoints on `http://localhost:5219` (server default in `Server/Properties/launchSettings.json`).

- **Run / build / debug commands (Windows PowerShell)**
  - Build & run server (from repo root):
    ```powershell
    dotnet restore
    dotnet run --project Server
    ```
    The development launch profile exposes the API at `http://localhost:5219` (Swagger available at `/swagger`).

  - Start client (from `Client/`):
    ```powershell
    cd Client
    npm install
    npm run dev
    ```
    The client dev server runs on `http://localhost:5173` by default; the server `CORS` policy whitelists that origin.

- **Configuration and secrets**:
  - `Server/appsettings.json` contains a `ConnectionStrings:DefaultConnection` placeholder. `DbService` throws if the connection string is missing — set this in `appsettings.Development.json`, environment variables, or `dotnet user-secrets` for local dev.
  - `OpenAI:ApiKey` is read in `ChatController` from configuration. Add your key under that path or via env var `OpenAI__ApiKey`.

- **Patterns & conventions specific to this repo**
  - Controllers return localized (Hebrew) messages and often wrap non-critical errors as `Ok(...)` rather than exceptions (e.g., validation returns `Ok(BotResponse)` with guidance). Respect these response shapes.
  - DB access uses Dapper with manual SQL (see `AdminController` upsert/merge queries and `DbService` methods). Prefer small, explicit SQL edits close to where they are used.
  - Excel-driven workflow: `AdminController.UploadExcel` is authoritative for data — updates mark old debts `IsActive=0` then upsert/merge into `StudentDebts`. When modifying sync logic, preserve this three-step approach (mark inactive → upsert/activate → commit transaction).
  - File storage: server saves uploads to `Server/Data` and user-uploaded submissions to `Server/BotUploads` (see `AdminController` & `Properties` usage).

- **Integration points to be careful with**
  - SQL Server connection — the existing connection string points to a local instance (`Server=COMP\\BOTWORKSDB`). Changing DB schema requires updating the Excel-to-DB mapping in `AdminController`.
  - External payment config at `NedarimPlus` in `appsettings.json` and `PaymentController` (payments callback URL configured there). Check `Server/Controllers/PaymentController.cs` before modifying payment flows.
  - OpenAI usage: key is taken from config in `ChatController`. Keep API calls centralized; avoid hardcoding keys in code.

- **Quick examples for common tasks**
  - To fetch debts for a student (login-step), client posts to `POST /api/chat/message` with body `{ "UserMessage": "<id>" }`. Server responds with `BotResponse.ActionType = "ShowDebts"` and `Data` containing debt objects (see `ChatController`).
  - To run the Excel sync locally: upload a file via `POST /api/admin/upload-excel` (Admin UI or curl with `multipart/form-data`). The server saves to `Server/Data/Current_Debts.xlsx` and updates the DB in a transaction.

- **When editing code an AI agent should**
  - Preserve DB transaction boundaries in `AdminController` when changing sync logic.
  - Keep response DTO shapes in `Server/Models/Entities.cs` stable (`BotResponse`, `ChatRequest`) unless you update both server and client simultaneously.
  - Respect the CORS policy name `AllowReactApp` if adding middleware or changing origins (client dev server uses `http://localhost:5173`).

If anything here is unclear or you'd like me to expand examples (e.g., show typical `axios` calls from `Client/src` or the exact SQL schema used), tell me which area to expand and I'll iterate.
