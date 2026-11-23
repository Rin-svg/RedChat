#!/usr/bin/env bash
# Exécute les tests Core, Infrastructure et Presentation SANS NuGet (sandbox) grâce au banc du skill csharp-gui-dev.
# Chez toi (avec Internet) : « dotnet test » depuis la racine de la solution.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SKILL="${SKILL_DIR:-/mnt/skills/plugins/csharp-gui-dev}"
BANC="${BANC_DIR:-/tmp/banc}"
rm -rf "$BANC" && mkdir -p "$BANC/Core" "$BANC/Infrastructure" "$BANC/Presentation" "$BANC/Core.Tests"
cp "$ROOT/Directory.Build.props" "$ROOT/.editorconfig" "$BANC/"
cp "$ROOT"/src/RedChat.Core/*.cs "$BANC/Core/"
cat > "$BANC/Core/RedChat.Core.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>RedChat.Core</RootNamespace></PropertyGroup>
<ItemGroup><InternalsVisibleTo Include="RedChat.Core.Tests" /></ItemGroup></Project>
XML
cp "$ROOT"/src/RedChat.Infrastructure/*.cs "$BANC/Infrastructure/"
mkdir -p "$BANC/Infrastructure/Security" && cp "$ROOT"/src/RedChat.Infrastructure/Security/*.cs "$BANC/Infrastructure/Security/"
cat > "$BANC/Infrastructure/RedChat.Infrastructure.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>RedChat.Infrastructure</RootNamespace></PropertyGroup>
<ItemGroup><ProjectReference Include="..\Core\RedChat.Core.csproj" /></ItemGroup>
<ItemGroup><InternalsVisibleTo Include="RedChat.Core.Tests" /></ItemGroup></Project>
XML
cp "$ROOT"/src/RedChat.Presentation/*.cs "$BANC/Presentation/"
cat > "$BANC/Presentation/RedChat.Presentation.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>RedChat.Presentation</RootNamespace></PropertyGroup>
<ItemGroup><ProjectReference Include="..\Core\RedChat.Core.csproj" /><ProjectReference Include="..\Infrastructure\RedChat.Infrastructure.csproj" /></ItemGroup></Project>
XML
cp "$ROOT"/tests/RedChat.Core.Tests/*.cs "$BANC/Core.Tests/"
cp "$ROOT"/tests/RedChat.Infrastructure.Tests/*.cs "$BANC/Core.Tests/"   # fusionnés dans un seul exécutable de banc
cp "$ROOT"/tests/RedChat.Presentation.Tests/*.cs "$BANC/Core.Tests/"
cp "$SKILL"/assets/sandbox-harness/*.cs "$BANC/Core.Tests/"
cat > "$BANC/Core.Tests/RedChat.Core.Tests.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><ProjectReference Include="..\Core\RedChat.Core.csproj" /><ProjectReference Include="..\Infrastructure\RedChat.Infrastructure.csproj" /><ProjectReference Include="..\Presentation\RedChat.Presentation.csproj" /></ItemGroup></Project>
XML
cd "$BANC"
dotnet build Core.Tests -p:NuGetAudit=false "$@" 2>&1 | grep -E "error|warn|Error\(s\)|Warn.*\(s\)" | sort -u | head -40
dotnet run --no-build --project Core.Tests
