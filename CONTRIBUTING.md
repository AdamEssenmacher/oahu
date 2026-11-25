# Contributing to Oahu

Oahu is under construction. It is being built in a planned sequence of small pull requests, and nothing is published yet.

- Bug reports and questions are welcome as [issues](https://github.com/AdamEssenmacher/oahu/issues).
- For anything beyond a small fix (a typo, a broken link, a bug with an obvious fix), open an issue before you write code. The core is being assembled in a fixed order, so a feature pull request that was not discussed first will probably be closed, however good it is. Asking first saves you that work.
- Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md), not in an issue.

## Build and check

You need the .NET 11 SDK, version 11.0.100 or any later 11.0 release; `global.json` enforces this and does not allow preview SDKs. Run these commands from the repository root:

```sh
dotnet build Oahu.slnx -c Release
dotnet format Oahu.slnx --verify-no-changes
dotnet pack Oahu.slnx -c Release -o artifacts/packages
```

To fix formatting rather than check it, run `dotnet format Oahu.slnx`. Packages are written to `artifacts/packages`, which git ignores. Each package comes with a `.snupkg` symbol package.

The solution has no test projects yet.

CI runs these steps, plus `dotnet test Oahu.slnx -c Release`, on every pull request. The `Build and test (Linux)`, `Build and test (Windows)` and `Build and test (macOS)` checks build and test on each operating system. `Format and pack` verifies formatting, packs, and fails if a package has no symbol package. All four must pass.

## Pull requests

A pull request must:

- Add one discrete piece of value, with its documentation in the same pull request. Documentation means XML documentation on public API and, for user-visible behavior, the README.
- Have a title in the imperative, such as `Add the wire format codec`. Pull requests are squash-merged, so the title becomes the commit subject on `main`. Push changes made in response to review as new commits; do not force-push. If your branch conflicts with `main`, merge `main` into it.
- Record public API changes in the project's `PublicAPI.Unshipped.txt`. The build fails with RS0016 for a public symbol that is not listed and RS0017 for a listed symbol that no longer exists; add or remove the line the diagnostic names (your IDE's code fix for RS0016 writes it for you). Do not edit `PublicAPI.Shipped.txt`; entries move there at release, and removing a shipped symbol is a breaking change to raise in an issue first.
- Have XML documentation comments on every public type and member in library projects. A missing one fails the build with CS1591.
- Build cleanly. Warnings are errors, and the code style rules in `.editorconfig` are enforced in the build.
- Keep library code trim- and AOT-compatible. Every library project sets `IsAotCompatible`, so the trim and AOT analyzers run on every build and a violation fails it.
- Add any new package version to `Directory.Packages.props`. Project files reference packages without a version.

To add a library project, add it to `Oahu.slnx` and copy `IsAotCompatible`, `GenerateDocumentationFile` and the `Microsoft.CodeAnalysis.PublicApiAnalyzers` reference from `src/core/AdamE.Oahu/AdamE.Oahu.csproj`. Then create `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` next to the project file, each starting with the line `#nullable enable`, as in `src/core/AdamE.Oahu`. If one file is missing, the build fails with RS0048. If `PublicAPI.Shipped.txt` lacks `#nullable enable`, it fails with RS0037 once the API uses nullable annotations.

## License

By contributing, you agree that your contribution is licensed under the [MIT license](LICENSE) that covers this repository.
