# Blake.SourceGenerator

Roslyn source generator for `blakelang`, compiling `.blake` files to `.cs`.

## Publishing

1. Update the version in `Blake.SourceGenerator.csproj`
2. Update the version in `Sample.App.csproj`
3. Update the version in `src/Blake.VsCode/package.json`
3. Run
```
cd src/Blake.SourceGenerator
dotnet pack
```
4. Publish to nuget