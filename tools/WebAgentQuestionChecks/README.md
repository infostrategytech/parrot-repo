# Inbox attention contract checks

These checks use controlled frontend state and HTTP/DOM fakes. They do not load
demo customer data, sign in to a real account, or contact an API.

Build `Parrot.Web/Parrot.Web/Parrot.Web.csproj` using the repository's normal
frontend build first. Consume the committed Figma token outputs read-only.
Then run from the repository root:

```powershell
dotnet run --project tools/WebAgentQuestionChecks/WebAgentQuestionChecks.csproj
dotnet run --project tools/WebTaskQuestionChecks/WebTaskQuestionChecks.csproj
node --test tools/web-inbox-scroll.test.mjs
npm.cmd run check --prefix design-system/tools
```

If the web build uses a custom output directory, pass its absolute path to
both .NET checks as `-p:ParrotWebBin=C:/path/to/web-output/` (keep the trailing
slash). Default references point to the normal Debug/net10.0 web output, not a
local demo build.

Coverage: personally routed conversation input vs operational tasks; old/new
summary JSON compatibility; confirmed reply vs note/uncertain send/history
read; agent-question links/identity; attention paging; unavailable real-mode
consultation controls; independent task/response state; measured-footer sizing,
reading-position preservation and binding cleanup.
