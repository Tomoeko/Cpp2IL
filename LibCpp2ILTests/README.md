# Parser integration tests

The default suite loads the tracked synthetic players under `TestFiles/` locally. It requires no network access after package restore. These fixtures cover parser behavior; they do not establish recovered-code compilation or exact Unity 2021.3.35f1 support.

```sh
dotnet test --project LibCpp2ILTests/LibCpp2ILTests.csproj -c Release
```

Five legacy external-sample tests are skipped unless `CPP2IL_RUN_EXTERNAL_PARSER_TESTS=1` is set explicitly. They await downloads and parsing, so a download or parse failure fails its test. Downloads are cached only in the gitignored `Files/parser-samples/` directory; an existing cached sample avoids another request.

```sh
CPP2IL_RUN_EXTERNAL_PARSER_TESTS=1 dotnet test --project LibCpp2ILTests/LibCpp2ILTests.csproj -c Release
```

External availability is separate from offline parser coverage. Keep the external tests optional in ordinary CI and report their skipped state accurately.
