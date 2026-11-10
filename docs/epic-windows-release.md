# Épico: lançamento da versão Windows 1.0

> Status: **planejamento** · Plataforma: Windows 10 (19041+) / 11, x64 e ARM64
> Regras de segurança, formato e convenções: [AGENTS.md](../AGENTS.md), [vault-format.md](vault-format.md), [threat-model.md](threat-model.md).

## 1. Objetivo

Publicar a primeira versão estável do app Windows: um gerenciador de senhas offline, com múltiplos perfis, que cria, abre e edita cofres `.pmvault` no formato v1 **congelado**, e que exporta arquivos que o app Android vai conseguir abrir.

**Pronto quando:** um usuário instala o MSIX assinado, cria um perfil, cadastra entradas, bloqueia/desbloqueia, exporta para `.pmvault` e CSV e importa de volta, sem que o app tenha qualquer capacidade de rede, e todos os itens da [seção 7](#7-checklist-de-release-definition-of-done) estão marcados.

## 2. Escopo

**Dentro (MVP da seção 1 do AGENTS.md, versão Windows):**

- Perfis múltiplos, cada um com cofre e senha mestra próprios.
- Desbloqueio por senha mestra (Argon2id) com atraso progressivo após erros.
- Kit de Recuperação (decisão D1).
- CRUD de entradas, busca local, favoritos, pastas.
- Gerador de senhas e cópia para a área de transferência com limpeza em 30 s, fora do histórico e da nuvem.
- Bloqueio automático: inatividade, bloqueio da sessão do Windows, opcionalmente ao minimizar.
- Troca de senha mestra.
- Exportação/importação `.pmvault` e CSV (colunas Bitwarden/KeePass).
- Configurações em uma tela só.
- Proteção contra captura de tela (`WDA_EXCLUDEFROMCAPTURE`).
- Pacote MSIX assinado, sem `internetClient`.

**Fora:** Windows Hello, sync, extensão de navegador, autoatualização pela rede, telemetria/crash reporting, app Android (só a parte de `core` necessária para validar os vetores, ver seção 5).

## 3. Ponto de partida (out/2026)

| Item | Estado |
|---|---|
| Solução `.slnx`, CPM, lockfiles, analisadores, warnings como erro | feito |
| `PasswordManager.Core`: modelos (`VaultEntry`, `VaultContent`), constantes (`VaultFormat`), JSON source-gen | feito |
| Testes: só serialização JSON | parcial |
| Cripto (KDF, wrap/unwrap, AEAD), leitura/escrita do arquivo | não iniciado |
| App WinUI: janela vazia com título vindo do `.resw` | esqueleto |
| Empacotamento: `WindowsPackageType=None` (sem ícones na partida; os ícones vieram na W-27) | pendente |
| `spec/test-vectors/`: só README | pendente |

## 4. Decisões que precisam sair antes de começar

Estas mudam o formato ou a distribuição; decidir antes das fases que dependem delas.

| # | Decisão | Bloqueia | Recomendação |
|---|---|---|---|
| D1 | Kit de Recuperação entra na 1.0? Se sim, onde fica o segundo envelope da Vault Key | congelamento do formato (F1) | **Decidido (W-01):** Kit na 1.0 = slot tipo 2 da tabela de slots; senha = slot tipo 1; biometria/Windows Hello ficam em arquivo local separado (AGENTS §4.1), fora do cabeçalho. Ver vault-format.md. |
| D2 | Canal de distribuição: Microsoft Store, MSIX assinado no GitHub Releases, winget | F6 | Store (assinatura e atualização feitas pela loja) + MSIX no GitHub Releases para quem não usa a loja. A loja não exige `internetClient` no app. |
| D3 | Certificado de assinatura para o MSIX fora da loja | F6 | Certificado de code signing (OV ou Azure Trusted Signing). Chave **fora do repositório**. |
| D4 | Medidor de força da senha | F3 | **Decidido (W-16):** estimador próprio no `Core` (`PasswordStrength`), sem dependência nova; port de zxcvbn descartado (pacote novo, dicionários grandes). Menor custo em bits entre força bruta, palavras (lista EFF curta + lista própria de senhas comuns), repetições, sequências e anos. Medidor só informativo; a regra que bloqueia continua sendo ≥ 12 caracteres. |
| D5 | Trimming do app WinUI | F6 | **Decidido (W-29):** desligar o trimming no App e manter `IsTrimmable`/`IsAotCompatible` no Core (o ramo de fallback). Com o alvo `_EnsurePublishItemsForOptimization`, o trimming de verdade falha no build com IL2104 em `Microsoft.Windows.SDK.NET`/`WinRT.Runtime` e depois NETSDK1144: o Windows App SDK/WinRT não é compatível com trimming. O `-Trim` de `tools/Publish-Release.ps1` fica como opt-in documentado que falha hoje, sem suprimir aviso nenhum; o teste manual completo (W-34) só decide de novo se o Windows App SDK passar a suportar trimming. ReadyToRun continua o padrão (aplicado de verdade, §9.17). |
| D6 | Versão mínima do Windows | F6 | Manter `10.0.19041` (Windows 10 2004). Confirmar que `WDA_EXCLUDEFROMCAPTURE` (2004+) e as APIs de clipboard funcionam nela. |

## 5. Fases e histórias

IDs `W-xx` para referência em branches/PRs. Cada fase termina com `dotnet test --solution windows/PasswordManager.slnx` verde.

### F1 — Core: cripto e formato do cofre

Toda a lógica fica em `PasswordManager.Core`, testável sem UI.

| ID | História | Critérios de aceite |
|---|---|---|
| W-01 | Fechar o layout v1 com a decisão D1 | `vault-format.md` atualizado (sem "rascunho"); `VaultFormat.cs` e `VaultFormat.kt` espelham as constantes. |
| W-02 | Derivação da KEK com Argon2id (NSec) | Parâmetros lidos do cabeçalho; senha em `byte[]`/`char[]` zerada após uso; teste com valores conhecidos. |
| W-03 | Wrap/unwrap da Vault Key (XChaCha20-Poly1305) | AAD = cabeçalho conforme spec; senha errada → erro tipado, sem detalhe do motivo; chaves zeradas com `CryptographicOperations.ZeroMemory`. |
| W-04 | Cifrar/decifrar o corpo JSON | Nonce novo a cada gravação; byte alterado no cabeçalho ou no corpo → falha (teste para cada região). |
| W-05 | Leitura/escrita do arquivo `.pmvault` | Escrita atômica: temporário → `Flush(true)` → substituir original; `.bak` da versão anterior; arquivo truncado ou magic/versão inválidos → erro claro. |
| W-06 | Repositório de perfis (`profiles.json`) | Criar, listar, renomear, excluir perfil (excluir pede confirmação na UI e remove `.pmvault` e `.bak`); pasta `%LOCALAPPDATA%\<App>\vaults\` ou `LocalFolder` quando empacotado. |
| W-07 | Troca de senha mestra | Vault Key não muda; só o slot de senha é re-envolvido (salt e nonce novos); slot do Kit intacto; corpo re-selado com a mesma Vault Key e nonce novo (cabeçalho é AAD); senha antiga deixa de abrir, nova abre. |
| W-08 | Kit de Recuperação | Chave de 128+ bits do CSPRNG, formato legível (grupos de caracteres); slot tipo 2; texto Crockford base32 8×4 conforme vault-format.md; abre o cofre sem a senha mestra; permite definir nova senha. |
| W-09 | Fachada `VaultSession` | API única para a UI: `Unlock`, `Lock`, `Save`, `ChangePassword`, `Export`, `Import`. `Lock` zera chaves e descarta o modelo. |
| W-10 | Vetores de teste compartilhados | `spec/test-vectors/` com ao menos: cofre vazio, cofre com entradas (inclusive Unicode/emoji), cofre com Kit de Recuperação, exportação. Teste do Core abre todos. |

**Dependência cruzada:** o formato v1 só é considerado congelado quando o `core` Android também passar nos vetores de W-10 (seção 8 do AGENTS.md). Isso exige implementar a cripto do `core` Android em paralelo (sem UI). Depois do lançamento, qualquer mudança vira v2.

### F2 — App: fundação

| ID | História | Critérios de aceite |
|---|---|---|
| W-11 | Estrutura MVVM e navegação | `CommunityToolkit.Mvvm`; navegação Perfil → Desbloqueio → Lista → Detalhe; ViewModels testáveis sem janela. |
| W-12 | Recursos e localização | Todo texto em `Strings/pt-BR/Resources.resw`; nenhum literal de UI no código. |
| W-13 | Tema e acessibilidade | Segue tema do sistema; `AutomationProperties.Name` em todos os controles interativos; navegação completa por teclado; contraste AA. |
| W-14 | Proteção da janela | `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` ligado enquanto o cofre estiver aberto; verificado com Ferramenta de Captura e OBS. |

### F3 — App: perfis e desbloqueio

| ID | História | Critérios de aceite |
|---|---|---|
| W-15 | Tela de seleção de perfil | Lista nomes de `profiles.json`; criar/renomear/excluir. |
| W-16 | Criação de perfil | Senha mestra ≥ 12 caracteres, medidor de força (D4), sugestão de frase-senha, aviso explícito de que **não há recuperação** pelo app; exibe o Kit de Recuperação uma vez. |
| W-17 | Desbloqueio | Argon2id fora da thread de UI com indicador de progresso; atraso progressivo 1, 2, 4… até 30 s; tempo de desbloqueio medido em máquina modesta. |
| W-18 | Bloqueio automático | Inatividade (padrão 5 min), bloqueio da sessão do Windows (`SessionSwitch` ou `WTSRegisterSessionNotification`), opcional ao minimizar; ao bloquear a UI volta ao desbloqueio sem resquícios das entradas. |

### F4 — App: entradas

| ID | História | Critérios de aceite |
|---|---|---|
| W-19 | Lista com busca | Busca local por título, usuário, URL e pasta; filtros favoritos/pasta; responsivo com 2.000 entradas. |
| W-20 | Detalhe e edição | Todos os campos do `VaultEntry`; senha oculta por padrão com botão mostrar; `createdAt`/`updatedAt` mantidos; salvar grava atomicamente; inclui criar e excluir entrada. |
| W-21 | Gerador de senhas | Embutido no formulário; tamanho (padrão 20), maiúsculas, números, símbolos; `RandomNumberGenerator`, sem viés de módulo; testes no Core; minúsculas sempre incluídas; ao menos um caractere de cada classe ligada. |
| W-22 | Área de transferência | `ClipboardContentOptions` com `IsAllowedInHistory = false` e `IsRoamable = false`; limpa após 30 s só se o conteúdo ainda for o copiado; testado com Win+V ligado; botão Copiar senha no detalhe e Ctrl+C na senha mostrada; limpa também ao bloquear e ao fechar o app; marcador `ExcludeClipboardContentFromMonitorProcessing`. |

### F5 — Exportação, importação e configurações

| ID | História | Critérios de aceite |
|---|---|---|
| W-23 | Exportação `.pmvault` | Senha de exportação própria; Vault Key nova; `FileSavePicker`; nunca grava em temp/cache; botão Exportar na lista e página própria, com a senha digitada duas vezes (mínimo de 12 caracteres); grava só no local escolhido, com `.tmp` ao lado e sem `.bak`; recusa a pasta de cofres do app; testado no Core, nos ViewModels e no E2E (Debug). |
| W-24 | Exportação CSV | Pede a senha mestra de novo; aviso claro de arquivo legível; colunas `name,url,username,password,notes,folder`; escaping correto (vírgula, aspas, quebra de linha); reconhecimento do aviso antes da senha; senha conferida de verdade (`VaultSession.VerifyPassword`, Argon2id e tag AEAD), com atraso compartilhado com o desbloqueio; link no fim da página de exportação, com a página do CSV acima dela; grava só no local escolhido, sem `.bak`; recusa a pasta de cofres do app; sem mudança de formato; testado no Core, nos ViewModels e no E2E (Debug). |
| W-25 | Importação | `.pmvault` próprio e CSV Bitwarden/KeePass; validação completa; prévia antes de gravar; tratamento de duplicados definido (pular, duplicar ou substituir). Entregue: botão Importar na lista e página própria; `.pmvault` (senha do arquivo conferida por Argon2id e tag AEAD antes de qualquer entrada chegar à prévia) e CSV do próprio app, do Bitwarden, do KeePassXC e do KeePass 1.x (com o dialeto de barra invertida), lidos de UTF-8 estrito; limites de 32 MiB, 20.000 itens e 65.536 caracteres por campo; arquivo malformado recusado inteiro e registro inválido recusado sozinho, com a linha; prévia sem senhas, com as colunas ignoradas (TOTP e campos personalizados não são guardados); duplicado por `Id` ou por título, usuário e URL (ordinal, depois de NFC), com `Skip` como padrão, `Duplicate` e `Replace` (que nunca apaga um campo); uma única gravação atômica com `.bak`, só depois de "Confirmar importação"; o `Import` roda o Argon2id fora da trava da sessão, e o bloqueio não espera por ele; sem mudança de formato e sem pacote novo; testado no Core, nos ViewModels e no E2E (Debug), com o diálogo de abrir real. |
| W-26 | Tela de configurações | Tempo de inatividade, bloquear ao minimizar, trocar senha mestra, gerar novo Kit de Recuperação, exportar/importar. Persistência fora do cofre só para preferências não sensíveis. Entregue: botão Configurações na lista e uma página só, com tudo o que o critério pede; tempo de inatividade (1, 2, 5, 10, 15, 30 ou 60 min, sem "nunca") e bloquear ao minimizar guardados em `settings.json` na pasta de cofres (só esses dois campos, globais, não por perfil, atômico e sem `.bak`; um arquivo ruim vale como o padrão e fica como está até o usuário mudar algo) e aplicados na hora, sem reiniciar; trocar a senha mestra conferindo a atual de verdade (Argon2id e tag AEAD), com o atraso progressivo compartilhado com o desbloqueio, medidor de força informativo e mínimo de 12 caracteres; Kit de Recuperação novo em duas fases (`PrepareRecoveryKit` sem gravar e `CommitRecoveryKit`), mostrado uma vez, gravado só em "Concluir" depois de marcar que foi anotado, de modo que Cancelar, Escape, bloqueio ou fechar o app mantêm o kit antigo valendo; depois de uma troca de senha ou de Kit o `.bak` recebe o arquivo novo (exceção deliberada à AGENTS §3.3, que resolve o `.bak` abrir com a credencial antiga); exportar e importar pelas configurações, com o Voltar de volta a elas, e os botões da lista mantidos; sem mudança de formato, sem vetor novo e sem pacote novo; testado no Core, nos ViewModels e no E2E (Debug). **Não entregue:** nenhuma tela resgata um Kit de Recuperação (`RecoveryKit.Unlock` e `ResetPassword` seguem sem tela e sem história), persistir as opções do gerador e uma página "Sobre". |

### F6 — Empacotamento e distribuição

| ID | História | Critérios de aceite |
|---|---|---|
| W-27 | Ícones e identidade visual | Ícone do app e logos MSIX em todas as escalas exigidas. Arte gerada conforme as regras globais de imagem. Entregue: `Assets/` com os 77 PNGs e o `AppIcon.ico` (14 quadros, de 16 a 256 px), gerados por `windows/art/Build-AppAssets.ps1` a partir de dois mestres, em todas as famílias da lista da Microsoft; o ícone no `.exe` (`ApplicationIcon`) e na janela (`AppWindow.SetIcon`); testado por `Branding/AppAssetsTests` (191) e por `AppIconE2ETests` (20), que leem o `.exe` e a janela de fora do app. **Exceção à regra de imagem, autorizada pelo usuário:** os mestres foram gerados no ComfyUI local (Qwen-Image 2.1), não no Codex. O Codex atingiu o limite de uso várias vezes durante a W-27, e o usuário liberou o ComfyUI local para este projeto; não é cumprimento da regra do Codex (`gpt-6.1-sol`, `high`). Os prompts e os parâmetros estão em [architecture.md](architecture.md) §9.15. **Não entregue:** o `BadgeLogo` (opcional, sem badges) e as escalas 250 e 300 (a lista completa não as cita). O MSIX e o manifesto com os logos ficaram para a W-28 e foram entregues lá. |
| W-28 | Migração para MSIX | `WindowsPackageType` padrão (empacotado); manifesto **sem** `internetClient` nem outra capability de rede; cofre em `ApplicationData.Current.LocalFolder`. Entregue: `Package.appxmanifest` na forma do template do WinUI 3, com a identidade `com.passwordmanagement.windows` / `CN=PasswordManager` / `1.0.0.0` (placeholders da W-30) e os logos da W-27, `TargetDeviceFamily` Windows.Desktop com mínimo `10.0.19041.0` e `pt-BR`, e **só** a capability `runFullTrust`; o csproj deixou `WindowsPackageType=None` e ligou `EnableMsixTooling`, com `AppxSymbolPackageEnabled=false` e um alvo que esvazia o `PDBPayload` (sem o aviso do `mspdbcmf.exe`) e com `IncludePublishItemsOutputGroup` só ligado quando `GenerateAppxPackageOnBuild=true`, para o build Release comum não passar pelo ILLink; sem certificado e sem `GenerateAppxPackageOnBuild`, então o `.msix` sai sem assinatura; `AppPackages/`, `BundleArtifacts/` e os arquivos de pacote no `.gitignore`; o cofre vai para `<ApplicationData.Current.LocalFolder>\vaults`, sem migração da pasta sem pacote, e um switch só de Debug (`--packaged-local-folder`) exercita essa pasta num exe solto, que não tem identidade de pacote; testado por `Packaging/PackageManifestTests` (6, que rodam também em Release) e `Packaging/PackagedVaultFolderE2ETests` (2, Debug), e o `publish` gera o pacote sem assinatura. **Não entregue na W-28:** assinatura e pacote de símbolos (a W-30 criou o processo de assinatura e recusou os símbolos, mas nenhum certificado real foi usado), a identidade/publisher definitivos (a W-30 congelou o `Name`; o `Publisher` segue placeholder até o certificado real), o build self-contained x64/ARM64 com trimming e ReadyToRun e as medidas de pacote e de inicialização (W-29); e a ativação do pacote instalado pelo menu Iniciar, que **não foi executada** porque o Developer Mode está desligado nesta máquina (ponto em aberto, [architecture.md](architecture.md) §13). |
| W-29 | Build de release | Self-contained x64 e ARM64; trimming conforme D5; ReadyToRun; medir tamanho do pacote e tempo de inicialização. Entregue: `tools/Publish-Release.ps1` publica `win-x64` e `win-arm64` self-contained, com ReadyToRun por padrão e o `.msix` sem assinatura por RID (`-p:AppxPackageDir=AppPackages\<rid>\`), confere a máquina PE (`0x8664`/`0xAA64`), o `libsodium.dll` e a existência do `.msix`, e imprime os tamanhos; o trimming ficou **desligado** no App (D5 resolvido no ramo de fallback; o `-Trim` é opt-in que falha hoje), e o ReadyToRun foi provado aplicado (`obj/R2R` com 41 arquivos; `PasswordManager.Core.dll` publicado com 299.008 B contra 125.440 B em IL); o csproj ganhou `_EnsurePublishItemsForOptimization`, que materializa os itens de publicação antes do ILLink/crossgen2 do empacotamento MSIX (sem ele, o snapshot rodava com a lista vazia, o MSBuild gravava "já feito" e o publish saía sem otimização nenhuma); `tools/Measure-Release.ps1` mede o `.msix` e a pasta de publish (sem `*.pdb`, com contagem de arquivos) e a inicialização quente de `Process.Start` até a primeira janela visível (`-Runs` 10, `-Warmup` 2, `-CsvPath` fora do repositório); `Packaging/ReleaseBuildTests` (6, Debug e Release) e `Packaging/PublishedAppSmokeTests` (2, Debug, ignorados sem publish, `PM_PUBLISHED_APP` sobrepõe o caminho) guardam a receita e exercitam o `.exe` Release real; o build Release não roda ILLink e o `packages.lock.json` fica byte a byte igual. Na máquina x64 (SDK 10.0.302): `win-x64` com ReadyToRun publica 291.645.145 B (278,13 MiB, 600 arquivos) e gera um `.msix` de 85.798.237 B (81,82 MiB); sem ReadyToRun, 242.952.089 B (231,70 MiB) e inicialização 187,7 ms de mediana contra 137,2 ms (mín. 129,6, máx. 145,5) com ReadyToRun; `win-arm64` publica 318.850.854 B (304,08 MiB) com `.msix` de 83.138.414 B (79,29 MiB). **Não entregue:** o `win-arm64` foi cross-compilado e empacotado, mas **não executado** (máquina x64); o `.msix` **não foi instalado** (Developer Mode desligado, §13); a inicialização fria não foi medida (exige reiniciar; o script não derruba o cache de arquivos sem elevação — o comando manual está em architecture.md §9.17); e o portão manual de D5 continua aberto — o caminho `-Trim` falha no build hoje, e se o Windows App SDK um dia suportar trimming, a execução manual completa da W-34 decide. |
| W-30 | Assinatura e publicação | Conforme D2/D3; chave fora do repositório; processo de release documentado (versionamento, changelog, hash do pacote). Entregue: `Identity/@Name` congelado em `com.passwordmanagement.windows` (mudá-lo muda o PackageFamilyName e o LocalState), `<Version>1.0.0</Version>` no csproj espelhado por `Identity/@Version="1.0.0.0"`, e `Publisher="CN=PasswordManager"` mantido como placeholder que precisa ser igual ao `Subject` do certificado, byte a byte; `AppxSymbolPackageEnabled=false` mantido (símbolos recusados, sem telemetria/crash reporting e a Store não exige); `windows/tools/New-Release.ps1` (novo, com `-RuntimeIdentifier`, `-CertificatePath`, `-CertificatePassword`, `-TimestampUrl`, `-Unsigned`, `-SkipPublish` e `-Clean`) chama o `Publish-Release.ps1` da W-29 como processo filho, exige a seção `## [<Version>]` do changelog, recusa um certificado dentro do repositório e, sem certificado e sem `-Unsigned`, falha antes do publish nomeando as duas saídas; o certificado é conferido (EKU de code signing, validade, chave privada e `Subject` igual ao `Publisher`, com a linha pronta a colar no manifesto), o `.msix` é assinado por `signtool` (resolvido do `PATH` ou do `microsoft.windows.sdk.buildtools`), `Get-AuthenticodeSignature` tem de dar `Valid` e só então o `.sha256` é gravado no formato `sha256sum`; `.gitignore` ganhou `*.cer`/`*.snk`/`*.key`; `CHANGELOG.md` na raiz (pt-BR, Keep a Changelog, `## [1.0.0] - 2026-11-09`, com o aviso de que não há recuperação de senha sem o Kit de Recuperação); `docs/release-process.md` documenta identidade, versionamento, geração, certificado (OV fora do repositório ou Azure Trusted Signing manual), hash, GitHub Releases e o checklist da Store; `AGENTS.md` §4.2 trocou "sai sem assinatura até a W-30" pela regra do certificado fora do repositório e `PM_CERT_PASSWORD`; testado por `Packaging/PackageIdentityTests` (6), `Packaging/ReleaseSigningTests` (8) e `Packaging/ReleaseDocumentationTests` (6), todos sem janela e também em Release. **Nenhum certificado real foi usado**: a assinatura de verdade e a instalação do pacote assinado numa máquina limpa continuam **não verificadas** (Developer Mode desligado). |
| W-31 | Auditoria de dependências | Lista final de pacotes NuGet revisada contra a seção 7 do AGENTS.md; nenhuma dependência transitiva de rede/telemetria. Entregue: `docs/dependency-audit.md` (pt-BR) como a lista final revisada, lida **offline** de `Directory.Packages.props` e dos seis `packages.lock.json` comitados (nada resolvido pela rede), com os seis pacotes diretos (motivo e veredito), o grafo transitivo do App com 19 nomes e um veredito por pacote — **aceito** para `NSec.Cryptography`/`libsodium` e a família WinUI/Foundation/Runtime; **aceito, não usado** para `Microsoft.WindowsAppSDK.AI`/`.ML`/`.Search`/`.Widgets`, `Microsoft.Windows.AI.MachineLearning` e `System.Numerics.Tensors` (vêm do metapackage e sair deles exigiria abandoná-lo); **aceito, sem uso de rede** para `Microsoft.Web.WebView2`, um controle de navegador que não é hospedado por código nenhum do app; as seções "só de teste" e "só de build" (`Microsoft.NET.ILLink.Tasks`, que entra por `IsTrimmable` e fica fora do lockfile do publish) e o veredito explícito de que nenhum pacote embarcado é de rede, analytics, anúncios ou crash reporting remoto. O achado honesto: `Microsoft.ApplicationInsights` 2.23.0 e `Microsoft.Testing.Extensions.Telemetry` 2.5.1 **existem na solução**, mas só no grafo dos três projetos de teste (via `xunit.v3` → `Microsoft.Testing.Platform`), nunca em App/Core/ViewModels nem no MSIX (o E2E referencia o App com `ReferenceOutputAssembly="false"`). Guarda estática `Packaging/DependencyAuditTests`, **9 testes** sem janela em Debug **e** Release: CPM coerente (toda `PackageReference` com uma `PackageVersion`, sem órfã e sem `Version` inline), a lista de diretos por projeto como mapa auditado, nenhum nome de rede/telemetria no grafo embarcado (com `libsodium` como controle positivo), as duas telemetrias contidas nos lockfiles de teste, os seis lockfiles com `"version": 2` e `contentHash`, o contrato do documento da auditoria, `windows/src` sem `WebView2`/`Microsoft.Windows.AI`, e esta entrega registrada na arquitetura (§9.19) e no épico. Nenhum pacote foi adicionado, removido ou atualizado: `Directory.Packages.props`, os `.csproj` e os seis lockfiles terminaram byte a byte iguais. **Não entregue:** os avisos de licença dos pacotes NuGet embarcados (`THIRD-PARTY-NOTICES.md` só cobre a lista EFF), a troca do metapackage `Microsoft.WindowsAppSDK` pelos pacotes individuais para tirar os componentes de IA/ML sem uso, e a prova de zero tentativas de rede em tempo de execução (W-33; a auditoria é do grafo estático, e segredos ficam com a W-32). |

### F7 — Garantia de qualidade e segurança

| ID | História | Critérios de aceite |
|---|---|---|
| W-32 | Revisão de segurança | Checagem contra `threat-model.md`; busca por logs/`ToString()` com segredos; revisão de zeragem de chaves no `Lock`. |
| W-33 | Teste de "offline de verdade" | App rodando com o firewall registrando conexões: zero tentativas de rede durante o uso completo. |
| W-34 | Roteiro de teste manual | Roteiro em `docs/` cobrindo o fluxo completo, em Windows 10 e 11, x64 e ARM64 (se houver máquina), tema claro/escuro, Narrador, escala 100/150/200%. |
| W-35 | Testes de robustez | Queda de energia simulada durante a gravação (processo morto entre temp e rename) → cofre ou `.bak` abre; disco cheio → erro sem corromper. |

## 6. Ordem e dependências

```
D1 ──► F1 (W-01…W-10) ──► F3, F4, F5
           │
           └─► core Android passa nos vetores ──► formato v1 congelado ──┐
F2 (pode começar junto com F1) ──► F3 ──► F4 ──► F5                       │
D2, D3, D5, D6 ──► F6 ────────────────────────────────────────────────────┤
                                                                         ▼
                                                              F7 ──► Release 1.0
```

- F1 e F2 podem andar em paralelo; F3 em diante precisam de `VaultSession` (W-09).
- W-27 (ícones) pode começar já: é ele que destrava a migração para MSIX (W-28).
- Recomenda-se migrar para MSIX cedo (logo após F3), para que F4–F7 já sejam testadas empacotadas.

## 7. Checklist de release (Definition of Done)

- [ ] Todas as histórias W-01…W-35 concluídas (ou explicitamente adiadas com motivo neste arquivo).
- [ ] `dotnet test --solution windows/PasswordManager.slnx` verde, cobrindo: derivação, wrap/unwrap, cifrar/decifrar, senha errada, adulteração de cabeçalho e de corpo, troca de senha, export/import.
- [ ] Core Android abre todos os vetores de `spec/test-vectors/`; `vault-format.md` marcado como v1 final.
- [ ] MSIX sem `internetClient`; nenhuma conexão observada em W-33.
- [ ] `WDA_EXCLUDEFROMCAPTURE`, limpeza de clipboard e bloqueio automático verificados manualmente.
- [ ] Roteiro W-34 executado e registrado.
- [x] Nenhum segredo, certificado, `.pfx` ou cofre real no repositório.
- [x] Notas de versão em pt-BR, incluindo o aviso de que não há recuperação de senha sem o Kit.

## 8. Riscos

| Risco | Impacto | Mitigação |
|---|---|---|
| Formato precisar mudar logo após a release (ex.: Kit de Recuperação, Windows Hello) | v2 cedo, migração de cofres | D1 decidido em W-01: tabela de slots. |
| Divergência Windows ↔ Android no formato | exportação não abre no outro app | Vetores compartilhados como portão obrigatório; mesma libsodium nas duas pontas. |
| Trimming quebrar WinUI/reflexão | crash só no build de release | D5 decidido na W-29: o App não é trimado (o Windows App SDK/WinRT não é compatível) e o Core mantém `IsTrimmable`; o `.exe` Release é exercitado por um smoke test, não só o Debug. |
| Argon2id lento em máquinas fracas | desbloqueio travando a UI | Rodar fora da thread de UI; medir (medido na W-17, [architecture.md](architecture.md) §4.3: ~40 ms na máquina de desenvolvimento; a linha da máquina modesta está pendente); parâmetros já ficam no cabeçalho para ajuste futuro. |
| Custo/burocracia de assinatura e Store | atraso na publicação | Iniciar D2/D3 cedo, em paralelo com F1. |
| Dependência nova para medidor de força | aumento de superfície | D4 decidido sem dependência (W-16). |
