# Releasing

Releases are published from GitHub Actions with NuGet
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the workflow exchanges a
GitHub OIDC token for a short-lived NuGet key, so no API key is stored in the repository.

## One-time setup

1. On nuget.org, open your account's **Trusted Publishing** page and add a policy:
   - repository owner `OnurGumus`, repository `FsiMcp`
   - workflow file `release.yml`
2. In the GitHub repository, add the secret `NUGET_USER` = your nuget.org **profile name** (not your email).

## Each release

```sh
git tag v0.1.0
git push origin v0.1.0
```

`.github/workflows/release.yml` then runs the tests, packs with the tag's version (also stamped into
`.mcp/server.json`), pushes to NuGet and creates a GitHub release with the `.nupkg` attached.
