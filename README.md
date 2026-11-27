# Murly AI

Murly AI is a language-learning chat app. Each stored lesson opens in Learning or Practice. Signed-in learners have private, separate chat histories per lesson and mode.

## Run the local demo

Install Node.js 22.12+ and the .NET 10 SDK. Run these from the project root in separate terminals.

```sh
dotnet run --project server
```

```sh
cd client
npm ci
npm run dev
```

Open [localhost:5173](http://localhost:5173). The Vite dev server proxies API calls to port 5074. The default launch profile uses a local SQLite database, an automatic demo identity, three sample Spanish lessons, and deterministic demo replies. The demo profile is available only in the Development environment.

To try the OpenAI tutor locally, save the key with .NET user secrets, then start the API with local demo identity and lessons but with the mock tutor disabled.

```sh
dotnet user-secrets set --project server OpenAI:ApiKey "<your key>"
ASPNETCORE_ENVIRONMENT=Development Demo__Enabled=true Demo__MockTutor=false \
  ASPNETCORE_URLS=http://localhost:5074 dotnet run --project server --no-launch-profile
```

Choose the model with `OpenAI__Model`. The default is `gpt-4.1-mini`. Tutor requests send the lesson, the current prompt, and up to 20 previous exchanges to OpenAI and disable Responses API storage. Editable instructions are in [learning.md](server/Instructions/learning.md) and [practice.md](server/Instructions/practice.md).

## Import your curriculum

Use [demo.json](server/Curriculum/demo.json) as the format reference. Each entry takes a stable lowercase hyphenated `id` (maximum 80 characters), `title`, `targetLanguage`, non-negative `displayOrder`, `openingContent`, and optional `examples`. The opening and replies support line breaks and `**bold**`. An import validates every lesson before saving and updates matching IDs in place, so those chats stay linked. Omitted lessons remain published.

The Azure production database starts empty. Run database migrations first, then run the import with the path to your curriculum. For a deployed build, run these from the publish directory with `ConnectionStrings__Murly` set for the database. With source, use `dotnet run --project server --no-launch-profile --` followed by either command. Pass an absolute curriculum path when using `dotnet run`.

```sh
dotnet Murly.Api.dll migrate
dotnet Murly.Api.dll import-curriculum /path/to/your-curriculum.json
```

Run the commands using an operator identity with the required permissions. The migration identity needs schema permissions, and the import identity needs lesson read/write access. The web app's identity only needs data access.

## Azure deployment

1. Create an Azure SQL database and an Azure App Service with the .NET 10 runtime. Enable the App Service system-assigned managed identity, HTTPS-only, and database network access from the app. Set `ConnectionStrings__Murly` to `Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False`. As the database administrator, create a contained user for the managed identity and grant `SELECT` on `Lessons`, `SELECT, INSERT, UPDATE` on `Chats`, and `SELECT, INSERT` on `Turns` after the migration.
2. In a Microsoft Entra External ID external tenant, create a web app registration, associate it with a sign-up and sign-in user flow, and configure `https://<app-host>/signin-oidc` and `https://<app-host>/signout-callback-oidc` as web redirect URIs. Set `Identity__Authority` to the issuer URL published in that tenant's OpenID discovery document, `Identity__ClientId` to the app registration client ID, and `Identity__ClientSecret` to a Key Vault reference. For App Service Linux TLS termination, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so ASP.NET Core builds HTTPS callback URLs. Restrict direct app access to the trusted HTTPS ingress.
3. Set `OpenAI__ApiKey` to a Key Vault reference, such as `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<secret>)`. Grant the managed identity permission to read the referenced secrets. Keep `Demo__Enabled` disabled in production.
4. Run the frontend build before publishing the API. Deploy the contents of `publish/` to App Service with your deployment pipeline. ASP.NET Core serves the React build and the API from the same origin.

   ```sh
   npm --prefix client ci
   npm --prefix client run build
   dotnet publish server -c Release -o publish
   ```

5. Run the migration and import with an authorized database connection, then confirm that sign-in, sign-out, and one live tutor reply per mode work in the deployed app. Monitor App Service logs for database and tutor request failures.

## Verification

```sh
dotnet test tests
npm --prefix client run build
```

The integration suite uses SQLite and a simulated OpenAI HTTP transport. It exercises learner isolation, prompt mode selection, curriculum reimport, timeouts, and concurrent or repeated submissions. Live Azure and OpenAI verification requires configured services and your production curriculum.
