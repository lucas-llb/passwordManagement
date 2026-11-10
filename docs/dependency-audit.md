# Auditoria de dependências (W-31)

> Data: **2026-11-10**. Retrato do grafo do app Windows no dia da auditoria. Dependência nova ou versão nova exige atualizar este documento no mesmo PR (AGENTS §9), e a guarda `DependencyAuditTests` falha se o documento e o grafo divergirem.

**Método.** O grafo foi lido **offline**, dos arquivos do próprio repositório: `windows/Directory.Packages.props` (as versões centrais) e os seis `packages.lock.json` comitados (as versões resolvidas e os `contentHash`). Nada foi resolvido pela rede nesta auditoria. As regras aplicadas são o AGENTS §7 (lista permitida, o que é proibido) e o princípio de offline de verdade do AGENTS §1. Pacote é julgado pelo que ele é e por onde ele entra, não pelo nome: os três projetos que embarcam no MSIX são `PasswordManager.App`, `PasswordManager.ViewModels` e `PasswordManager.Core`; os três projetos de teste nunca entram no pacote.

## Lista final de pacotes diretos

São os seis `PackageVersion` de `windows/Directory.Packages.props`, cada um com o motivo e o veredito. Nenhuma referência tem `Version` inline: a versão vem sempre daqui (Central Package Management).

| Pacote | Versão | Projeto(s) | Motivo | Veredito |
|---|---|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | `PasswordManager.ViewModels`, `PasswordManager.App` | MVVM: `ObservableObject`, comandos e mensagens; é a lista permitida do AGENTS §7 | **Aceito** |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2705 | `PasswordManager.App` | Ferramenta de build do SDK do Windows (empacotamento, ferramentas do SDK); não é assembly de runtime do app | **Aceito**, só de build |
| `Microsoft.WindowsAppSDK` | 2.5.1 | `PasswordManager.App` | Windows App SDK / WinUI 3: a própria UI da tela | **Aceito** |
| `NSec.Cryptography` | 26.4.0 | `PasswordManager.Core` | Binding de libsodium; Argon2id e XChaCha20-Poly1305 exigidos pelo AGENTS §3.2 | **Aceito** |
| `FlaUI.UIA3` | 5.0.0 | `PasswordManager.App.E2E.Tests` | Automação de UI nos testes E2E (AGENTS §8); nunca embarca no MSIX | **Aceito**, só de teste |
| `xunit.v3` | 4.0.2 | os três projetos de teste | Framework de testes (xUnit v3 sobre Microsoft.Testing.Platform); nunca embarca no MSIX | **Aceito**, só de teste |

## Grafo transitivo do app

Uma linha por pacote que aparece nos lockfiles do que embarca: `PasswordManager.App` (19 nomes únicos, somando os três diretos) e o que chega por `PasswordManager.Core`/`PasswordManager.ViewModels`. As versões são as do lockfile de 2026-11-10; sobem junto com o Windows App SDK e não são contrato — o contrato é o veredito.

| Pacote | Versão | Como entra | Veredito |
|---|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | Direto no App e no ViewModels | **Aceito**: MVVM, sem rede |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2705 | Direto no App; também pedido por `Microsoft.WindowsAppSDK.Base` (vencedor é a versão central) | **Aceito**, só de build |
| `Microsoft.WindowsAppSDK` | 2.5.1 | Direto no App; metapackage que traz a família abaixo | **Aceito** |
| `Microsoft.WindowsAppSDK.WinUI` | 2.3.9 | Família do metapackage | **Aceito**: é a UI que o app renderiza |
| `Microsoft.WindowsAppSDK.Foundation` | 2.3.12 | Família do metapackage | **Aceito**: infraestrutura do Windows App SDK usada pelo WinUI |
| `Microsoft.WindowsAppSDK.Base` | 2.0.4 | Família do metapackage | **Aceito**: base comum da família, usada em runtime |
| `Microsoft.WindowsAppSDK.DWrite` | 2.1.0 | Família do metapackage | **Aceito**: texto/renderização usados pelo WinUI |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 2.1.9 | Família do metapackage | **Aceito**: entrada e composição usadas pelo WinUI |
| `Microsoft.WindowsAppSDK.Runtime` | 2.5.1 | Família do metapackage | **Aceito**: runtime do Windows App SDK |
| `Microsoft.WindowsAppSDK.AI` | 2.5.5 | Família do metapackage | **Aceito, não usado**: nada em `windows/src` referencia as APIs de IA; sai do grafo só deixando de usar o metapackage |
| `Microsoft.WindowsAppSDK.ML` | 2.1.94 | Família do metapackage | **Aceito, não usado**: idem; é o componente que arrasta `Microsoft.Windows.AI.MachineLearning` |
| `Microsoft.WindowsAppSDK.Search` | 2.5.5 | Família do metapackage | **Aceito, não usado**: o app tem busca própria, em memória |
| `Microsoft.WindowsAppSDK.Widgets` | 2.0.5 | Família do metapackage | **Aceito, não usado**: o app não tem widgets |
| `Microsoft.Windows.AI.MachineLearning` | 2.1.74 | Transitivo de `Microsoft.WindowsAppSDK.ML` | **Aceito, não usado**: nenhuma API de IA/ML é chamada no código do app |
| `System.Numerics.Tensors` | 9.0.0 | Transitivo de `Microsoft.Windows.AI.MachineLearning` | **Aceito, não usado**: só existe para o componente de ML |
| `Microsoft.Windows.SDK.BuildTools.MSIX` | 1.7.251221100 | Transitivo de `Microsoft.WindowsAppSDK.Base` | **Aceito**, só de build: ferramenta de empacotamento MSIX; não é assembly do app |
| `NSec.Cryptography` | 26.4.0 | Direto no Core; aparece como `CentralTransitive` no lockfile do App | **Aceito**: cripto do cofre (AGENTS §3.2) |
| `libsodium` | 1.0.22 | Transitivo de `NSec.Cryptography` | **Aceito**: a biblioteca nativa de cripto, sem rede |
| `Microsoft.Web.WebView2` | 1.0.3719.77 | Transitivo de `Microsoft.WindowsAppSDK.WinUI` | **Aceito, sem uso de rede**: é um controle de navegador embutido que só fala com a rede quando o app hospeda um WebView2. O app não tem WebView2 nenhum, e a guarda `Sources_UseNeitherWebView2NorTheWindowsAiApis` mais a prova de firewall da W-33 mantêm assim |

**Ponto em aberto.** `Microsoft.WindowsAppSDK.AI`, `.ML`, `.Search`, `.Widgets`, `Microsoft.Windows.AI.MachineLearning` e `System.Numerics.Tensors` embarcam sem uso porque vêm do metapackage `Microsoft.WindowsAppSDK`, e removê-los exigiria abandonar o metapackage. Sair deles não é escopo desta história: fica registrado para uma história futura avaliar o uso dos pacotes individuais do Windows App SDK.

## Só de teste

Nada aqui entra no MSIX. Os três projetos de teste (`PasswordManager.Core.Tests`, `PasswordManager.ViewModels.Tests`, `PasswordManager.App.E2E.Tests`) são os únicos que referenciam esses pacotes; `App`, `Core` e `ViewModels` não os referenciam, e o projeto E2E referencia o App com `ReferenceOutputAssembly="false"`, então nem o binário do app é copiado para o runner.

- **xUnit v3** (`xunit.v3` direto; `xunit.v3.mtp-v2`, `xunit.v3.core.mtp-v2`, `xunit.v3.assert`, `xunit.v3.common`, `xunit.v3.extensibility.core`, `xunit.v3.runner.common`, `xunit.v3.runner.inproc.console`, `xunit.analyzers` transitivos).
- **Microsoft.Testing.Platform** (`Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild`, `Microsoft.Testing.Extensions.TrxReport.Abstractions`) — o runner que o `xunit.v3` usa.
- **Telemetria do runner** (`Microsoft.Testing.Extensions.Telemetry` 2.5.1 e `Microsoft.ApplicationInsights` 2.23.0). **Este é o único lugar da solução onde existe um pacote de telemetria**, e ele é aceitável por três razões: (a) entra por `xunit.v3` → `Microsoft.Testing.Platform`, não por escolha do app; (b) só existe no processo do runner de testes, na máquina de desenvolvimento ou de CI, e o app que a pessoa usa nunca o carrega; (c) não aparece em nenhum lockfile de `App`/`Core`/`ViewModels` nem no MSIX. A guarda `ShippedGraph_HasNoNetworkOrTelemetryPackage` proíbe esses nomes no grafo embarcado, e `TestTooling_StaysOutOfTheShippedProjects` fixa essa separação.
- **FlaUI** (`FlaUI.UIA3` direto; `FlaUI.Core`, `Interop.UIAutomationClient`) e os auxiliares de WMI/desenho que o FlaUI puxa (`System.Diagnostics.PerformanceCounter`, `System.Drawing.Common`, `System.Management`, `System.Security.Permissions`, `System.Configuration.ConfigurationManager`, `System.Diagnostics.EventLog`, `System.Security.Cryptography.ProtectedData`, `System.CodeDom`, `Microsoft.Win32.Registry`, `Microsoft.Win32.SystemEvents`, `System.Windows.Extensions`, `System.Security.AccessControl`, `Microsoft.Bcl.AsyncInterfaces`) — automação de UI dos testes E2E.
- Pelo encadeamento de projetos, os lockfiles de teste também carregam `NSec.Cryptography` e `libsodium` (via Core) e `CommunityToolkit.Mvvm` (via ViewModels); são as mesmas dependências já aprovadas no grafo embarcado.

## Só de build

`Microsoft.NET.ILLink.Tasks` 10.0.10 aparece como `Direct` nos lockfiles de `PasswordManager.Core` e `PasswordManager.ViewModels` porque os dois projetos são `IsTrimmable`; o SDK o injeta no restore. É tarefa de build (trimming), não assembly que embarca no app: não está no lockfile do App nem no conteúdo do MSIX. O caminho de publish da W-29 (`windows/tools/Publish-Release.ps1`) restaura com `RestorePackagesWithLockFile=false`, o que o mantém fora do lockfile comitado desse caminho.

## Veredito

Contra o AGENTS §7: **nenhum pacote embarcado é de rede, analytics, anúncios ou crash reporting remoto**. O grafo que entra no MSIX é WinUI/Windows App SDK (UI), CommunityToolkit.Mvvm (MVVM), NSec/libsodium (cripto permitida) e ferramentas de build; o WebView2 que vem junto com o WinUI está presente mas não é hospedado por nenhum código do app, e os componentes de IA/ML do metapackage embarcam sem uso. Os dois nomes de "telemetria" da solução — `Microsoft.ApplicationInsights` e `Microsoft.Testing.Extensions.Telemetry` — existem só no grafo de teste, nunca no app. O que esta auditoria **não** prova: que o app não gera tráfego em tempo de execução — isso é a prova de firewall da W-33; e que segredos não vazam por log ou `ToString()` — isso é a W-32. Ela também cobre pacotes, não licenças: os avisos de terceiros dos pacotes NuGet embarcados (WinUI/Windows App SDK, libsodium, NSec, xunit) continuam em aberto em `THIRD-PARTY-NOTICES.md`, que hoje só cobre a lista de palavras EFF.

## Como reproduzir

A partir de `windows/`:

```
dotnet test --project tests/PasswordManager.App.E2E.Tests/PasswordManager.App.E2E.Tests.csproj --filter-class PasswordManager.App.E2E.Tests.Packaging.DependencyAuditTests
```

A guarda estática lê estes arquivos (sem abrir janela e sem rede): `windows/Directory.Packages.props`, os seis `windows/**/packages.lock.json`, os seis `.csproj`, os fontes de `windows/src` e este documento.

Para conferir o grafo resolvido projeto a projeto (usa o restore existente; se tentar acessar a rede, pule — o lockfile é a fonte auditada):

```
dotnet list src/PasswordManager.App/PasswordManager.App.csproj package --include-transitive
dotnet list src/PasswordManager.Core/PasswordManager.Core.csproj package --include-transitive
dotnet list src/PasswordManager.ViewModels/PasswordManager.ViewModels.csproj package --include-transitive
```

Os lockfiles auditados são:

- `windows/src/PasswordManager.App/packages.lock.json`
- `windows/src/PasswordManager.Core/packages.lock.json`
- `windows/src/PasswordManager.ViewModels/packages.lock.json`
- `windows/tests/PasswordManager.Core.Tests/packages.lock.json`
- `windows/tests/PasswordManager.ViewModels.Tests/packages.lock.json`
- `windows/tests/PasswordManager.App.E2E.Tests/packages.lock.json`

E, para provar que a auditoria não mudou pacote nem lockfile:

```
git diff --exit-code -- windows/Directory.Packages.props windows/Directory.Build.props "windows/**/packages.lock.json"
```
