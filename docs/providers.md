# GitHub and Azure DevOps

The repository importer discovers repositories accessible to your existing account, including private repositories. Select the repositories you want to save in GitHistory. Discovery and pull request lookups only read provider metadata.

## Sign-in and private repositories

| Provider | Existing sign-in | Optional import credential |
| --- | --- | --- |
| GitHub.com | Install GitHub CLI and run `gh auth login` once. | A GitHub personal access token authorized for the repositories you need. Fine-grained tokens need repository access and Pull requests (read) for PR details. |
| Azure DevOps Services | Install Azure CLI and run `az login` for the organization's Microsoft Entra tenant. Enter the organization name or its `https://dev.azure.com/organization` URL, and optionally a project. | An Azure DevOps PAT with Code (Read). |

Organization SSO and account policy still apply. GitHub organization filtering narrows the list returned for your account; it cannot grant additional access. Azure DevOps can list repositories across an organization or within one project.

An optional token remains in process memory for provider metadata queries until GitHistory closes. Tokens are never written to settings, the history database, log messages, or process arguments. They are not embedded in clone URLs. Reopen Import to replace an expired token.

**Git fetch uses its own authentication.** GitHistory invokes installed Git, so configure Git Credential Manager or SSH credentials for the private repository independently. An API import token does not configure Git or a credential helper. Existing GitHub CLI credentials can be connected to Git with `gh auth setup-git` if that is your preferred setup; run this yourself because it changes Git configuration.

## Browser links and pull requests

GitHub HTTPS and SSH remotes, Azure `dev.azure.com` URLs, legacy `organization.visualstudio.com` URLs, and Azure SSH `v3/organization/project/repository` remotes resolve to provider browser pages. Repository, branch, file, commit, and pull request links preserve URL escaping for spaces, branch slashes, and unusual file names. Unknown Git hosts do not receive guessed provider links.

The PR lookup asks GitHub for pull requests associated with the selected commit. For Azure DevOps, it queries both commits contained in a PR and the merge commit created when completing a PR. PR information comes from the provider and is independent of the cached Git history. If metadata is unavailable, recognized merge commit subjects can still provide a link labeled **From commit message · unverified**. Such a link does not claim a verified title, author, or PR state.

Provider requests are cancellable and have bounded response sizes. HTTP requests do not follow redirects with credentials. GitHub pagination uses numbered requests on its fixed API origin; Azure continuation tokens are escaped as values within the original organization URL.

## Validation

Deterministic provider tests cover private repository discovery, pagination, CLI argument construction, credential scoping, canonical URLs, associated PRs, malformed origins, cancellation, and sanitized errors. Run them with:

```powershell
dotnet test tests/GitHistory.Tests --filter FullyQualifiedName~Providers
```

The optional live test reads GitHub repository metadata using the current CLI sign-in. Its output contains only repository counts:

```powershell
$env:GITHISTORY_PROVIDER_SMOKE = '1'
dotnet test tests/GitHistory.Tests --filter Category=ProviderSmoke --logger 'console;verbosity=detailed'
Remove-Item Env:GITHISTORY_PROVIDER_SMOKE
```

Azure DevOps behavior is covered by API fixtures. A live Azure organization sign-in is needed for an end-to-end tenant-specific check.

## API references

- [GitHub: repositories for the authenticated user](https://docs.github.com/en/rest/repos/repos#list-repositories-for-the-authenticated-user)
- [GitHub: pull requests associated with a commit](https://docs.github.com/en/rest/commits/commits#list-pull-requests-associated-with-a-commit)
- [GitHub CLI API command](https://cli.github.com/manual/gh_api)
- [Azure DevOps: list Git repositories](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/repositories/list?view=azure-devops-rest-7.1)
- [Azure DevOps: pull request query by commit](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-request-query/get?view=azure-devops-rest-7.1)
- [Azure CLI: obtain an Entra token for Azure DevOps](https://learn.microsoft.com/en-us/azure/devops/cli/entra-tokens?view=azure-devops)
